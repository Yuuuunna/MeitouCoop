using Meitou.Simulation.Bodies;
using Meitou.Networking.VNext;
using Meitou.Networking.VNext.Transport;
using System.Diagnostics;
using Meitou.Diagnostics;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Channels;
using Meitou.Networking;
using Meitou.Simulation;

namespace Meitou.Server;
public sealed record ServerPeerOptions
{
    public TimeSpan HandshakeTimeout { get; init; } = TimeSpan.FromSeconds(15);
    public TimeSpan AuthenticationPersistenceTimeout { get; init; } = TimeSpan.FromMinutes(3);
    public TimeSpan WriteTimeout { get; init; } = TimeSpan.FromSeconds(10);
    public TimeSpan PublicationInterval { get; init; } = TimeSpan.FromMilliseconds(100);
    public TimeSpan PreparationTimeout { get; init; } = TimeSpan.FromMinutes(3);
    public int MessagesPerSecond { get; init; } = 120;
    public int BytesPerSecond { get; init; } = 2 * 1024 * 1024;
    public PlayerObservationRules Observation { get; init; } = new();
    public Action<GameStatePublication>? PublicationObserved { get; init; }
    public Action<AnimationExportDiagnostics>? AnimationObserved { get; init; }
    public Action<AccountBindingPersistenceDiagnostics>? BindingPersistenceObserved { get; init; }
}

/// <summary>
/// One TLS connection. Preparation is a host-supplied region loader; all world access goes through the shared loop.
/// </summary>
public sealed class SecureServerPeer
{
    readonly ServerWorldLoop _loop;
    readonly ServerAccountSessions _sessions;
    readonly CompatibilityHello _hello;
    readonly WorldViewCodec _codec;
    readonly Func<AuthorityConnection, EntityId, CancellationToken, Task<bool>> _prepareEntry;
    readonly ServerPeerOptions _options;
    readonly ServerCreationEndpoint? _creation;
    readonly ServerInvitationEndpoint? _invitations;
    readonly ServerInformation? _information;
    public SecureServerPeer(ServerWorldLoop loop, ServerAccountSessions sessions, CompatibilityHello hello, WorldViewCodec codec, Func<AuthorityConnection, EntityId, CancellationToken, Task<bool>> prepareEntry, ServerPeerOptions? options = null, ServerCreationEndpoint? creation = null, ServerInvitationEndpoint? invitations = null, ServerInformation? information = null)
    {
        this._loop = loop;
        this._sessions = sessions;
        this._hello = hello;
        this._codec = codec;
        this._prepareEntry = prepareEntry;
        this._options = options ?? new();
        this._creation = creation;
        this._invitations = invitations;
        this._information = information;
        if (information is not null)
        {
            if (information.Hello != hello)
                throw new ArgumentException("Discovery must describe the active server.", nameof(information));
            _ = ServerDiscovery.Encode(information);
        }

        if (this._options.AuthenticationPersistenceTimeout <= TimeSpan.Zero || this._options.AuthenticationPersistenceTimeout > TimeSpan.FromMinutes(3) || this._options.HandshakeTimeout <= TimeSpan.Zero || this._options.WriteTimeout <= TimeSpan.Zero || this._options.PublicationInterval < TimeSpan.FromMilliseconds(10) || this._options.PreparationTimeout <= TimeSpan.Zero || this._options.MessagesPerSecond <= 0 || this._options.BytesPerSecond < WireFrame.MaximumPayload)
            throw new ArgumentOutOfRangeException(nameof(options));
    }

    internal CancellationToken Stopping => _loop.Stopping;

    public async Task RunAsync(NativeQuicEndpoint endpoint, CancellationToken cancellation = default)
    {
        await using (var transport = new NativeGameConnection(endpoint))
        using (var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellation, _loop.Stopping))
        using (var writes = new SemaphoreSlim(1))
        using (var handshake = CancellationTokenSource.CreateLinkedTokenSource(stop.Token))
        {
            handshake.CancelAfter(_options.HandshakeTimeout);
            AuthoritativeCommandChannel? commands = null;
            AuthenticatedPlayerSession? session = null;
            World? source = null;
            Task? publishing = null;
            Task? reading = null;
            Task? acknowledging = null;
            var replies = new HashSet<Task>();
            Func<WireFrame, CancellationToken, Task>? sendLarge = null;
            try
            {
                var first = await transport.ReadControlAsync(handshake.Token).ConfigureAwait(false);
                if (first.Kind == WireMessage.ServerInformationQuery)
                {
                    if (!first.Payload.IsEmpty || _information is null)
                        return;
                    await Send([ServerDiscovery.Encode(_information)], handshake.Token).ConfigureAwait(false);
                    return;
                }

                commands = await _loop.InvokeAsync(() => new AuthoritativeCommandChannel(_hello, _loop.Runtime.World.Authority), handshake.Token).ConfigureAwait(false);
                var compatible = commands.ReceiveHello(first);
                await Send([new(compatible == CompatibilityFailure.None ? WireMessage.Compatible : WireMessage.Incompatible, CompatibilityHandshake.Encode(_hello))], handshake.Token).ConfigureAwait(false);
                if (compatible != CompatibilityFailure.None)
                {
                    await AwaitPeerClose().ConfigureAwait(false);
                    return;
                }

                var authentication = await transport.ReadControlAsync(handshake.Token).ConfigureAwait(false);
                bool device = authentication.Kind == WireMessage.AuthenticateDevice;
                AuthenticationRequest? request = AuthenticationProtocol.Decode(device ? authentication with { Kind = WireMessage.Authenticate } : authentication);
                source = _loop.Runtime.World;
                session = await _sessions.OpenAsync(_loop, request!, device && _information?.DeviceRegistration == true, async () =>
                {
                    handshake.CancelAfter(_options.AuthenticationPersistenceTimeout);
                    byte[] pending = new byte[4];
                    System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(pending, checked((uint)_options.AuthenticationPersistenceTimeout.TotalMilliseconds));
                    await transport.SendControlAsync(new(WireMessage.AuthenticationPending, pending)).WaitAsync(handshake.Token).ConfigureAwait(false);
                }, handshake.Token, _options.BindingPersistenceObserved).ConfigureAwait(false);
                request = null;
                source = session?.SourceWorld ?? source;
                handshake.Token.ThrowIfCancellationRequested();
                if (session is null)
                {
                    await transport.SendControlAsync(new(WireMessage.AuthenticationFailed, ReadOnlyMemory<byte>.Empty)).WaitAsync(handshake.Token).ConfigureAwait(false);
                    await AwaitPeerClose().ConfigureAwait(false);
                    return;
                }

                commands.BindAuthenticated(session.Connection);
                ulong sessionEpoch = System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(System.Security.Cryptography.RandomNumberGenerator.GetBytes(8)) | 1;
                ulong viewEpoch = 1, baselinePublication = 0, bulkBaselineId = 0, pinnedAuthorizationPublication = 0;
                GameStatePublisher? publisher = null;
                BulkTransferSender? bulkSender = null;
                BulkChunk? pendingBulkChunk = null;
                MotionReplicationProducer? motion = null;
                var animations = new AuthorityAnimationExporter(_options.AnimationObserved is not null, phaseTolerance: .02, weightTolerance: .015, rateTolerance: .015, targetWeightTolerance: .01);
                var sentCheckpoints = new HashSet<ulong>();
                bool viewActive = false;
                bool viewInitialized = false;
                bool manifestAcknowledged = false;
                ulong begunView = 0;
                var playback = new Dictionary<ulong, AnimationPlaybackState>();
                var entityMap = new Dictionary<CharacterId, ulong>();
                var componentCharacters = new Dictionary<CharacterId, ulong>();
                var retainedEvents = new PendingPresentationEvents(_codec);
                (IReadOnlyList<GameStatePublication> Batches, LatencyTraceEvent Trace, ulong Events, int Index)? pendingCritical = null;
                LatencyTraceEvent bulkTrace = default;
                int bulkTotalBytes = 0;
                transport.StateSendObserved = (payload, released) =>
                {
                    if (LatencyTrace.Sink is null)
                        return;
                    var packet = MotionDatagramCodec.Decode(payload.Span);
                    var trace = new LatencyTraceEvent(released ? LatencyStage.MotionWriteComplete : LatencyStage.MotionWriteStarted, session.Connection.World, session.Connection.Player.Value, session.Connection.Generation, Publication: packet.Header.AuthorityPublication, Tick: checked((long)packet.Header.ServerTick), Value: unchecked((long)packet.Header.PacketSequence));
                    foreach (var record in packet.Records)
                        LatencyTrace.Emit(LatencyTrace.Sink, trace with { Entity = record.EntityId });
                };
                var replicationGate = new SemaphoreSlim(1);
                var bulkQueue = new Queue<(byte[] Bytes, WireMessage? Business, Guid Query, ulong Publication, ulong Tick, LatencyTraceEvent Trace)>();
                WireMessage? activeBusiness = null;
                Guid activeBusinessQuery = default;
                int queuedBulkBytes = 0;
                ulong transferSequence = 0;
                sendLarge = async (frame, token) =>
                {
                    var query = BusinessBulkProtocol.Query(frame, _codec);
                    await replicationGate.WaitAsync(token).ConfigureAwait(false);
                    try
                    {
                        if (begunView != viewEpoch)
                            BeginView();
                        QueueBulk(frame.Payload.ToArray(), frame.Kind, query, 0, 0, default);
                    }
                    finally
                    {
                        replicationGate.Release();
                    }
                };
                transport.SetControlEpoch(sessionEpoch, viewEpoch);
                PlayerViewRequest observation = new();
                await Send([await Status(authenticated: true).ConfigureAwait(false)], stop.Token).ConfigureAwait(false);
                var incoming = Channel.CreateBounded<WireFrame>(new BoundedChannelOptions(64) { SingleReader = true, SingleWriter = true });
                long queuedBytes = 0;
                var ackGate = new object ();
                var coalescedAcks = new Dictionary<(WireMessage Kind, ulong Session, ulong View, ulong Id), WireFrame>();
                acknowledging = AcknowledgeInputs();
                reading = ReadInputs();
                publishing = Publish();
                await foreach (var frame in incoming.Reader.ReadAllAsync(stop.Token).ConfigureAwait(false))
                {
                    Interlocked.Add(ref queuedBytes, -frame.Payload.Length - 12);
                    LatencyFrameTrace.Command(frame, new(session.Connection.World, session.Connection.Player, session.Connection.Generation), Meitou.Diagnostics.LatencyStage.ServerBusinessDequeued);
                    if (!await _loop.InvokeAsync(() => _sessions.IsCurrent(session), stop.Token).ConfigureAwait(false))
                        break;
                    if (frame.SessionEpoch != sessionEpoch || frame.ViewEpoch > viewEpoch)
                        throw new InvalidDataException("Operation belongs to another control epoch.");
                    if (frame.ViewEpoch < viewEpoch)
                    {
                        if (frame.Kind is WireMessage.AdmissionQuery)
                        {
                            await Send([await Status().ConfigureAwait(false)], stop.Token).ConfigureAwait(false);
                            continue;
                        }

                        if (frame.Kind is WireMessage.StateResync or WireMessage.ViewReady)
                            continue;
                        transport.SendControl(NotDispatchedProtocol.Reply(frame));
                        if (frame.Kind is WireMessage.EnterWorld or WireMessage.Observe)
                            await Send([await Status().ConfigureAwait(false)], stop.Token).ConfigureAwait(false);
                        continue;
                    }

                    switch (frame.Kind)
                    {
                        case WireMessage.TalkHintQuery:
                            var query = InteractionProtocol.ReadQuery(frame);
                            var hint = await _loop.InvokeAsync(() => source.Authority.QueryTalk(session.Connection, query.Speaker, query.Target, new(session.Connection.Player, query.Grant.Entity, query.Grant.Faction, query.Grant.Revision), query.MembershipRevision, observation, _options.Observation), stop.Token).ConfigureAwait(false);
                            await Send([InteractionProtocol.Reply(new(new(session.Connection.World, session.Connection.Player, session.Connection.Generation), query.Query, query.Speaker, query.Target, query.Grant.Revision, query.MembershipRevision, hint))], stop.Token).ConfigureAwait(false);
                            break;
                        case WireMessage.InvitationRequest:
                            var invitation = InvitationProtocol.ReadRequest(frame);
                            var invitationReply = _invitations is null ? InvitationProtocol.Reply(new(new(session.Connection.World, session.Connection.Player, session.Connection.Generation), invitation.Query, InvitationOutcome.Rejected, [], default, [])) : await _invitations.HandleAsync(session.Connection, frame, stop.Token).ConfigureAwait(false);
                            await Send([invitationReply, await Status().ConfigureAwait(false)], stop.Token).ConfigureAwait(false);
                            break;
                        case WireMessage.CreationCatalogQuery:
                        case WireMessage.CreationPreview:
                        case WireMessage.CreationCommit:
                        case WireMessage.CreationLookup:
                            using (var preparing = CancellationTokenSource.CreateLinkedTokenSource(stop.Token))
                            {
                                preparing.CancelAfter(_options.PreparationTimeout);
                                var reply = _creation is null ? CreationProtocol.Reply(CreationProtocol.Unavailable(new(session.Connection.World, session.Connection.Player, session.Connection.Generation), frame), _codec) : await _creation.HandleAsync(session.Connection, frame, preparing.Token).ConfigureAwait(false);
                                await Send([reply, await Status().ConfigureAwait(false)], stop.Token).ConfigureAwait(false);
                            }

                            break;
                        case WireMessage.Command:
                            LatencyFrameTrace.Command(frame, new(session.Connection.World, session.Connection.Player, session.Connection.Generation), Meitou.Diagnostics.LatencyStage.ServerDispatch);
                            foreach (var completed in replies.Where(t => t.IsCompleted).ToArray())
                            {
                                await completed.ConfigureAwait(false);
                                replies.Remove(completed);
                            }

                            if (replies.Count >= 64)
                                throw new InvalidDataException("Too many pending command responses.");
                            replies.Add(Reply(commands.ReceiveCommand(frame)));
                            break;
                        case WireMessage.EnterWorld:
                            var entry = SessionProtocol.ReadEnter(frame);
                            bool allowed = await _loop.InvokeAsync(() => CanPrepare(entry.FocusRole), stop.Token).ConfigureAwait(false);
                            if (allowed)
                            {
                                using var preparing = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
                                preparing.CancelAfter(_options.PreparationTimeout);
                                if (await _prepareEntry(session.Connection, entry.FocusRole, preparing.Token).ConfigureAwait(false))
                                    await _loop.InvokeAsync(() =>
                                    {
                                        if (CanEnter(entry.FocusRole))
                                            source.Authority.BeginSynchronization(session.Connection);
                                    }, stop.Token).ConfigureAwait(false);
                            }

                            await Send([await Status().ConfigureAwait(false)], stop.Token).ConfigureAwait(false);
                            break;
                        case WireMessage.BulkManifestAck:
                        case WireMessage.BulkAck:
                            await HandleTransportAck(frame).ConfigureAwait(false);
                            break;
                        case WireMessage.ViewReady:
                            await HandleTransportAck(frame).ConfigureAwait(false);
                            break;
                        case WireMessage.MotionCheckpointAck:
                            await HandleTransportAck(frame).ConfigureAwait(false);
                            break;
                        case WireMessage.StateResync:
                            await replicationGate.WaitAsync(stop.Token).ConfigureAwait(false);
                            try
                            {
                                publisher = null;
                                viewActive = false;
                                viewEpoch = checked(viewEpoch + 1);
                                BeginView();
                            }
                            finally
                            {
                                replicationGate.Release();
                            }

                            break;
                        case WireMessage.Observe:
                            var requested = SessionProtocol.ReadObservation(frame);
                            await _loop.InvokeAsync(() => observation = requested, stop.Token).ConfigureAwait(false);
                            break;
                        case WireMessage.AdmissionQuery:
                            if (!frame.Payload.IsEmpty)
                                throw new InvalidDataException("Admission query must be empty.");
                            await Send([await Status().ConfigureAwait(false)], stop.Token).ConfigureAwait(false);
                            break;
                        default:
                            throw new InvalidDataException("Unexpected authenticated message.");
                    }
                }

                bool CanPrepare(EntityId role) => ReferenceEquals(source, _loop.Runtime.World) && source.Authority.GetPhase(session.Connection) == AdmissionPhase.Preparing && !source.HasPendingDurableMutation && source.Players.KnownRoles(session.Connection.Player).Any(g => g.Entity == role && source.Players.Find(session.Connection.Player)!.IsMember(g.Faction));
                bool CanEnter(EntityId role) => CanPrepare(role) && source.Entities.TryResolve(role, out var character) && source.Players.CanControl(session.Connection.Player, character);
                Task<WireFrame> Status(bool authenticated = false) => _loop.InvokeAsync(() => SessionProtocol.Admission(session.Connection, source.Authority.ReadAdmission(session.Connection) ?? throw new IOException("Connection was replaced."), authenticated), stop.Token);
                async Task Reply(Task<AuthorityReceipt> pending)
                {
                    try
                    {
                        var receipt = await pending.WaitAsync(stop.Token).ConfigureAwait(false);
                        await Send([CommandProtocol.EncodeReceipt(session.Connection.World, receipt)], stop.Token).ConfigureAwait(false);
                    }
                    catch
                    {
                        stop.Cancel();
                        throw;
                    }
                }

                async Task HandleTransportAck(WireFrame frame)
                {
                    await replicationGate.WaitAsync(stop.Token).ConfigureAwait(false);
                    try
                    {
                        var epoch = frame.Kind == WireMessage.BulkAck ? (Session: GameViewProtocol.ReadAck(frame).Identity.SessionEpoch, View: GameViewProtocol.ReadAck(frame).Identity.ViewEpoch) : (Session: GameViewProtocol.ReadEpoch(frame).Session, View: GameViewProtocol.ReadEpoch(frame).View);
                        if (epoch.Session != sessionEpoch || epoch.View > viewEpoch)
                            throw new InvalidDataException("Protocol acknowledgement belongs to another epoch.");
                        if (epoch.View < viewEpoch)
                            return;
                        if (frame.Kind == WireMessage.ViewReady)
                        {
                            var ready = GameViewProtocol.ReadEpoch(frame);
                            if (ready.Session == sessionEpoch && (ready.View < viewEpoch || ready.View == viewEpoch && ready.Publication < baselinePublication))
                                return;
                            if (ready.Session != sessionEpoch || ready.View != viewEpoch || ready.Publication != baselinePublication)
                                throw new InvalidDataException("Invalid view readiness.");
                            if (viewActive)
                                return; // Duplicate Ready cannot enqueue repeated owner checks.
                            bool activated = await _loop.InvokeAsync(() => source.Authority.CanRetainPublishedView(session.Connection, baselinePublication) && (source.Authority.GetPhase(session.Connection) == AdmissionPhase.Active || source.Authority.ActivateAfterView(session.Connection, baselinePublication)), stop.Token).ConfigureAwait(false);
                            if (!activated)
                            {
                                publisher = null;
                                viewEpoch = checked(viewEpoch + 1);
                                BeginView();
                                return;
                            }

                            viewActive = true;
                            viewInitialized = true;
                            ReleaseCompletedPin();
                            await Send([GameViewProtocol.Epoch(WireMessage.ViewActive, sessionEpoch, viewEpoch, baselinePublication), await Status().ConfigureAwait(false)], stop.Token).ConfigureAwait(false);
                        }
                        else if (frame.Kind == WireMessage.BulkManifestAck)
                        {
                            var ack = GameViewProtocol.ReadEpoch(frame);
                            if (ack.Session != sessionEpoch || ack.View != viewEpoch)
                                return;
                            if (ack.Publication != bulkBaselineId)
                                throw new InvalidDataException("Invalid manifest ACK.");
                            manifestAcknowledged = true;
                            await PumpAuthorizedBulk().ConfigureAwait(false);
                        }
                        else if (frame.Kind == WireMessage.BulkAck)
                        {
                            var ack = GameViewProtocol.ReadAck(frame);
                            if (ack.Identity.SessionEpoch != sessionEpoch || ack.Identity.ViewEpoch != viewEpoch)
                                return;
                            if (ack.Identity.BaselineId < bulkBaselineId)
                                return;
                            if (bulkSender is null || bulkSender.Acknowledge(ack.Identity, ack.Offset)is not (BulkTransferOutcome.Accepted or BulkTransferOutcome.Duplicate))
                                throw new InvalidDataException("Invalid baseline ACK.");
                            await PumpAuthorizedBulk().ConfigureAwait(false);
                            if (bulkSender.Finish() == BulkTransferOutcome.Accepted)
                            {
                                activeBusiness = null;
                                bulkTotalBytes = 0;
                                ReleaseCompletedPin();
                                StartNextBulk();
                            }
                        }
                        else
                        {
                            var ack = GameViewProtocol.ReadEpoch(frame);
                            ulong id = ack.Publication & ~(1UL << 63);
                            if (motion is not null && ack.Session == sessionEpoch && ack.View == viewEpoch)
                            {
                                transport.WithScheduler(_ => motion.AcknowledgeCheckpointsThrough(ack.Session, ack.View, id, sentCheckpoints));
                                sentCheckpoints.RemoveWhere(submitted => submitted <= id);
                            }
                        }
                    }
                    finally
                    {
                        replicationGate.Release();
                    }
                }

                async Task AcknowledgeInputs()
                {
                    try
                    {
                        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(5));
                        while (await timer.WaitForNextTickAsync(stop.Token).ConfigureAwait(false))
                        {
                            WireFrame[] batch;
                            lock (ackGate)
                            {
                                batch = coalescedAcks.Values.ToArray();
                                coalescedAcks.Clear();
                            }

                            foreach (var ack in batch)
                                await HandleTransportAck(ack).ConfigureAwait(false);
                        }
                    }
                    finally
                    {
                        stop.Cancel();
                    }
                }

                async Task ReadInputs()
                {
                    double messages = _options.MessagesPerSecond, bytes = _options.BytesPerSecond;
                    long lastInput = Stopwatch.GetTimestamp();
                    try
                    {
                        while (!stop.IsCancellationRequested)
                        {
                            var frame = await transport.ReadControlAsync(stop.Token).ConfigureAwait(false);
                            LatencyFrameTrace.Command(frame, new(session.Connection.World, session.Connection.Player, session.Connection.Generation), Meitou.Diagnostics.LatencyStage.ServerFrameReceived);
                            if (frame.Kind is WireMessage.BulkAck or WireMessage.BulkManifestAck or WireMessage.MotionCheckpointAck or WireMessage.ViewReady)
                            {
                                (WireMessage Kind, ulong Session, ulong View, ulong Id) key;
                                if (frame.Kind == WireMessage.BulkAck)
                                {
                                    var ack = GameViewProtocol.ReadAck(frame);
                                    key = (frame.Kind, ack.Identity.SessionEpoch, ack.Identity.ViewEpoch, ack.Identity.BaselineId);
                                }
                                else
                                {
                                    var ack = GameViewProtocol.ReadEpoch(frame);
                                    key = (frame.Kind, ack.Session, ack.View, frame.Kind is WireMessage.MotionCheckpointAck or WireMessage.ViewReady ? 0 : ack.Publication);
                                }

                                lock (ackGate)
                                {
                                    if (coalescedAcks.Count >= 4098 && !coalescedAcks.ContainsKey(key))
                                        throw new InvalidDataException("Transport acknowledgement key bound exceeded.");
                                    if (!coalescedAcks.TryGetValue(key, out var previous) || (frame.Kind == WireMessage.BulkAck ? GameViewProtocol.ReadAck(frame).Offset > GameViewProtocol.ReadAck(previous).Offset : GameViewProtocol.ReadEpoch(frame).Publication > GameViewProtocol.ReadEpoch(previous).Publication))
                                        coalescedAcks[key] = frame;
                                }

                                continue;
                            }

                            long now = Stopwatch.GetTimestamp();
                            double elapsed = Stopwatch.GetElapsedTime(lastInput, now).TotalSeconds;
                            lastInput = now;
                            messages = Math.Min(_options.MessagesPerSecond, messages + elapsed * _options.MessagesPerSecond) - 1;
                            bytes = Math.Min(_options.BytesPerSecond, bytes + elapsed * _options.BytesPerSecond) - frame.Payload.Length - 12;
                            if (messages < 0 || bytes < 0)
                                throw new InvalidDataException("Connection input budget exceeded.");
                            if (Interlocked.Add(ref queuedBytes, frame.Payload.Length + 12) > 4 * WireFrame.MaximumPayload || !incoming.Writer.TryWrite(frame))
                                throw new InvalidDataException("Connection input queue exceeded.");
                        }
                    }
                    finally
                    {
                        incoming.Writer.TryComplete();
                        stop.Cancel();
                    }
                }

                async Task PumpAuthorizedBulk()
                {
                    if (bulkSender is null || !bulkSender.Active)
                        return;
                    bool retain = await _loop.InvokeAsync(() => _sessions.IsCurrent(session) && (activeBusiness is not null || source.Authority.CanRetainPublishedView(session.Connection, baselinePublication)), stop.Token).ConfigureAwait(false);
                    if (retain)
                    {
                        PumpBulk();
                        return;
                    }

                    bulkSender.Withdraw();
                    pendingBulkChunk = null;
                    publisher = null;
                    viewActive = false;
                    viewEpoch = checked(viewEpoch + 1);
                    BeginView();
                    await Send([await Status().ConfigureAwait(false)], stop.Token).ConfigureAwait(false);
                }

                void PumpBulk()
                {
                    // One bounded credit window; further chunks are released only by application ACK.
                    if (bulkSender is null || !manifestAcknowledged)
                        return;
                    while (true)
                    {
                        if (pendingBulkChunk is null && (bulkSender.UnacknowledgedBytes >= 128 * 1024 || !bulkSender.TryTakeChunk(out pendingBulkChunk)))
                            return;
                        var trace = bulkTrace;
                        Action? submittedTrace = activeBusiness is null && pendingBulkChunk!.Offset == 0 ? () => LatencyTrace.Emit(LatencyTrace.Sink, trace with { Stage = LatencyStage.PublicationWriteStarted }) : null;
                        Action? releasedTrace = activeBusiness is null && pendingBulkChunk!.Offset + pendingBulkChunk.Payload.Length == bulkTotalBytes ? () => LatencyTrace.Emit(LatencyTrace.Sink, trace with { Stage = LatencyStage.PublicationWriteComplete }) : null;
                        if (!transport.TrySend(QuicLane.Bulk, BulkChunkCodec.Encode(pendingBulkChunk!), submittedTrace, releasedTrace))
                            return;
                        pendingBulkChunk = null;
                    }
                }

                void QueueBulk(byte[] bytes, WireMessage? business, Guid query, ulong publication, ulong tick, LatencyTraceEvent trace)
                {
                    if (bytes.Length == 0 || bytes.Length > BulkTransferSender.MaximumTransferBytes || bulkQueue.Count >= 8 || (bulkSender?.Active == true && bytes.Length + (long)queuedBulkBytes > 256 * 1024) || bulkTotalBytes + (long)queuedBulkBytes + bytes.Length > BulkTransferSender.MaximumTransferBytes)
                        throw new IOException("Bounded shared bulk response queue is full; accepted durable operations can be recovered by request identity.");
                    bulkQueue.Enqueue((bytes, business, query, publication, tick, trace));
                    queuedBulkBytes += bytes.Length;
                    StartNextBulk();
                }

                void StartNextBulk()
                {
                    if (bulkSender?.Active == true || !bulkQueue.TryDequeue(out var work))
                        return;
                    queuedBulkBytes -= work.Bytes.Length;
                    bulkSender ??= new(sessionEpoch, viewEpoch);
                    bulkBaselineId = checked(++transferSequence);
                    var identity = new BulkTransferIdentity(sessionEpoch, viewEpoch, bulkBaselineId, work.Tick, work.Publication);
                    if (bulkSender.Begin(identity, work.Bytes, out var manifest) != BulkTransferOutcome.Accepted)
                        throw new InvalidDataException("Cannot prepare shared bulk transfer.");
                    activeBusiness = work.Business;
                    activeBusinessQuery = work.Query;
                    manifestAcknowledged = false;
                    pendingBulkChunk = null;
                    bulkTotalBytes = manifest!.TotalBytes;
                    bulkTrace = work.Trace;
                    if (work.Business is { } kind)
                        transport.SendControl(BusinessBulkProtocol.Manifest(kind, work.Query, manifest));
                    else
                    {
                        LatencyTrace.Emit(LatencyTrace.Sink, work.Trace with { Stage = LatencyStage.PublicationSendQueued });
                        transport.SendControl(GameViewProtocol.Manifest(manifest));
                        transport.SendControl(GameViewProtocol.Epoch(WireMessage.ViewCommit, sessionEpoch, viewEpoch, work.Publication));
                    }
                }

                void BeginView()
                {
                    if (begunView == viewEpoch)
                        return;
                    if (activeBusiness is { } activeKind)
                        transport.SendControl(BusinessBulkProtocol.Cancel(activeKind, activeBusinessQuery));
                    foreach (var pending in bulkQueue)
                        if (pending.Business is { } kind)
                            transport.SendControl(BusinessBulkProtocol.Cancel(kind, pending.Query));
                    bulkQueue.Clear();
                    queuedBulkBytes = 0;
                    activeBusiness = null;
                    bulkSender?.Withdraw();
                    bulkSender = new(sessionEpoch, viewEpoch);
                    bulkTotalBytes = 0;
                    pendingBulkChunk = null;
                    transferSequence = 0;
                    transport.SendControl(GameViewProtocol.Epoch(WireMessage.ViewBegin, sessionEpoch, viewEpoch));
                    begunView = viewEpoch;
                    viewInitialized = false;
                    pendingCritical = null;
                    retainedEvents.Reset();
                    source.Authority.ReleasePublishedView(session.Connection, pinnedAuthorizationPublication);
                    pinnedAuthorizationPublication = 0;
                }

                void ReleaseCompletedPin()
                {
                    // Ready and cumulative final ACK arrive on independent dispatch paths. Both must
                    // finish before retiring a state transfer stamp that motion history may have pruned.
                    if (!viewActive || pendingCritical is not null || (bulkSender?.Active == true && activeBusiness is null) || bulkQueue.Any(work => work.Business is null))
                        return;
                    source.Authority.ReleasePublishedView(session.Connection, pinnedAuthorizationPublication);
                    pinnedAuthorizationPublication = 0;
                }

                async Task FlushCritical()
                {
                    if (pendingCritical is not { } work)
                        return;
                    ulong authorizationPublication = pinnedAuthorizationPublication != 0 ? pinnedAuthorizationPublication : work.Trace.Publication;
                    if (!await _loop.InvokeAsync(() => _sessions.IsCurrent(session) && source.Authority.CanRetainPublishedView(session.Connection, authorizationPublication), stop.Token).ConfigureAwait(false))
                    {
                        pendingCritical = null;
                        publisher = null;
                        viewActive = false;
                        viewEpoch = checked(viewEpoch + 1);
                        BeginView();
                        return;
                    }

                    for (int index = work.Index; index < work.Batches.Count; index++)
                    {
                        var publication = work.Batches[index];
                        var trace = work.Trace;
                        if (!transport.TrySendLifecyclePublication(publication.Frames, submitted: () => LatencyTrace.Emit(LatencyTrace.Sink, trace with { Stage = LatencyStage.PublicationWriteStarted }), released: () => LatencyTrace.Emit(LatencyTrace.Sink, trace with { Stage = LatencyStage.PublicationWriteComplete })))
                        {
                            pendingCritical = work with
                            {
                                Index = index
                            };
                            return;
                        }

                        LatencyTrace.Emit(LatencyTrace.Sink, trace with { Stage = LatencyStage.PublicationSendQueued });
                    }

                    retainedEvents.Admit(work.Events);
                    pendingCritical = null;
                    ReleaseCompletedPin();
                }

                async Task Publish()
                {
                    try
                    {
                        long captureOrigin = Stopwatch.GetTimestamp();
                        TimeSpan nextComponents = TimeSpan.Zero;
                        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(Math.Min(50, _options.PublicationInterval.TotalMilliseconds)));
                        while (await timer.WaitForNextTickAsync(stop.Token).ConfigureAwait(false))
                        {
                            await replicationGate.WaitAsync(stop.Token).ConfigureAwait(false);
                            try
                            {
                                if (bulkSender?.Active == true)
                                    await PumpAuthorizedBulk().ConfigureAwait(false);
                                await FlushCritical().ConfigureAwait(false);
                                var captureNow = Stopwatch.GetElapsedTime(captureOrigin);
                                bool componentsDue = captureNow >= nextComponents;
                                if (componentsDue)
                                    do
                                    {
                                        nextComponents += _options.PublicationInterval;
                                    }
                                    while (nextComponents <= captureNow);
                                bool criticalOnlyCapture = !componentsDue || pendingCritical is not null || transport.QueuedLifecycleBytes > 16 * 1024;
                                var capture = await _loop.InvokeAsync(() =>
                                {
                                    if (!_sessions.IsCurrent(session))
                                        throw new IOException("Authenticated connection was revoked or replaced.");
                                    if (publisher is null && !componentsDue)
                                        return (View: (PlayerWorldView? )null, Animations: (IReadOnlyDictionary<ulong, AnimationPlaybackState>? )null, MotionOnly: false, Destroyed: (IReadOnlySet<ulong>? )null);
                                    if (publisher is not null && !viewInitialized && source.Authority.CanRetainPublishedView(session.Connection, baselinePublication))
                                        return (View: (PlayerWorldView? )null, Animations: (IReadOnlyDictionary<ulong, AnimationPlaybackState>? )null, MotionOnly: false, Destroyed: (IReadOnlySet<ulong>? )null);
                                    bool motionOnly = publisher is not null && (bulkSender?.Active == true || bulkQueue.Count != 0 || !viewActive || criticalOnlyCapture);
                                    var view = source.Authority.CaptureView(session.Connection, observation, _options.Observation);
                                    if (view is null)
                                        return (View: (PlayerWorldView? )null, Animations: (IReadOnlyDictionary<ulong, AnimationPlaybackState>? )null, MotionOnly: false, Destroyed: (IReadOnlySet<ulong>? )null);
                                    if (publisher is null)
                                    {
                                        publisher = new(_codec, id => entityMap[id], sessionEpoch, viewEpoch);
                                        animations.Clear();
                                        playback.Clear();
                                        componentCharacters.Clear();
                                    }

                                    entityMap.Clear();
                                    foreach (var character in view.Snapshot.Characters)
                                        entityMap[character.Id] = source.Entities.Track(character.Id).Value;
                                    var destroyed = new HashSet<ulong>();
                                    foreach (var previous in componentCharacters)
                                        if (!entityMap.ContainsKey(previous.Key) && source.Authority.ConfirmedDestroyed(session.Connection, previous.Key))
                                            destroyed.Add(previous.Value);
                                    if (!motionOnly)
                                    {
                                        componentCharacters.Clear();
                                        foreach (var pair in entityMap)
                                            componentCharacters[pair.Key] = pair.Value;
                                    }

                                    foreach (var pair in componentsDue ? AnimationPlaybackExport.Capture(source, view.Snapshot) : new Dictionary<CharacterId, AnimationPlaybackCapture>())
                                    {
                                        ulong entity = source.Entities.Track(pair.Key).Value;
                                        if (animations.Capture(entity, pair.Value, checked((ulong)view.Snapshot.Tick), (float)view.SimulationStepSeconds)is { } animation)
                                            playback[entity] = animation;
                                    }

                                    view = view with
                                    {
                                        TimeScale = _loop.Runtime.Speed
                                    };
                                    return (View: publisher.DetachForEncoding(view), Animations: (IReadOnlyDictionary<ulong, AnimationPlaybackState>? )new Dictionary<ulong, AnimationPlaybackState>(playback), MotionOnly: motionOnly, Destroyed: (IReadOnlySet<ulong>? )destroyed);
                                }, stop.Token).ConfigureAwait(false);
                                if (capture.View is not { } capturedView)
                                {
                                    bool retain = baselinePublication != 0 && await _loop.InvokeAsync(() => source.Authority.CanRetainPublishedView(session.Connection, baselinePublication), stop.Token).ConfigureAwait(false);
                                    if (!retain && publisher is not null)
                                    {
                                        publisher = null;
                                        bulkSender?.Withdraw();
                                        viewActive = false;
                                        viewEpoch = checked(viewEpoch + 1);
                                        BeginView();
                                        await Send([await Status().ConfigureAwait(false)], stop.Token).ConfigureAwait(false);
                                    }

                                    continue;
                                }

                                var encodingPublisher = publisher ?? throw new InvalidOperationException("Missing component publisher.");
                                var publicationTrace = new LatencyTraceEvent(LatencyStage.EncodeStarted, capturedView.World, capturedView.Player.Value, capturedView.ConnectionGeneration, Publication: capturedView.Publication, Tick: capturedView.Snapshot.Tick);
                                LatencyTrace.Emit(LatencyTrace.Sink, publicationTrace);
                                var publications = await Task.Run<IReadOnlyList<(GameStatePublication Publication, bool Critical)>>(() =>
                                {
                                    var view = capturedView with
                                    {
                                        Events = retainedEvents.Observe(capturedView.Events)
                                    };
                                    if (!viewInitialized)
                                        return[(encodingPublisher.Capture(view, capture.Animations, capture.Destroyed), false)];
                                    if (criticalOnlyCapture)
                                    {
                                        var motionOnly = encodingPublisher.CaptureMotionOnly(view);
                                        return[(new GameStatePublication(0, false, []) { Motion = motionOnly.Motion, WithdrawnMotion = motionOnly.WithdrawnMotion }, true)];
                                    }

                                    var critical = encodingPublisher.CaptureCritical(view, capture.Animations, capture.Destroyed);
                                    var result = critical.Select(item => (Publication: item, Critical: true)).ToList();
                                    if (!capture.MotionOnly)
                                    {
                                        var ui = encodingPublisher.Capture(view, capture.Animations, capture.Destroyed);
                                        if (ui.ComponentBytes.Count != 0)
                                            result.Add((ui with { Motion = [], WithdrawnMotion = [] }, false));
                                    }

                                    return result;
                                }, stop.Token).ConfigureAwait(false);
                                try
                                {
                                    _options.AnimationObserved?.Invoke(animations.Diagnostics);
                                }
                                catch
                                { /* numeric diagnostics only */
                                }

                                var criticalBatches = publications.Where(item => item.Critical && item.Publication.Frames.Count != 0).Select(item => item.Publication).ToArray();
                                if (criticalBatches.Length != 0)
                                {
                                    if (pendingCritical is not null)
                                        throw new InvalidOperationException("Critical publication was encoded without admission capacity.");
                                    pendingCritical = (criticalBatches, publicationTrace, capturedView.Events.HighWater, 0);
                                    if (pinnedAuthorizationPublication == 0)
                                    {
                                        if (!await _loop.InvokeAsync(() => source.Authority.PinPublishedView(session.Connection, capturedView.Publication), stop.Token).ConfigureAwait(false))
                                        {
                                            publisher = null;
                                            viewActive = false;
                                            viewEpoch = checked(viewEpoch + 1);
                                            BeginView();
                                            await Send([await Status().ConfigureAwait(false)], stop.Token).ConfigureAwait(false);
                                            continue;
                                        }

                                        pinnedAuthorizationPublication = capturedView.Publication;
                                    }

                                    await FlushCritical().ConfigureAwait(false);
                                }

                                foreach (var(publication, critical)in publications)
                                {
                                    LatencyTrace.Emit(LatencyTrace.Sink, publicationTrace with { Stage = LatencyStage.EncodeFinished, Value = publication.Frames.Sum(f => (long)f.Length) });
                                    try
                                    {
                                        _options.PublicationObserved?.Invoke(publication);
                                    }
                                    catch
                                    { /* diagnostic subscriber cannot alter gameplay */
                                    }

                                    bool authorized = await _loop.InvokeAsync(() => source.Authority.CanRetainPublishedView(session.Connection, capturedView.Publication), stop.Token).ConfigureAwait(false);
                                    if (!authorized)
                                    {
                                        publisher = null;
                                        bulkSender?.Withdraw();
                                        pendingBulkChunk = null;
                                        viewActive = false;
                                        viewEpoch = checked(viewEpoch + 1);
                                        BeginView();
                                        await Send([await Status().ConfigureAwait(false)], stop.Token).ConfigureAwait(false);
                                        continue;
                                    }

                                    if (!critical)
                                    {
                                        viewActive = false;
                                        baselinePublication = capturedView.Publication;
                                        if (publication.Baseline)
                                            BeginView();
                                        if (!await _loop.InvokeAsync(() => source.Authority.PinPublishedView(session.Connection, baselinePublication), stop.Token).ConfigureAwait(false))
                                        {
                                            publisher = null;
                                            viewActive = false;
                                            viewEpoch = checked(viewEpoch + 1);
                                            BeginView();
                                            await Send([await Status().ConfigureAwait(false)], stop.Token).ConfigureAwait(false);
                                            break;
                                        }

                                        pinnedAuthorizationPublication = baselinePublication;
                                        QueueBulk(GameViewProtocol.Pack(publication.Frames), null, default, capturedView.Publication, checked((ulong)capturedView.Snapshot.Tick), publicationTrace);
                                        if (publication.Baseline)
                                            retainedEvents.Admit(capturedView.Events.HighWater);
                                        if (publication.Baseline)
                                        {
                                            motion = transport.WithScheduler(scheduler =>
                                            {
                                                var producer = new MotionReplicationProducer(scheduler, 1, 1, sessionEpoch, viewEpoch,
                                                    () => Stopwatch.GetElapsedTime(0));
                                                // These are the same detached roots encoded in the queued baseline, never a later world sample.
                                                producer.SeedReliableBaseline(sessionEpoch, viewEpoch, checked((ulong)capturedView.Snapshot.Tick),
                                                    publication.Motion.Select(item => (item.Record, item.Relevance)).ToArray());
                                                return producer;
                                            });
                                            motion.PacketQueued = packet =>
                                            {
                                                var queued = new LatencyTraceEvent(LatencyStage.MotionSendQueued, session.Connection.World, session.Connection.Player.Value, session.Connection.Generation, Publication: packet.Header.AuthorityPublication, Tick: checked((long)packet.Header.ServerTick), Value: unchecked((long)packet.Header.PacketSequence));
                                                foreach (var record in packet.Records)
                                                    LatencyTrace.Emit(LatencyTrace.Sink, queued with { Entity = record.EntityId });
                                            };
                                            sentCheckpoints.Clear();
                                        }
                                    }

                                    if (viewInitialized && motion is not null && (publication.Motion.Count != 0 || publication.WithdrawnMotion.Count != 0))
                                    {
                                        transport.WithScheduler(_ =>
                                        {
                                            foreach (var withdrawn in publication.WithdrawnMotion)
                                            {
                                                motion.Withdraw(withdrawn.Entity, withdrawn.Generation);
                                                animations.Withdraw(withdrawn.Entity);
                                                playback.Remove(withdrawn.Entity);
                                            }

                                            foreach (var item in publication.Motion)
                                                if (motion.Capture(sessionEpoch, viewEpoch, checked((ulong)capturedView.Snapshot.Tick), item.Record, item.Relevance, capturedView.Publication)is MotionCaptureOutcome.CheckpointBackpressure or MotionCaptureOutcome.ReduceSubscription)
                                                    throw new IOException("Motion subscription capacity exceeded.");
                                            if (publication.Motion.Count != 0)
                                                motion.Pump(checked((ulong)capturedView.Snapshot.Tick), capturedView.Publication);
                                            return true;
                                        });
                                        foreach (var checkpoint in transport.WithScheduler(_ => motion.PendingCheckpoints))
                                        {
                                            if (!sentCheckpoints.Add(checkpoint.Id))
                                                continue;
                                            byte[] bytes = MotionDatagramCodec.Encode(new(new(sessionEpoch, viewEpoch, checkpoint.Id | (1UL << 63), checkpoint.CaptureTick, checkpoint.AuthorityPublication), [checkpoint.Record]));
                                            var checkpointTrace = publicationTrace with
                                            {
                                                Tick = checked((long)checkpoint.CaptureTick),
                                                Publication = checkpoint.AuthorityPublication
                                            };
                                            var correlation = checkpointTrace;
                                            LatencyTrace.Emit(LatencyTrace.Sink, correlation with { Stage = LatencyStage.MotionSendQueued, Entity = checkpoint.Record.EntityId, Value = unchecked((long)(checkpoint.Id | (1UL << 63))) });
                                            transport.Send(QuicLane.Lifecycle, bytes, submitted: () => LatencyTrace.Emit(LatencyTrace.Sink, correlation with { Stage = LatencyStage.MotionWriteStarted, Entity = checkpoint.Record.EntityId, Value = unchecked((long)(checkpoint.Id | (1UL << 63))) }), released: () => LatencyTrace.Emit(LatencyTrace.Sink, correlation with { Stage = LatencyStage.MotionWriteComplete, Entity = checkpoint.Record.EntityId, Value = unchecked((long)(checkpoint.Id | (1UL << 63))) }));
                                        }
                                    }
                                }
                            }
                            finally
                            {
                                replicationGate.Release();
                            }
                        }
                    }
                    finally
                    {
                        stop.Cancel();
                    }
                }
            }
            catch (OperationCanceledException)when (stop.IsCancellationRequested)
            {
            }
            finally
            {
                await stop.CancelAsync().ConfigureAwait(false);
                Exception? backgroundFailure = null;
                foreach (var task in new[]
                {
                    publishing,
                    reading,
                    acknowledging
                }.OfType<Task>().Concat(replies))
                    try
                    {
                        await task.ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)when (stop.IsCancellationRequested)
                    {
                    }
                    catch (Exception e)
                    {
                        backgroundFailure ??= e;
                    }

                try
                {
                    if (session is not null && source is not null && !_loop.Stopping.IsCancellationRequested)
                        await _loop.InvokeAsync(() => source.ReleasePlayerConnection(session.Connection)).ConfigureAwait(false);
                }
                finally
                {
                    if (session is not null)
                        source?.Authority.Close(session.Connection);
                    commands?.Dispose();
                }

                if (backgroundFailure is not null)
                    System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(backgroundFailure).Throw();
            }

            async Task AwaitPeerClose()
            {
                // The peer must consume the terminal result before its connection is destroyed.
                // Native ownership release alone does not prove peer receipt.
                try
                {
                    await transport.ReadAsync(handshake.Token).ConfigureAwait(false);
                }
                catch (Exception error)when (error is IOException or ChannelClosedException or OperationCanceledException)
                {
                }
            }

            async Task Send(IReadOnlyList<WireFrame> frames, CancellationToken token)
            {
                token.ThrowIfCancellationRequested();
                foreach (var frame in frames)
                    if (frame.Payload.Length <= 16 * 1024 - 26)
                        transport.SendControl(frame);
                    else if (sendLarge is not null)
                        await sendLarge(frame, token).ConfigureAwait(false);
                    else
                        throw new InvalidDataException("Oversized handshake response.");
            }
        }
    }
}