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

/// <summary>A coherent presentation reference. Treat the contained views as read-only.</summary>
public sealed record RemoteSessionState(long Version, AdmissionMessage Admission, PlayerWorldView? View, Exception? Disconnection)
{
    public bool CanControl => Disconnection is null && !Admission.State.Busy && Admission.State.Phase == AdmissionPhase.Active && View is not null;
}

/// <summary>One immutable render/input sample. Metadata, sampled roots and correlation sources share an authorization capture.</summary>
public sealed record RemotePresentationFrame(RemoteSessionState State, WorldSnapshot? Snapshot, double Tick, ulong ViewEpoch,
    IReadOnlyDictionary<CharacterId, GamePresentationSource> Sources);

/// <summary>
/// One authenticated remote connection; owns transport and replication, never a Simulation.World.
/// Request cancellation stops waiting only: an accepted write may already have executed. Recover durable
/// mutations by their original request identity through lookup/resume; this class never reconnects or retries mutations.
/// </summary>
public sealed class RemoteGameSession : IAsyncDisposable
{
    public ClientLatencyObserver? LatencyObserver { get; } = LatencyTrace.Sink is { } sink ? new(sink) : null;
    readonly NativeGameConnection connection;
    readonly WorldViewCodec codec;
    readonly ClientPresentationEventJournal presentationEvents;
    GameStateReplica? replica;
    BulkTransferReceiver? bulk;
    StagedBulkTransfer? staged;
    ulong sessionEpoch, viewEpoch, commitPublication;
    bool active;
    EntityId? desiredEntry; PlayerViewRequest? desiredObservation;
    ulong entrySentEpoch, observationSentEpoch, entryRetriedEpoch, observationRetriedEpoch;
    WireMessage? bulkBusiness; Guid bulkBusinessQuery;
    double presentationTick;
    readonly RemotePresentationClock presentationClock = new();
    readonly long presentationOrigin = Stopwatch.GetTimestamp();
    public void RegisterPresentationEventConsumer() { lock (gate) presentationEvents.Register(); }
    public PresentationEventBatch ReadPresentationEvents()
    { lock (gate) return state.View is null ? PresentationEventBatch.Empty : presentationEvents.Read(); }
    public void AcknowledgePresentationEvents(ulong highWater) { lock (gate) presentationEvents.Acknowledge(highWater); }
    public IReadOnlyList<GameEntityDeparture> LastDepartures { get { lock (replicaGate) return replica?.LastDepartures ?? []; } }
    public ulong ViewEpoch { get { lock (gate) return viewEpoch; } }
    public QuicTransportStatistics TransportStatistics => connection.Statistics;
    public ulong PresentationClockCorrectionCount { get { lock (gate) return presentationClock.CorrectionCount; } }
    public PresentationClockCorrection? LastPresentationClockCorrection { get { lock (gate) return presentationClock.LastCorrection; } }
    public double PresentationTick { get { lock (gate) return presentationTick; } }
    public WorldSnapshot? PresentationSnapshot => CapturePresentation().Snapshot;
    public RemotePresentationFrame CapturePresentation()
    {
        lock (gate) lock (replicaGate)
        {
            var published = state;
            if (published.View is null || replica is null) return new(published, null, presentationTick, viewEpoch, FrozenDictionary<CharacterId, GamePresentationSource>.Empty);
            UpdatePresentationClock(sampleTimelines: false);
            var sampled = replica.Present(presentationTick) ?? throw new InvalidOperationException("Published domain has no presentation replica.");
            // An active same-view bulk commit is published by TryReady before this lock can be
            // acquired; initial/new-view staging never has a published View. All UI metadata here
            // therefore comes from the same immutable domain reference used for command binding.
            if (!ReferenceEquals(replica.View, published.View)) throw new InvalidOperationException("Presentation domain differs from published authorization state.");
            var sources = new Dictionary<CharacterId, GamePresentationSource>();
            foreach (var character in sampled.Snapshot.Characters)
                if (replica.PresentationSource(character.Id) is { } source) sources[character.Id] = source;
            return new(published, sampled.Snapshot, presentationTick, viewEpoch, sources.ToFrozenDictionary());
        }
    }
    public void AdvancePresentation(double realSeconds)
    {
        if (!double.IsFinite(realSeconds) || realSeconds < 0) throw new ArgumentOutOfRangeException(nameof(realSeconds));
        lock (gate) lock (replicaGate) UpdatePresentationClock();
    }
    void UpdatePresentationClock(bool sampleTimelines = true)
    {
        if (state.View is not { } view || replica is null) return;
        presentationTick = ObserveClock(Math.Max(checked((ulong)view.Snapshot.Tick), replica.LatestServerTick), view.TimeScale,
            view.SimulationStepSeconds);
        // Consume due animation transitions even while GPU/content loading has no renderer calls.
        if (sampleTimelines) replica.AdvancePresentation(presentationTick);
    }
    double ObserveClock(ulong receivedTick, double scale, float step)
    {
        ulong corrections = presentationClock.CorrectionCount;
        double tick = presentationClock.Observe(receivedTick, scale, step, Stopwatch.GetElapsedTime(presentationOrigin));
        if (presentationClock.CorrectionCount != corrections && presentationClock.LastCorrection is { } correction)
            LatencyTrace.Emit(LatencyTrace.Sink, new(LatencyStage.PresentationClockResynchronized, state.Admission.Identity.World,
                state.Admission.Identity.Player.Value, state.Admission.Identity.Generation, Tick: checked((long)receivedTick),
                Revision: presentationClock.CorrectionCount, PresentationTick: correction.After,
                Value: BitConverter.DoubleToInt64Bits(correction.Before), Endpoint: LatencyEndpoint.Client));
        return tick;
    }
    GameStateReplica CreateReplica()
    {
        return new(codec, sessionEpoch, viewEpoch)
        {
            ObservePresentationClock = (receivedTick, scale, step) => presentationTick = ObserveClock(receivedTick, scale, step),
        };
    }
    public void TraceRendererRootSubmission(RemotePresentationFrame frame, CharacterId character, System.Numerics.Vector3 root, long renderFrame)
    {
        lock (gate)
        {
            if (state.View is null || frame.ViewEpoch != viewEpoch || frame.State.View is not { } view || !frame.Sources.TryGetValue(character, out var source)) return;
            presentationClock.MarkPresented();
            var actualSource = view with { Publication = source.Publication, Snapshot = view.Snapshot with { Tick = checked((long)source.Tick) } };
            LatencyObserver?.RendererRootSubmitted(actualSource, character, root, renderFrame, frame.Tick, source.PacketSequence);
        }
    }
    readonly object gate = new();
    readonly object replicaGate = new();
    readonly CancellationTokenSource lifetime = new();
    readonly Channel<WireFrame> outbound = Channel.CreateBounded<WireFrame>(new BoundedChannelOptions(64)
    { SingleReader = true, FullMode = BoundedChannelFullMode.Wait, AllowSynchronousContinuations = false });
    readonly Dictionary<RequestKey, TaskCompletionSource<object>> pending = [];
    readonly ProtocolAcknowledgements transportAcks = new();
    TaskCompletionSource changed = NewSignal();
    Task reader = Task.CompletedTask, writer = Task.CompletedTask, acknowledger = Task.CompletedTask;
    Task? disposal;
    RemoteSessionState state;
    int queuedBytes;
    const int MaximumQueuedBytes = 256 * 1024;
    readonly record struct RequestKey(WireMessage Kind, Guid Query = default, ulong Command = 0);
    static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    RemoteGameSession(NativeGameConnection connection, AdmissionMessage admission, WorldViewCodec codec)
    {
        this.connection = connection; this.codec = codec; presentationEvents = new(codec);
        state = new(1, admission, null, null);
    }

    public static async Task<RemoteGameSession> ConnectAsync(string host, int port, CompatibilityHello hello,
        AuthenticationRequest credential, WorldViewCodec codec, CancellationToken cancellationToken = default, bool deviceIdentity = false,
        QuicGameTransportOptions? transportOptions = null)
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
            if (reply.Kind is not (WireMessage.Compatible or WireMessage.Incompatible)) throw new InvalidDataException("Expected compatibility response.");
            var mismatch = new CompatibilityHandshake(hello).Receive(CompatibilityHandshake.Decode(reply.Payload.Span));
            if (reply.Kind != WireMessage.Compatible || mismatch != CompatibilityFailure.None) throw new InvalidDataException($"Server compatibility rejected: {mismatch}.");
            var authentication = AuthenticationProtocol.Encode(credential);
            transport.SendControl(deviceIdentity ? authentication with { Kind = WireMessage.AuthenticateDevice } : authentication);
            reply = await transport.ReadControlAsync(deadline.Token).ConfigureAwait(false);
            if (reply.Kind == WireMessage.AuthenticationPending)
            {
                if (reply.Payload.Length != 4) throw new InvalidDataException("Invalid authentication persistence stage.");
                uint milliseconds = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(reply.Payload.Span);
                if (milliseconds is 0 or > 180_000) throw new InvalidDataException("Authentication persistence deadline exceeds its bound.");
                deadline.CancelAfter(TimeSpan.FromMilliseconds(milliseconds));
                reply = await transport.ReadControlAsync(deadline.Token).ConfigureAwait(false);
            }
            if (reply.Kind == WireMessage.AuthenticationFailed) throw new AuthenticationException("Server rejected account credentials.");
            if (reply.Kind != WireMessage.Authenticated) throw new InvalidDataException("Expected authenticated admission.");
            var session = new RemoteGameSession(transport, SessionProtocol.ReadAdmission(reply), codec);
            session.writer = Task.Run(session.WriteLoop); session.reader = Task.Run(session.ReadLoop); session.acknowledger = Task.Run(session.AcknowledgeLoop);
            return session;
        }
        catch { if (transport is not null) await transport.DisposeAsync().ConfigureAwait(false); else endpoint.Dispose(); throw; }
    }

    public RemoteSessionState State { get { lock (gate) return state; } }

    /// <summary>Race-free UI notification without running caller callbacks on the receive thread.</summary>
    public async Task<RemoteSessionState> WaitForChangeAsync(long version, CancellationToken cancellationToken = default)
    {
        Task signal;
        lock (gate)
        {
            if (state.Version != version || state.Disconnection is not null) return state;
            signal = changed.Task;
        }
        await signal.WaitAsync(cancellationToken).ConfigureAwait(false);
        return State;
    }

    /// <summary>Queues preparation; observe State for rejection, synchronization, or activation.</summary>
    public void RequestEntry(EntityId role)
    {
        lock (gate) { desiredEntry = role; entrySentEpoch = viewEpoch; Queue(SessionProtocol.Enter(role)); }
    }
    public void RefreshAdmission() => Queue(new(WireMessage.AdmissionQuery, ReadOnlyMemory<byte>.Empty));
    /// <summary>Requests a fresh complete view; ViewBegin withdraws the old view before staging its replacement.</summary>
    public void RequestBaseline() => Queue(new(WireMessage.StateResync, ReadOnlyMemory<byte>.Empty));
    public void Observe(PlayerViewRequest request)
    {
        var frame = SessionProtocol.Observe(request);
        SessionProtocol.ReadObservation(frame); // apply the same bounds before queuing
        lock (gate) { desiredObservation = request with { Zones = request.Zones?.ToArray() }; observationSentEpoch = viewEpoch; Queue(frame); }
    }

    public Task<CommandReceiptMessage> CommandAsync(ulong request, SimCommand command, ExpectedCommandAuthority expected, CancellationToken cancellationToken = default)
    {
        lock (gate)
        {
            EnsureConnected();
            if (!state.CanControl) throw new InvalidOperationException("Control requires active admission and a usable world replica.");
            long started = Stopwatch.GetTimestamp();
            LatencyObserver?.Intent(state.Admission.Identity, request, command, expected, state.View);
            return TraceCommand(Request<CommandReceiptMessage>(new(WireMessage.CommandReceipt, Command: request),
                CommandProtocol.Encode(state.Admission.Identity.World, request, command, expected), cancellationToken),
                started, request, command.GetType().Name);
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

    public Task<CreationReply> CatalogAsync(CreationCatalogQuery query, CancellationToken cancellationToken = default)
        => Request<CreationReply>(new(WireMessage.CreationReply, query.Query), CreationProtocol.Catalog(query), cancellationToken);
    public Task<CreationReply> PreviewAsync(CreationDraftQuery query, CancellationToken cancellationToken = default)
        => Request<CreationReply>(new(WireMessage.CreationReply, query.Query), CreationProtocol.Preview(query), cancellationToken);
    public Task<CreationReply> CommitAsync(CreationDraftQuery query, CancellationToken cancellationToken = default)
        => Request<CreationReply>(new(WireMessage.CreationReply, query.Query), CreationProtocol.Commit(query), cancellationToken);
    public Task<CreationReply> LookupAsync(CreationLookupQuery query, CancellationToken cancellationToken = default)
        => Request<CreationReply>(new(WireMessage.CreationReply, query.Query), CreationProtocol.Lookup(query), cancellationToken);
    public Task<InvitationReply> InvitationAsync(InvitationRequest query, CancellationToken cancellationToken = default)
        => Request<InvitationReply>(new(WireMessage.InvitationReply, query.Query), InvitationProtocol.Request(query), cancellationToken);
    public Task<TalkHintReply> TalkHintAsync(TalkHintQuery query, CancellationToken cancellationToken = default)
        => Request<TalkHintReply>(new(WireMessage.TalkHintReply, query.Query), InteractionProtocol.Query(query), cancellationToken);

    Task<T> Request<T>(RequestKey key, WireFrame frame, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        TaskCompletionSource<object> completion;
        lock (gate)
        {
            EnsureConnected();
            if (pending.Count >= 128) throw new InvalidOperationException("Too many outstanding server requests.");
            completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!pending.TryAdd(key, completion)) throw new InvalidOperationException("Request identity is already outstanding.");
            try { Queue(frame); }
            catch { pending.Remove(key); throw; }
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
        lock (gate)
        {
            EnsureConnected();
            if (frame.Payload.Length > MaximumQueuedBytes - queuedBytes || !outbound.Writer.TryWrite(frame))
                throw new InvalidOperationException("Client send queue is full.");
            queuedBytes += frame.Payload.Length;
            TraceCommandFrame(frame, LatencyStage.ClientEnqueue);
        }
    }
    void EnsureConnected()
    {
        if (state.Disconnection is not null) throw new IOException("Remote session is disconnected.", state.Disconnection);
    }
    void TraceCommandFrame(WireFrame frame, LatencyStage stage)
        => LatencyFrameTrace.Command(frame, state.Admission.Identity, stage);
    void Publish(RemoteSessionState next)
    {
        if ((state.View is null) != (next.View is null) || state.Admission.State.Phase != next.Admission.State.Phase ||
            state.Admission.State.Busy != next.Admission.State.Busy || state.View?.TimeScale != next.View?.TimeScale ||
            (state.Disconnection is null) != (next.Disconnection is null))
            Console.WriteLine($"network   {DateTime.UtcNow:O} session view={(next.View is null ? "unavailable" : "ready")} publication={next.View?.Publication} phase={next.Admission.State.Phase} busy={next.Admission.State.Busy} characters={next.View?.Snapshot.Characters.Count ?? 0} speed={next.View?.TimeScale ?? 0} disconnected={next.Disconnection is not null}");
        state = next with { Version = checked(state.Version + 1) };
        var previous = changed; changed = NewSignal(); previous.TrySetResult();
    }
    void Complete(RequestKey key, object reply)
    {
        if (!pending.Remove(key, out var completion)) throw new InvalidDataException("Unsolicited server reply.");
        completion.TrySetResult(reply);
    }

    async Task ReadLoop()
    {
        try
        {
            while (true)
            {
                var message = await connection.ReadAsync(lifetime.Token).ConfigureAwait(false);
                if (message.Lane != QuicLane.Control)
                {
                    lock (gate) lock (replicaGate)
                    {
                        if (replica is null && bulkBusiness is null) continue; // withdrawn epochs may still have already submitted native bytes
                        bool applied = false;
                        if ((state.Admission.State.Busy || state.Admission.State.Phase == AdmissionPhase.Preparing) &&
                            !(message.Lane == QuicLane.Bulk && bulkBusiness is not null)) continue;
                        if (message.Lane == QuicLane.Bulk)
                        {
                            var chunk = BulkChunkCodec.Decode(message.Payload.Span);
                            if (chunk.Identity.SessionEpoch != sessionEpoch || chunk.Identity.ViewEpoch != viewEpoch) continue;
                            if (bulk is null || bulk.AcceptChunk(chunk) is not (BulkTransferOutcome.Accepted or BulkTransferOutcome.Duplicate)) throw new InvalidDataException("Invalid baseline chunk.");
                            QueueTransportAck(GameViewProtocol.Ack(chunk.Identity, bulk.ContiguousBytes));
                            if (bulk.TryComplete(out var complete) == BulkTransferOutcome.Accepted)
                            {
                                if (bulkBusiness is { } businessKind)
                                {
                                    using (complete)
                                    {
                                        var reply = new WireFrame(businessKind, complete!.Bytes.ToArray());
                                        if (BusinessBulkProtocol.Query(reply, codec) != bulkBusinessQuery) throw new InvalidDataException("Bulk business query mismatch.");
                                        HandleBusinessReply(reply);
                                    }
                                    bulkBusiness = null;
                                }
                                else { staged?.Dispose(); staged = complete; TryReady(); }
                            }
                        }
                        else if (replica is not null && message.Lane == QuicLane.Lifecycle)
                        {
                            if (message.Payload.Span.StartsWith("MTN2"u8))
                            {
                                var checkpoint = MotionDatagramCodec.Decode(message.Payload.Span);
                                if (checkpoint.Header.SessionEpoch != sessionEpoch || checkpoint.Header.ViewEpoch != viewEpoch) continue;
                                if (active) applied = replica.ApplyMotion(checkpoint, checkpoint: true);
                                QueueTransportAck(GameViewProtocol.Epoch(WireMessage.MotionCheckpointAck, sessionEpoch, viewEpoch, checkpoint.Header.PacketSequence));
                            }
                            else applied = replica.Apply(message.Payload.Span);
                        }
                        else if (active && replica is not null) applied = replica.ApplyMotion(MotionDatagramCodec.Decode(message.Payload.Span));
                        if (active) { if (applied) PublishReplica(); else UpdatePresentationClock(); }
                    }
                    continue;
                }
                var frame = connection.DecodeControl(message.Payload.Span);
                lock (gate)
                {
                    EnsureConnected();
                    switch (frame.Kind)
                    {
                        case WireMessage.ViewBegin:
                            var epoch = GameViewProtocol.ReadEpoch(frame);
                            if (sessionEpoch != 0 && (epoch.Session != sessionEpoch || epoch.View <= viewEpoch)) throw new InvalidDataException("Invalid view fence.");
                            sessionEpoch = epoch.Session; viewEpoch = epoch.View; transportAcks.Fence(sessionEpoch, viewEpoch); active = false; presentationTick = 0; presentationClock.Reset(); presentationEvents.Fence(); commitPublication = 0; bulkBusiness = null;
                            lock (replicaGate) { staged?.Dispose(); staged = null; replica = CreateReplica(); bulk = new(sessionEpoch, viewEpoch); }
                            Publish(state with { View = null }); break;
                        case WireMessage.BusinessBulkManifest:
                            var business = BusinessBulkProtocol.ReadManifest(frame);
                            CheckEpoch(business.Manifest.Identity.SessionEpoch, business.Manifest.Identity.ViewEpoch);
                            if (bulk is null || bulk.Begin(business.Manifest) is not (BulkTransferOutcome.Accepted or BulkTransferOutcome.Duplicate)) throw new InvalidDataException("Invalid business bulk manifest.");
                            bulkBusiness = business.Kind; bulkBusinessQuery = business.Query;
                            QueueTransportAck(GameViewProtocol.Epoch(WireMessage.BulkManifestAck, sessionEpoch, viewEpoch, business.Manifest.Identity.BaselineId));
                            break;
                        case WireMessage.BusinessBulkCancelled:
                            var cancelled = BusinessBulkProtocol.ReadCancel(frame);
                            if (pending.Remove(new(cancelled.Kind, cancelled.Query), out var interrupted)) interrupted.TrySetException(new IOException("Business reply was withdrawn by an authorization fence; recover durable outcomes by request identity."));
                            if (bulkBusiness == cancelled.Kind && bulkBusinessQuery == cancelled.Query) { bulk?.Withdraw(); bulkBusiness = null; }
                            break;
                        case WireMessage.BulkManifest:
                            commitPublication = 0; bulkBusiness = null;
                            var manifest = GameViewProtocol.ReadManifest(frame);
                            if (bulk is null || bulk.Begin(manifest) is not (BulkTransferOutcome.Accepted or BulkTransferOutcome.Duplicate)) throw new InvalidDataException("Invalid baseline manifest.");
                            QueueTransportAck(GameViewProtocol.Epoch(WireMessage.BulkManifestAck, sessionEpoch, viewEpoch, manifest.Identity.BaselineId));
                            break;
                        case WireMessage.ViewCommit:
                            var commit = GameViewProtocol.ReadEpoch(frame); CheckEpoch(commit.Session, commit.View);
                            commitPublication = commit.Publication; lock (replicaGate) TryReady(); break;
                        case WireMessage.ViewActive:
                            var activation = GameViewProtocol.ReadEpoch(frame); CheckEpoch(activation.Session, activation.View);
                            if (replica?.View is null || activation.Publication != commitPublication) throw new InvalidDataException("Activation preceded baseline readiness.");
                            active = true; PublishReplica(); break;
                        case WireMessage.Admission:
                            var admission = SessionProtocol.ReadAdmission(frame); CheckIdentity(admission.Identity);
                            if (admission.State.Sequence > state.Admission.State.Sequence)
                            {
                                if (admission.State.Busy || admission.State.Phase == AdmissionPhase.Preparing)
                                {
                                    active = false;
                                    lock (replicaGate)
                                    {
                                        replica = sessionEpoch == 0 ? null : CreateReplica();
                                        staged?.Dispose(); staged = null;
                                        if (bulkBusiness is null) bulk?.Withdraw();
                                    }
                                }
                                if (desiredEntry is { } wanted && (admission.State.Phase == AdmissionPhase.Active ||
                                    admission.State.Roles.FirstOrDefault(role => role.Entity == wanted) is not { } candidate || candidate.Dead || candidate.Availability is PlayerRoleAvailability.Dead or PlayerRoleAvailability.Unavailable)) desiredEntry = null;
                                Publish(state with { Admission = admission, View = VisibleView(admission) });
                            }
                            break;
                        case WireMessage.OperationNotDispatched:
                            var notDispatched = NotDispatchedProtocol.Read(frame);
                            if (notDispatched.Kind == WireMessage.EnterWorld)
                            {
                                if (desiredEntry is { } entry && entry.Value == notDispatched.Request && frame.ViewEpoch == viewEpoch && viewEpoch > entrySentEpoch && entryRetriedEpoch < viewEpoch)
                                { entryRetriedEpoch = entrySentEpoch = viewEpoch; Queue(SessionProtocol.Enter(entry)); }
                            }
                            else if (notDispatched.Kind == WireMessage.Observe)
                            {
                                if (desiredObservation is { } observation && frame.ViewEpoch == viewEpoch && viewEpoch > observationSentEpoch && observationRetriedEpoch < viewEpoch)
                                { observationRetriedEpoch = observationSentEpoch = viewEpoch; Queue(SessionProtocol.Observe(observation)); }
                            }
                            else
                            {
                                var key = new RequestKey(notDispatched.ReplyKind, notDispatched.Query, notDispatched.Request);
                                if (!pending.Remove(key, out var rejected)) throw new InvalidDataException("Unsolicited not-dispatched correlation.");
                                rejected.TrySetException(new RemoteOperationNotDispatchedException());
                                if (notDispatched.Kind == WireMessage.Command) LatencyObserver?.NotDispatched(notDispatched.Request);
                            }
                            break;
                        case WireMessage.CommandReceipt:
                            var receipt = CommandProtocol.DecodeReceipt(frame);
                            if (receipt.World != state.Admission.Identity.World) throw new InvalidDataException("Receipt belongs to another world.");
                            LatencyObserver?.Receipt(state.Admission.Identity, receipt.Receipt);
                            Complete(new(frame.Kind, Command: receipt.Receipt.Request), receipt); break;
                        case WireMessage.CreationReply:
                        case WireMessage.InvitationReply:
                        case WireMessage.TalkHintReply:
                            HandleBusinessReply(frame); break;
                        default: throw new InvalidDataException("Unexpected server message.");
                    }
                }
            }
        }
        catch (Exception error) { Stop(error); }
    }
    void HandleBusinessReply(WireFrame frame)
    {
        switch (frame.Kind)
        {
            case WireMessage.CreationReply:
                var creation = CreationProtocol.ReadReply(frame, codec); CheckIdentity(creation.Identity); Complete(new(frame.Kind, creation.Query), creation); break;
            case WireMessage.InvitationReply:
                var invitation = InvitationProtocol.ReadReply(frame); CheckIdentity(invitation.Identity); Complete(new(frame.Kind, invitation.Query), invitation); break;
            case WireMessage.TalkHintReply:
                var hint = InteractionProtocol.ReadReply(frame); CheckIdentity(hint.Identity); Complete(new(frame.Kind, hint.Query), hint); break;
            default: throw new InvalidDataException("Unexpected business bulk kind.");
        }
    }
    void CheckEpoch(ulong session, ulong view)
    { if (session != sessionEpoch || view != viewEpoch) throw new InvalidDataException("View epoch mismatch."); }
    void TryReady()
    {
        if (commitPublication == 0 || staged is null) return;
        CheckEpoch(staged.Identity.SessionEpoch, staged.Identity.ViewEpoch);
        if (staged.Identity.LifecycleWatermark != commitPublication) throw new InvalidDataException("View commit does not name the staged publication.");
        GameViewProtocol.Unpack(staged.Bytes.Span, bytes => replica!.Apply(bytes.Span));
        staged.Dispose(); staged = null;
        if (replica!.View is not { } committed || committed.Publication < commitPublication) throw new InvalidDataException("Baseline publication mismatch.");
        // Ordinary same-view component deltas are already within an active authorized domain.
        // Publish atomically after checksum/commit validation; only initial/new views await Active.
        if (active) PublishReplica();
        QueueTransportAck(GameViewProtocol.Epoch(WireMessage.ViewReady, sessionEpoch, viewEpoch, commitPublication));
    }
    void PublishReplica()
    {
        var view = replica?.View;
        if (view is not null) { presentationEvents.Observe(view.Events); LatencyObserver?.PublicationApplied(view, id => replica?.LatestMotionSource(id) is { } source ? (source.Publication, checked((long)source.Tick), source.PacketSequence) : null); if (presentationTick == 0) presentationTick = view.Snapshot.Tick; }
        Publish(state with { View = VisibleView(state.Admission) });
        UpdatePresentationClock();
    }

    void CheckIdentity(ReplicationIdentity identity)
    {
        if (identity != state.Admission.Identity) throw new InvalidDataException("Reply belongs to another connection.");
    }

    PlayerWorldView? VisibleView(AdmissionMessage admission)
        => admission.State.Busy || admission.State.Phase == AdmissionPhase.Preparing ? null : active ? replica?.View : null;

    void QueueTransportAck(WireFrame frame)
    { lock (gate) { EnsureConnected(); transportAcks.Enqueue(frame); } }
    async Task AcknowledgeLoop()
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(10));
            while (await timer.WaitForNextTickAsync(lifetime.Token).ConfigureAwait(false))
                lock (gate) transportAcks.Flush(connection.TrySendProtocolControl);
        }
        catch (Exception error) { Stop(error); }
    }
    async Task WriteLoop()
    {
        try
        {
            await foreach (var frame in outbound.Reader.ReadAllAsync(lifetime.Token).ConfigureAwait(false))
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                deadline.CancelAfter(TimeSpan.FromSeconds(10));
                await connection.SendControlAsync(frame).WaitAsync(deadline.Token).ConfigureAwait(false);
                TraceCommandFrame(frame, LatencyStage.ClientWriteComplete);
                lock (gate) queuedBytes -= frame.Payload.Length;
            }
        }
        catch (Exception error) { Stop(error); }
    }

    void Stop(Exception error)
    {
        lock (gate)
        {
            if (state.Disconnection is not null) return;
            LatencyObserver?.Close(); presentationEvents.Fence();
            if (error is not ObjectDisposedException && disposal is null)
                Console.Error.WriteLine($"network   {DateTime.UtcNow:O} disconnected: {error}");
            lock (replicaGate) { replica = null; staged?.Dispose(); staged = null; bulk?.Withdraw(); }
            Publish(state with { View = null, Disconnection = error });
            foreach (var waiter in pending.Values) waiter.TrySetException(new IOException("Remote request interrupted; its server outcome may be unknown.", error));
            pending.Clear();
            outbound.Writer.TryComplete();
            lifetime.Cancel();
            connection.Close();

        }
    }

    public ValueTask DisposeAsync()
    {
        lock (gate) return new(disposal ??= DisposeCoreAsync());
    }

    async Task DisposeCoreAsync()
    {
        Stop(new ObjectDisposedException(nameof(RemoteGameSession)));
        await Task.WhenAll(reader, writer, acknowledger).ConfigureAwait(false);
        try { await connection.DisposeAsync().ConfigureAwait(false); }
        finally
        {
            while (outbound.Reader.TryRead(out _)) { }
            lifetime.Dispose();
        }
    }
}
