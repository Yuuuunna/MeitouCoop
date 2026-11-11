using System.Net;
using System.Collections.Frozen;
using Meitou.Networking.VNext;
using Meitou.Networking.VNext.Transport;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Threading.Channels;
using System.Diagnostics;
using Meitou.Simulation;
using Meitou.Diagnostics;

namespace Meitou.Networking;

/// <summary>
/// A coherent presentation reference. Treat the contained views as read-only.
/// </summary>
public sealed record RemoteSessionState(long Version, AdmissionMessage Admission, PlayerWorldView? View, Exception? Disconnection)
{
    public bool CanControl => Disconnection is null && !Admission.State.Busy && Admission.State.Phase == AdmissionPhase.Active && View is not null;
}

/// <summary>
/// One immutable render/input sample. Metadata, sampled roots and correlation sources share an authorization capture.
/// </summary>
public sealed record RemotePresentationFrame(RemoteSessionState State, WorldSnapshot? Snapshot, double Tick, ulong ViewEpoch, IReadOnlyDictionary<CharacterId, GamePresentationSource> Sources);
/// <summary>
/// One authenticated remote connection; owns transport and replication, never a Simulation.World.
/// Request cancellation stops waiting only: an accepted write may already have executed. Recover durable
/// mutations by their original request identity through lookup/resume; this class never reconnects or retries mutations.
/// </summary>
public sealed class RemoteGameSession : IAsyncDisposable
{
    readonly NativeGameConnection _connection;
    readonly WorldViewCodec _codec;
    readonly ClientPresentationEventJournal _presentationEvents;
    GameStateReplica? _replica;
    BulkTransferReceiver? _bulk;
    StagedBulkTransfer? _staged;
    ulong _sessionEpoch, _viewEpoch, _commitPublication;
    bool _active;
    EntityId? _desiredEntry;
    PlayerViewRequest? _desiredObservation;
    ulong _entrySentEpoch, _observationSentEpoch, _entryRetriedEpoch, _observationRetriedEpoch;
    WireMessage? _bulkBusiness;
    Guid _bulkBusinessQuery;
    double _presentationTick;
    readonly RemotePresentationClock _presentationClock = new();
    readonly long _presentationOrigin = Stopwatch.GetTimestamp();
    readonly object _gate = new();
    readonly object _replicaGate = new();
    readonly CancellationTokenSource _lifetime = new();
    readonly Channel<WireFrame> _outbound = Channel.CreateBounded<WireFrame>(new BoundedChannelOptions(64) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait, AllowSynchronousContinuations = false });
    readonly Dictionary<RequestKey, TaskCompletionSource<object>> _pending = [];
    readonly ProtocolAcknowledgements _transportAcks = new();
    TaskCompletionSource _changed = NewSignal();
    Task _reader = Task.CompletedTask, _writer = Task.CompletedTask, _acknowledger = Task.CompletedTask;
    Task? _disposal;
    RemoteSessionState _state;
    int _queuedBytes;
    const int MaximumQueuedBytes = 256 * 1024;
    RemoteGameSession(NativeGameConnection connection, AdmissionMessage admission, WorldViewCodec codec)
    {
        this._connection = connection;
        this._codec = codec;
        _presentationEvents = new(codec);
        _state = new(1, admission, null, null);
    }

    public ClientLatencyObserver? LatencyObserver { get; } = LatencyTrace.Sink is { } sink ? new(sink) : null;

    public void RegisterPresentationEventConsumer()
    {
        lock (_gate)
            _presentationEvents.Register();
    }

    public PresentationEventBatch ReadPresentationEvents()
    {
        lock (_gate)
            return _state.View is null ? PresentationEventBatch.Empty : _presentationEvents.Read();
    }

    public void AcknowledgePresentationEvents(ulong highWater)
    {
        lock (_gate)
            _presentationEvents.Acknowledge(highWater);
    }

    public IReadOnlyList<GameEntityDeparture> LastDepartures
    {
        get
        {
            lock (_replicaGate)
                return _replica?.LastDepartures ?? [];
        }
    }

    public ulong ViewEpoch
    {
        get
        {
            lock (_gate)
                return _viewEpoch;
        }
    }

    public QuicTransportStatistics TransportStatistics => _connection.Statistics;

    public ulong PresentationClockCorrectionCount
    {
        get
        {
            lock (_gate)
                return _presentationClock.CorrectionCount;
        }
    }

    public PresentationClockCorrection? LastPresentationClockCorrection
    {
        get
        {
            lock (_gate)
                return _presentationClock.LastCorrection;
        }
    }

    public double PresentationTick
    {
        get
        {
            lock (_gate)
                return _presentationTick;
        }
    }

    public WorldSnapshot? PresentationSnapshot => CapturePresentation().Snapshot;

    public RemotePresentationFrame CapturePresentation()
    {
        lock (_gate)
            lock (_replicaGate)
            {
                var published = _state;
                if (published.View is null || _replica is null)
                    return new(published, null, _presentationTick, _viewEpoch, FrozenDictionary<CharacterId, GamePresentationSource>.Empty);
                UpdatePresentationClock(sampleTimelines: false);
                var sampled = _replica.Present(_presentationTick) ?? throw new InvalidOperationException("Published domain has no presentation replica.");
                // An active same-view bulk commit is published by TryReady before this lock can be
                // acquired; initial/new-view staging never has a published View. All UI metadata here
                // therefore comes from the same immutable domain reference used for command binding.
                if (!ReferenceEquals(_replica.View, published.View))
                    throw new InvalidOperationException("Presentation domain differs from published authorization state.");
                var sources = new Dictionary<CharacterId, GamePresentationSource>();
                foreach (var character in sampled.Snapshot.Characters)
                    if (_replica.PresentationSource(character.Id)is { } source)
                        sources[character.Id] = source;
                return new(published, sampled.Snapshot, _presentationTick, _viewEpoch, sources.ToFrozenDictionary());
            }
    }

    public void AdvancePresentation(double realSeconds)
    {
        if (!double.IsFinite(realSeconds) || realSeconds < 0)
            throw new ArgumentOutOfRangeException(nameof(realSeconds));
        lock (_gate)
            lock (_replicaGate)
                UpdatePresentationClock();
    }

    void UpdatePresentationClock(bool sampleTimelines = true)
    {
        if (_state.View is not { } view || _replica is null)
            return;
        _presentationTick = ObserveClock(Math.Max(checked((ulong)view.Snapshot.Tick), _replica.LatestServerTick), view.TimeScale, view.SimulationStepSeconds);
        // Consume due animation transitions even while GPU/content loading has no renderer calls.
        if (sampleTimelines)
            _replica.AdvancePresentation(_presentationTick);
    }

    double ObserveClock(ulong receivedTick, double scale, float step)
    {
        ulong corrections = _presentationClock.CorrectionCount;
        double tick = _presentationClock.Observe(receivedTick, scale, step, Stopwatch.GetElapsedTime(_presentationOrigin));
        if (_presentationClock.CorrectionCount != corrections && _presentationClock.LastCorrection is { } correction)
            LatencyTrace.Emit(LatencyTrace.Sink, new(LatencyStage.PresentationClockResynchronized, _state.Admission.Identity.World, _state.Admission.Identity.Player.Value, _state.Admission.Identity.Generation, Tick: checked((long)receivedTick), Revision: _presentationClock.CorrectionCount, PresentationTick: correction.After, Value: BitConverter.DoubleToInt64Bits(correction.Before), Endpoint: LatencyEndpoint.Client));
        return tick;
    }

    GameStateReplica CreateReplica()
    {
        return new(_codec, _sessionEpoch, _viewEpoch)
        {
            ObservePresentationClock = (receivedTick, scale, step) => _presentationTick = ObserveClock(receivedTick, scale, step),
        };
    }

    public void TraceRendererRootSubmission(RemotePresentationFrame frame, CharacterId character, System.Numerics.Vector3 root, long renderFrame)
    {
        lock (_gate)
        {
            if (_state.View is null || frame.ViewEpoch != _viewEpoch || frame.State.View is not { } view || !frame.Sources.TryGetValue(character, out var source))
                return;
            _presentationClock.MarkPresented();
            var actualSource = view with
            {
                Publication = source.Publication,
                Snapshot = view.Snapshot with
                {
                    Tick = checked((long)source.Tick)
                }
            };
            LatencyObserver?.RendererRootSubmitted(actualSource, character, root, renderFrame, frame.Tick, source.PacketSequence);
        }
    }

    readonly record struct RequestKey(WireMessage Kind, Guid Query = default, ulong Command = 0);
    static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    public static async Task<RemoteGameSession> ConnectAsync(string host, int port, CompatibilityHello hello, AuthenticationRequest credential, WorldViewCodec codec, CancellationToken cancellationToken = default, bool deviceIdentity = false, QuicGameTransportOptions? transportOptions = null)
    {
        ArgumentNullException.ThrowIfNull(codec);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        var settings = transportOptions ?? await QuicDeployment.ClientOptionsAsync(host, port, deadline.Token).ConfigureAwait(false);
        var addresses = await Dns.GetHostAddressesAsync(host, deadline.Token).ConfigureAwait(false);
        var endpoint = new NativeQuicEndpoint(settings.NativeDirectory, settings.ShimSha256, false, settings.CaFile);
        NativeGameConnection? transport = null;
        try
        {
            endpoint.Connect(addresses.First(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork), checked((ushort)port), settings.ServerName ?? host);
            transport = new(endpoint);
            await transport.Connected.WaitAsync(deadline.Token).ConfigureAwait(false);
            transport.SendControl(new(WireMessage.Hello, CompatibilityHandshake.Encode(hello)));
            var reply = await transport.ReadControlAsync(deadline.Token).ConfigureAwait(false);
            if (reply.Kind is not (WireMessage.Compatible or WireMessage.Incompatible))
                throw new InvalidDataException("Expected compatibility response.");
            var mismatch = new CompatibilityHandshake(hello).Receive(CompatibilityHandshake.Decode(reply.Payload.Span));
            if (reply.Kind != WireMessage.Compatible || mismatch != CompatibilityFailure.None)
                throw new InvalidDataException($"Server compatibility rejected: {mismatch}.");
            var authentication = AuthenticationProtocol.Encode(credential);
            transport.SendControl(deviceIdentity ? authentication with { Kind = WireMessage.AuthenticateDevice } : authentication);
            reply = await transport.ReadControlAsync(deadline.Token).ConfigureAwait(false);
            if (reply.Kind == WireMessage.AuthenticationPending)
            {
                if (reply.Payload.Length != 4)
                    throw new InvalidDataException("Invalid authentication persistence stage.");
                uint milliseconds = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(reply.Payload.Span);
                if (milliseconds is 0 or > 180_000)
                    throw new InvalidDataException("Authentication persistence deadline exceeds its bound.");
                deadline.CancelAfter(TimeSpan.FromMilliseconds(milliseconds));
                reply = await transport.ReadControlAsync(deadline.Token).ConfigureAwait(false);
            }

            if (reply.Kind == WireMessage.AuthenticationFailed)
                throw new AuthenticationException("Server rejected account credentials.");
            if (reply.Kind != WireMessage.Authenticated)
                throw new InvalidDataException("Expected authenticated admission.");
            var session = new RemoteGameSession(transport, SessionProtocol.ReadAdmission(reply), codec);
            session._writer = Task.Run(session.WriteLoop);
            session._reader = Task.Run(session.ReadLoop);
            session._acknowledger = Task.Run(session.AcknowledgeLoop);
            return session;
        }
        catch
        {
            if (transport is not null)
                await transport.DisposeAsync().ConfigureAwait(false);
            else
                endpoint.Dispose();
            throw;
        }
    }

    public RemoteSessionState State
    {
        get
        {
            lock (_gate)
                return _state;
        }
    }

    /// <summary>
    /// Race-free UI notification without running caller callbacks on the receive thread.
    /// </summary>
    public async Task<RemoteSessionState> WaitForChangeAsync(long version, CancellationToken cancellationToken = default)
    {
        Task signal;
        lock (_gate)
        {
            if (_state.Version != version || _state.Disconnection is not null)
                return _state;
            signal = _changed.Task;
        }

        await signal.WaitAsync(cancellationToken).ConfigureAwait(false);
        return State;
    }

    /// <summary>
    /// Queues preparation; observe State for rejection, synchronization, or activation.
    /// </summary>
    public void RequestEntry(EntityId role)
    {
        lock (_gate)
        {
            _desiredEntry = role;
            _entrySentEpoch = _viewEpoch;
            Queue(SessionProtocol.Enter(role));
        }
    }

    public void RefreshAdmission() => Queue(new(WireMessage.AdmissionQuery, ReadOnlyMemory<byte>.Empty));
    /// <summary>
    /// Requests a fresh complete view; ViewBegin withdraws the old view before staging its replacement.
    /// </summary>
    public void RequestBaseline() => Queue(new(WireMessage.StateResync, ReadOnlyMemory<byte>.Empty));
    public void Observe(PlayerViewRequest request)
    {
        var frame = SessionProtocol.Observe(request);
        SessionProtocol.ReadObservation(frame); // apply the same bounds before queuing
        lock (_gate)
        {
            _desiredObservation = request with
            {
                Zones = request.Zones?.ToArray()
            };
            _observationSentEpoch = _viewEpoch;
            Queue(frame);
        }
    }

    public Task<CommandReceiptMessage> CommandAsync(ulong request, SimCommand command, ExpectedCommandAuthority expected, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            EnsureConnected();
            if (!_state.CanControl)
                throw new InvalidOperationException("Control requires active admission and a usable world replica.");
            long started = Stopwatch.GetTimestamp();
            LatencyObserver?.Intent(_state.Admission.Identity, request, command, expected, _state.View);
            return TraceCommand(Request<CommandReceiptMessage>(new(WireMessage.CommandReceipt, Command: request), CommandProtocol.Encode(_state.Admission.Identity.World, request, command, expected), cancellationToken), started, request, command.GetType().Name);
        }
    }

    static async Task<CommandReceiptMessage> TraceCommand(Task<CommandReceiptMessage> pending, long started, ulong request, string kind)
    {
        var reply = await pending.ConfigureAwait(false);
        double milliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        if (kind == "MoveOrder" || milliseconds >= 100)
            Console.WriteLine($"network   {DateTime.UtcNow:O} command request={request} kind={kind} receiptMs={milliseconds:F1} status={reply.Receipt.Status} receiptTick={reply.Receipt.Tick}");
        return reply;
    }

    public Task<CreationReply> CatalogAsync(CreationCatalogQuery query, CancellationToken cancellationToken = default) => Request<CreationReply>(new(WireMessage.CreationReply, query.Query), CreationProtocol.Catalog(query), cancellationToken);
    public Task<CreationReply> PreviewAsync(CreationDraftQuery query, CancellationToken cancellationToken = default) => Request<CreationReply>(new(WireMessage.CreationReply, query.Query), CreationProtocol.Preview(query), cancellationToken);
    public Task<CreationReply> CommitAsync(CreationDraftQuery query, CancellationToken cancellationToken = default) => Request<CreationReply>(new(WireMessage.CreationReply, query.Query), CreationProtocol.Commit(query), cancellationToken);
    public Task<CreationReply> LookupAsync(CreationLookupQuery query, CancellationToken cancellationToken = default) => Request<CreationReply>(new(WireMessage.CreationReply, query.Query), CreationProtocol.Lookup(query), cancellationToken);
    public Task<InvitationReply> InvitationAsync(InvitationRequest query, CancellationToken cancellationToken = default) => Request<InvitationReply>(new(WireMessage.InvitationReply, query.Query), InvitationProtocol.Request(query), cancellationToken);
    public Task<TalkHintReply> TalkHintAsync(TalkHintQuery query, CancellationToken cancellationToken = default) => Request<TalkHintReply>(new(WireMessage.TalkHintReply, query.Query), InteractionProtocol.Query(query), cancellationToken);
    Task<T> Request<T>(RequestKey key, WireFrame frame, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        TaskCompletionSource<object> completion;
        lock (_gate)
        {
            EnsureConnected();
            if (_pending.Count >= 128)
                throw new InvalidOperationException("Too many outstanding server requests.");
            completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!_pending.TryAdd(key, completion))
                throw new InvalidOperationException("Request identity is already outstanding.");
            try
            {
                Queue(frame);
            }
            catch
            {
                _pending.Remove(key);
                throw;
            }
        }

        return AwaitReply<T>(completion.Task, cancellationToken);
    }

    static async Task<T> AwaitReply<T>(Task<object> task, CancellationToken token)
    {
        // Observe a later disconnect even when the caller has cancelled its wait.
        _ = task.ContinueWith(t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return (T)await task.WaitAsync(token).ConfigureAwait(false);
    }

    void Queue(WireFrame frame)
    {
        lock (_gate)
        {
            EnsureConnected();
            if (frame.Payload.Length > MaximumQueuedBytes - _queuedBytes || !_outbound.Writer.TryWrite(frame))
                throw new InvalidOperationException("Client send queue is full.");
            _queuedBytes += frame.Payload.Length;
            TraceCommandFrame(frame, LatencyStage.ClientEnqueue);
        }
    }

    void EnsureConnected()
    {
        if (_state.Disconnection is not null)
            throw new IOException("Remote session is disconnected.", _state.Disconnection);
    }

    void TraceCommandFrame(WireFrame frame, LatencyStage stage) => LatencyFrameTrace.Command(frame, _state.Admission.Identity, stage);
    void Publish(RemoteSessionState next)
    {
        if ((_state.View is null) != (next.View is null) || _state.Admission.State.Phase != next.Admission.State.Phase || _state.Admission.State.Busy != next.Admission.State.Busy || _state.View?.TimeScale != next.View?.TimeScale || (_state.Disconnection is null) != (next.Disconnection is null))
            Console.WriteLine($"network   {DateTime.UtcNow:O} session view={(next.View is null ? "unavailable" : "ready")} publication={next.View?.Publication} phase={next.Admission.State.Phase} busy={next.Admission.State.Busy} characters={next.View?.Snapshot.Characters.Count ?? 0} speed={next.View?.TimeScale ?? 0} disconnected={next.Disconnection is not null}");
        _state = next with
        {
            Version = checked(_state.Version + 1)
        };
        var previous = _changed;
        _changed = NewSignal();
        previous.TrySetResult();
    }

    void Complete(RequestKey key, object reply)
    {
        if (!_pending.Remove(key, out var completion))
            throw new InvalidDataException("Unsolicited server reply.");
        completion.TrySetResult(reply);
    }

    async Task ReadLoop()
    {
        try
        {
            while (true)
            {
                var message = await _connection.ReadAsync(_lifetime.Token).ConfigureAwait(false);
                if (message.Lane != QuicLane.Control)
                {
                    lock (_gate)
                        lock (_replicaGate)
                        {
                            if (_replica is null && _bulkBusiness is null)
                                continue; // withdrawn epochs may still have already submitted native bytes
                            bool applied = false;
                            if ((_state.Admission.State.Busy || _state.Admission.State.Phase == AdmissionPhase.Preparing) && !(message.Lane == QuicLane.Bulk && _bulkBusiness is not null))
                                continue;
                            if (message.Lane == QuicLane.Bulk)
                            {
                                var chunk = BulkChunkCodec.Decode(message.Payload.Span);
                                if (chunk.Identity.SessionEpoch != _sessionEpoch || chunk.Identity.ViewEpoch != _viewEpoch)
                                    continue;
                                if (_bulk is null || _bulk.AcceptChunk(chunk)is not (BulkTransferOutcome.Accepted or BulkTransferOutcome.Duplicate))
                                    throw new InvalidDataException("Invalid baseline chunk.");
                                QueueTransportAck(GameViewProtocol.Ack(chunk.Identity, _bulk.ContiguousBytes));
                                if (_bulk.TryComplete(out var complete) == BulkTransferOutcome.Accepted)
                                {
                                    if (_bulkBusiness is { } businessKind)
                                    {
                                        using (complete)
                                        {
                                            var reply = new WireFrame(businessKind, complete!.Bytes.ToArray());
                                            if (BusinessBulkProtocol.Query(reply, _codec) != _bulkBusinessQuery)
                                                throw new InvalidDataException("Bulk business query mismatch.");
                                            HandleBusinessReply(reply);
                                        }

                                        _bulkBusiness = null;
                                    }
                                    else
                                    {
                                        _staged?.Dispose();
                                        _staged = complete;
                                        TryReady();
                                    }
                                }
                            }
                            else if (_replica is not null && message.Lane == QuicLane.Lifecycle)
                            {
                                if (message.Payload.Span.StartsWith("MTN2"u8))
                                {
                                    var checkpoint = MotionDatagramCodec.Decode(message.Payload.Span);
                                    if (checkpoint.Header.SessionEpoch != _sessionEpoch || checkpoint.Header.ViewEpoch != _viewEpoch)
                                        continue;
                                    if (_active)
                                        applied = _replica.ApplyMotion(checkpoint, checkpoint: true);
                                    QueueTransportAck(GameViewProtocol.Epoch(WireMessage.MotionCheckpointAck, _sessionEpoch, _viewEpoch, checkpoint.Header.PacketSequence));
                                }
                                else
                                    applied = _replica.Apply(message.Payload.Span);
                            }
                            else if (_active && _replica is not null)
                                applied = _replica.ApplyMotion(MotionDatagramCodec.Decode(message.Payload.Span));
                            if (_active)
                            {
                                if (applied)
                                    PublishReplica();
                                else
                                    UpdatePresentationClock();
                            }
                        }

                    continue;
                }

                var frame = _connection.DecodeControl(message.Payload.Span);
                lock (_gate)
                {
                    EnsureConnected();
                    switch (frame.Kind)
                    {
                        case WireMessage.ViewBegin:
                            var epoch = GameViewProtocol.ReadEpoch(frame);
                            if (_sessionEpoch != 0 && (epoch.Session != _sessionEpoch || epoch.View <= _viewEpoch))
                                throw new InvalidDataException("Invalid view fence.");
                            _sessionEpoch = epoch.Session;
                            _viewEpoch = epoch.View;
                            _transportAcks.Fence(_sessionEpoch, _viewEpoch);
                            _active = false;
                            _presentationTick = 0;
                            _presentationClock.Reset();
                            _presentationEvents.Fence();
                            _commitPublication = 0;
                            _bulkBusiness = null;
                            lock (_replicaGate)
                            {
                                _staged?.Dispose();
                                _staged = null;
                                _replica = CreateReplica();
                                _bulk = new(_sessionEpoch, _viewEpoch);
                            }

                            Publish(_state with { View = null });
                            break;
                        case WireMessage.BusinessBulkManifest:
                            var business = BusinessBulkProtocol.ReadManifest(frame);
                            CheckEpoch(business.Manifest.Identity.SessionEpoch, business.Manifest.Identity.ViewEpoch);
                            if (_bulk is null || _bulk.Begin(business.Manifest)is not (BulkTransferOutcome.Accepted or BulkTransferOutcome.Duplicate))
                                throw new InvalidDataException("Invalid business bulk manifest.");
                            _bulkBusiness = business.Kind;
                            _bulkBusinessQuery = business.Query;
                            QueueTransportAck(GameViewProtocol.Epoch(WireMessage.BulkManifestAck, _sessionEpoch, _viewEpoch, business.Manifest.Identity.BaselineId));
                            break;
                        case WireMessage.BusinessBulkCancelled:
                            var cancelled = BusinessBulkProtocol.ReadCancel(frame);
                            if (_pending.Remove(new(cancelled.Kind, cancelled.Query), out var interrupted))
                                interrupted.TrySetException(new IOException("Business reply was withdrawn by an authorization fence; recover durable outcomes by request identity."));
                            if (_bulkBusiness == cancelled.Kind && _bulkBusinessQuery == cancelled.Query)
                            {
                                _bulk?.Withdraw();
                                _bulkBusiness = null;
                            }

                            break;
                        case WireMessage.BulkManifest:
                            _commitPublication = 0;
                            _bulkBusiness = null;
                            var manifest = GameViewProtocol.ReadManifest(frame);
                            if (_bulk is null || _bulk.Begin(manifest)is not (BulkTransferOutcome.Accepted or BulkTransferOutcome.Duplicate))
                                throw new InvalidDataException("Invalid baseline manifest.");
                            QueueTransportAck(GameViewProtocol.Epoch(WireMessage.BulkManifestAck, _sessionEpoch, _viewEpoch, manifest.Identity.BaselineId));
                            break;
                        case WireMessage.ViewCommit:
                            var commit = GameViewProtocol.ReadEpoch(frame);
                            CheckEpoch(commit.Session, commit.View);
                            _commitPublication = commit.Publication;
                            lock (_replicaGate)
                                TryReady();
                            break;
                        case WireMessage.ViewActive:
                            var activation = GameViewProtocol.ReadEpoch(frame);
                            CheckEpoch(activation.Session, activation.View);
                            if (_replica?.View is null || activation.Publication != _commitPublication)
                                throw new InvalidDataException("Activation preceded baseline readiness.");
                            _active = true;
                            PublishReplica();
                            break;
                        case WireMessage.Admission:
                            var admission = SessionProtocol.ReadAdmission(frame);
                            CheckIdentity(admission.Identity);
                            if (admission.State.Sequence > _state.Admission.State.Sequence)
                            {
                                if (admission.State.Busy || admission.State.Phase == AdmissionPhase.Preparing)
                                {
                                    _active = false;
                                    lock (_replicaGate)
                                    {
                                        _replica = _sessionEpoch == 0 ? null : CreateReplica();
                                        _staged?.Dispose();
                                        _staged = null;
                                        if (_bulkBusiness is null)
                                            _bulk?.Withdraw();
                                    }
                                }

                                if (_desiredEntry is { } wanted && (admission.State.Phase == AdmissionPhase.Active || admission.State.Roles.FirstOrDefault(role => role.Entity == wanted)is not { } candidate || candidate.Dead || candidate.Availability is PlayerRoleAvailability.Dead or PlayerRoleAvailability.Unavailable))
                                    _desiredEntry = null;
                                Publish(_state with { Admission = admission, View = VisibleView(admission) });
                            }

                            break;
                        case WireMessage.OperationNotDispatched:
                            var notDispatched = NotDispatchedProtocol.Read(frame);
                            if (notDispatched.Kind == WireMessage.EnterWorld)
                            {
                                if (_desiredEntry is { } entry && entry.Value == notDispatched.Request && frame.ViewEpoch == _viewEpoch && _viewEpoch > _entrySentEpoch && _entryRetriedEpoch < _viewEpoch)
                                {
                                    _entryRetriedEpoch = _entrySentEpoch = _viewEpoch;
                                    Queue(SessionProtocol.Enter(entry));
                                }
                            }
                            else if (notDispatched.Kind == WireMessage.Observe)
                            {
                                if (_desiredObservation is { } observation && frame.ViewEpoch == _viewEpoch && _viewEpoch > _observationSentEpoch && _observationRetriedEpoch < _viewEpoch)
                                {
                                    _observationRetriedEpoch = _observationSentEpoch = _viewEpoch;
                                    Queue(SessionProtocol.Observe(observation));
                                }
                            }
                            else
                            {
                                var key = new RequestKey(notDispatched.ReplyKind, notDispatched.Query, notDispatched.Request);
                                if (!_pending.Remove(key, out var rejected))
                                    throw new InvalidDataException("Unsolicited not-dispatched correlation.");
                                rejected.TrySetException(new RemoteOperationNotDispatchedException());
                                if (notDispatched.Kind == WireMessage.Command)
                                    LatencyObserver?.NotDispatched(notDispatched.Request);
                            }

                            break;
                        case WireMessage.CommandReceipt:
                            var receipt = CommandProtocol.DecodeReceipt(frame);
                            if (receipt.World != _state.Admission.Identity.World)
                                throw new InvalidDataException("Receipt belongs to another world.");
                            LatencyObserver?.Receipt(_state.Admission.Identity, receipt.Receipt);
                            Complete(new(frame.Kind, Command: receipt.Receipt.Request), receipt);
                            break;
                        case WireMessage.CreationReply:
                        case WireMessage.InvitationReply:
                        case WireMessage.TalkHintReply:
                            HandleBusinessReply(frame);
                            break;
                        default:
                            throw new InvalidDataException("Unexpected server message.");
                    }
                }
            }
        }
        catch (Exception error)
        {
            Stop(error);
        }
    }

    void HandleBusinessReply(WireFrame frame)
    {
        switch (frame.Kind)
        {
            case WireMessage.CreationReply:
                var creation = CreationProtocol.ReadReply(frame, _codec);
                CheckIdentity(creation.Identity);
                Complete(new(frame.Kind, creation.Query), creation);
                break;
            case WireMessage.InvitationReply:
                var invitation = InvitationProtocol.ReadReply(frame);
                CheckIdentity(invitation.Identity);
                Complete(new(frame.Kind, invitation.Query), invitation);
                break;
            case WireMessage.TalkHintReply:
                var hint = InteractionProtocol.ReadReply(frame);
                CheckIdentity(hint.Identity);
                Complete(new(frame.Kind, hint.Query), hint);
                break;
            default:
                throw new InvalidDataException("Unexpected business bulk kind.");
        }
    }

    void CheckEpoch(ulong session, ulong view)
    {
        if (session != _sessionEpoch || view != _viewEpoch)
            throw new InvalidDataException("View epoch mismatch.");
    }

    void TryReady()
    {
        if (_commitPublication == 0 || _staged is null)
            return;
        CheckEpoch(_staged.Identity.SessionEpoch, _staged.Identity.ViewEpoch);
        if (_staged.Identity.LifecycleWatermark != _commitPublication)
            throw new InvalidDataException("View commit does not name the staged publication.");
        GameViewProtocol.Unpack(_staged.Bytes.Span, bytes => _replica!.Apply(bytes.Span));
        _staged.Dispose();
        _staged = null;
        if (_replica!.View is not { } committed || committed.Publication < _commitPublication)
            throw new InvalidDataException("Baseline publication mismatch.");
        // Ordinary same-view component deltas are already within an active authorized domain.
        // Publish atomically after checksum/commit validation; only initial/new views await Active.
        if (_active)
            PublishReplica();
        QueueTransportAck(GameViewProtocol.Epoch(WireMessage.ViewReady, _sessionEpoch, _viewEpoch, _commitPublication));
    }

    void PublishReplica()
    {
        var view = _replica?.View;
        if (view is not null)
        {
            _presentationEvents.Observe(view.Events);
            LatencyObserver?.PublicationApplied(view, id => _replica?.LatestMotionSource(id)is { } source ? (source.Publication, checked((long)source.Tick), source.PacketSequence) : null);
            if (_presentationTick == 0)
                _presentationTick = view.Snapshot.Tick;
        }

        Publish(_state with { View = VisibleView(_state.Admission) });
        UpdatePresentationClock();
    }

    void CheckIdentity(ReplicationIdentity identity)
    {
        if (identity != _state.Admission.Identity)
            throw new InvalidDataException("Reply belongs to another connection.");
    }

    PlayerWorldView? VisibleView(AdmissionMessage admission) => admission.State.Busy || admission.State.Phase == AdmissionPhase.Preparing ? null : _active ? _replica?.View : null;
    void QueueTransportAck(WireFrame frame)
    {
        lock (_gate)
        {
            EnsureConnected();
            _transportAcks.Enqueue(frame);
        }
    }

    async Task AcknowledgeLoop()
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(10));
            while (await timer.WaitForNextTickAsync(_lifetime.Token).ConfigureAwait(false))
                lock (_gate)
                    _transportAcks.Flush(_connection.TrySendProtocolControl);
        }
        catch (Exception error)
        {
            Stop(error);
        }
    }

    async Task WriteLoop()
    {
        try
        {
            await foreach (var frame in _outbound.Reader.ReadAllAsync(_lifetime.Token).ConfigureAwait(false))
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
                deadline.CancelAfter(TimeSpan.FromSeconds(10));
                await _connection.SendControlAsync(frame).WaitAsync(deadline.Token).ConfigureAwait(false);
                TraceCommandFrame(frame, LatencyStage.ClientWriteComplete);
                lock (_gate)
                    _queuedBytes -= frame.Payload.Length;
            }
        }
        catch (Exception error)
        {
            Stop(error);
        }
    }

    void Stop(Exception error)
    {
        lock (_gate)
        {
            if (_state.Disconnection is not null)
                return;
            LatencyObserver?.Close();
            _presentationEvents.Fence();
            if (error is not ObjectDisposedException && _disposal is null)
                Console.Error.WriteLine($"network   {DateTime.UtcNow:O} disconnected: {error}");
            lock (_replicaGate)
            {
                _replica = null;
                _staged?.Dispose();
                _staged = null;
                _bulk?.Withdraw();
            }

            Publish(_state with { View = null, Disconnection = error });
            foreach (var waiter in _pending.Values)
                waiter.TrySetException(new IOException("Remote request interrupted; its server outcome may be unknown.", error));
            _pending.Clear();
            _outbound.Writer.TryComplete();
            _lifetime.Cancel();
            _connection.Close();
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_gate)
            return new(_disposal ??= DisposeCoreAsync());
    }

    async Task DisposeCoreAsync()
    {
        Stop(new ObjectDisposedException(nameof(RemoteGameSession)));
        await Task.WhenAll(_reader, _writer, _acknowledger).ConfigureAwait(false);
        try
        {
            await _connection.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            while (_outbound.Reader.TryRead(out _))
            {
            }

            _lifetime.Dispose();
        }
    }
}