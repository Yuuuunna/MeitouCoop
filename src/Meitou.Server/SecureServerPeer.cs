using System.Net.Security;
using System.Diagnostics;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Channels;
using Meitou.Networking;
using Meitou.Simulation;

namespace Meitou.Server;

public sealed record ServerPeerOptions
{
    public TimeSpan HandshakeTimeout { get; init; } = TimeSpan.FromSeconds(15);
    public TimeSpan WriteTimeout { get; init; } = TimeSpan.FromSeconds(10);
    public TimeSpan PublicationInterval { get; init; } = TimeSpan.FromMilliseconds(100);
    public TimeSpan PreparationTimeout { get; init; } = TimeSpan.FromMinutes(3);
    public int MessagesPerSecond { get; init; } = 120;
    public int BytesPerSecond { get; init; } = 2 * 1024 * 1024;
    public PlayerObservationRules Observation { get; init; } = new();
}

/// <summary>One TLS connection. Preparation is a host-supplied region loader; all world access goes through the shared loop.</summary>
public sealed class SecureServerPeer
{
    readonly ServerWorldLoop loop;
    readonly ServerAccountSessions sessions;
    readonly CompatibilityHello hello;
    readonly WorldViewCodec codec;
    readonly Func<AuthorityConnection, EntityId, CancellationToken, Task<bool>> prepareEntry;
    readonly ServerPeerOptions options;
    readonly ServerCreationEndpoint? creation;
    readonly ServerInvitationEndpoint? invitations;
    readonly ServerInformation? information;
    internal CancellationToken Stopping => loop.Stopping;
    public SecureServerPeer(ServerWorldLoop loop, ServerAccountSessions sessions, CompatibilityHello hello, WorldViewCodec codec,
        Func<AuthorityConnection, EntityId, CancellationToken, Task<bool>> prepareEntry, ServerPeerOptions? options = null, ServerCreationEndpoint? creation = null, ServerInvitationEndpoint? invitations = null, ServerInformation? information = null)
    {
        this.loop = loop; this.sessions = sessions; this.hello = hello; this.codec = codec; this.prepareEntry = prepareEntry;
        this.options = options ?? new();
        this.creation = creation;
        this.invitations = invitations;
        this.information = information;
        if (information is not null)
        {
            if (information.Hello != hello) throw new ArgumentException("Discovery must describe the active server.", nameof(information));
            _ = ServerDiscovery.Encode(information);
        }
        if (this.options.HandshakeTimeout <= TimeSpan.Zero || this.options.WriteTimeout <= TimeSpan.Zero || this.options.PublicationInterval < TimeSpan.FromMilliseconds(10)
            || this.options.PreparationTimeout <= TimeSpan.Zero || this.options.MessagesPerSecond <= 0 || this.options.BytesPerSecond < WireFrame.MaximumPayload)
            throw new ArgumentOutOfRangeException(nameof(options));
    }

    public async Task RunAsync(TcpClient client, X509Certificate2 certificate, CancellationToken cancellation = default)
    {
        using (client)
        using (var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellation, loop.Stopping))
        using (var writes = new SemaphoreSlim(1))
        using (var handshake = CancellationTokenSource.CreateLinkedTokenSource(stop.Token))
        {
            handshake.CancelAfter(options.HandshakeTimeout);
            await using var tls = await GameTls.AcceptAsync(client.GetStream(), certificate, handshake.Token).ConfigureAwait(false);
            AuthoritativeCommandChannel? commands = null;
            AuthenticatedPlayerSession? session = null;
            World? source = null;
            Task? publishing = null;
            Task? reading = null;
            var replies = new HashSet<Task>();
            try
            {
                var first = await WireFrame.ReadAsync(tls, handshake.Token).ConfigureAwait(false) ?? throw new EndOfStreamException();
                if (first.Kind == WireMessage.ServerInformationQuery)
                {
                    if (!first.Payload.IsEmpty || information is null) return;
                    await Send([ServerDiscovery.Encode(information)], handshake.Token).ConfigureAwait(false);
                    return;
                }
                commands = await loop.InvokeAsync(() => new AuthoritativeCommandChannel(hello, loop.Runtime.World.Authority), handshake.Token).ConfigureAwait(false);
                var compatible = commands.ReceiveHello(first);
                await Send([new(compatible == CompatibilityFailure.None ? WireMessage.Compatible : WireMessage.Incompatible, CompatibilityHandshake.Encode(hello))], handshake.Token).ConfigureAwait(false);
                if (compatible != CompatibilityFailure.None) return;
                var authentication = await WireFrame.ReadAsync(tls, handshake.Token).ConfigureAwait(false) ?? throw new EndOfStreamException();
                bool device = authentication.Kind == WireMessage.AuthenticateDevice;
                AuthenticationRequest? request = AuthenticationProtocol.Decode(device ? authentication with { Kind = WireMessage.Authenticate } : authentication);
                var binding = await loop.InvokeAsync(() => (World: loop.Runtime.World, Session: device && information?.DeviceRegistration == true ? sessions.OpenDevice(request!) : sessions.Open(request!)), handshake.Token).ConfigureAwait(false);
                request = null;
                source = binding.World; session = binding.Session;
                handshake.Token.ThrowIfCancellationRequested();
                if (session is null) { await Send([new(WireMessage.AuthenticationFailed, ReadOnlyMemory<byte>.Empty)], handshake.Token).ConfigureAwait(false); return; }
                commands.BindAuthenticated(session.Connection);
                var publisher = new AuthoritativeStatePublisher(source.Authority, session.Connection, codec);
                PlayerViewRequest observation = new();
                await Send([await Status(authenticated: true).ConfigureAwait(false)], stop.Token).ConfigureAwait(false);
                var incoming = Channel.CreateBounded<WireFrame>(new BoundedChannelOptions(64) { SingleReader = true, SingleWriter = true });
                long queuedBytes = 0;
                reading = ReadInputs();
                publishing = Publish();
                await foreach (var frame in incoming.Reader.ReadAllAsync(stop.Token).ConfigureAwait(false))
                {
                    Interlocked.Add(ref queuedBytes, -frame.Payload.Length - 12);
                    if (!await loop.InvokeAsync(() => sessions.IsCurrent(session), stop.Token).ConfigureAwait(false)) break;
                    switch (frame.Kind)
                    {
                        case WireMessage.TalkHintQuery:
                            var query = InteractionProtocol.ReadQuery(frame);
                            var hint = await loop.InvokeAsync(() => source.Authority.QueryTalk(session.Connection, query.Speaker, query.Target,
                                new(session.Connection.Player, query.Grant.Entity, query.Grant.Faction, query.Grant.Revision), query.MembershipRevision,
                                observation, options.Observation), stop.Token).ConfigureAwait(false);
                            await Send([InteractionProtocol.Reply(new(new(session.Connection.World, session.Connection.Player, session.Connection.Generation),
                                query.Query, query.Speaker, query.Target, query.Grant.Revision, query.MembershipRevision, hint))], stop.Token).ConfigureAwait(false);
                            break;
                        case WireMessage.InvitationRequest:
                            var invitation = InvitationProtocol.ReadRequest(frame);
                            var invitationReply = invitations is null
                                ? InvitationProtocol.Reply(new(new(session.Connection.World, session.Connection.Player, session.Connection.Generation), invitation.Query, InvitationOutcome.Rejected, [], default, []))
                                : await invitations.HandleAsync(session.Connection, frame, stop.Token).ConfigureAwait(false);
                            await Send([invitationReply, await Status().ConfigureAwait(false)], stop.Token).ConfigureAwait(false);
                            break;
                        case WireMessage.CreationCatalogQuery:
                        case WireMessage.CreationPreview:
                        case WireMessage.CreationCommit:
                        case WireMessage.CreationLookup:
                            using (var preparing = CancellationTokenSource.CreateLinkedTokenSource(stop.Token))
                            {
                                preparing.CancelAfter(options.PreparationTimeout);
                                var reply = creation is null ? CreationProtocol.Reply(CreationProtocol.Unavailable(new(session.Connection.World, session.Connection.Player, session.Connection.Generation), frame), codec)
                                    : await creation.HandleAsync(session.Connection, frame, preparing.Token).ConfigureAwait(false);
                                await Send([reply, await Status().ConfigureAwait(false)], stop.Token).ConfigureAwait(false);
                            }
                            break;
                        case WireMessage.Command:
                            foreach (var completed in replies.Where(t => t.IsCompleted).ToArray()) { await completed.ConfigureAwait(false); replies.Remove(completed); }
                            if (replies.Count >= 64) throw new InvalidDataException("Too many pending command responses.");
                            replies.Add(Reply(commands.ReceiveCommand(frame)));
                            break;
                        case WireMessage.EnterWorld:
                            var entry = SessionProtocol.ReadEnter(frame);
                            bool allowed = await loop.InvokeAsync(() => CanPrepare(entry.FocusRole), stop.Token).ConfigureAwait(false);
                            if (allowed)
                            {
                                using var preparing = CancellationTokenSource.CreateLinkedTokenSource(stop.Token); preparing.CancelAfter(options.PreparationTimeout);
                                if (await prepareEntry(session.Connection, entry.FocusRole, preparing.Token).ConfigureAwait(false))
                                    await loop.InvokeAsync(() => { if (CanEnter(entry.FocusRole)) source.Authority.BeginSynchronization(session.Connection); }, stop.Token).ConfigureAwait(false);
                            }
                            await Send([await Status().ConfigureAwait(false)], stop.Token).ConfigureAwait(false);
                            break;
                        case WireMessage.StateAck:
                            var ack = StateTransport.DecodeAck(frame);
                            if (ack.Identity != new ReplicationIdentity(session.Connection.World, session.Connection.Player, session.Connection.Generation))
                                throw new InvalidDataException("State acknowledgement belongs to another connection.");
                            var acknowledgedStatus = await loop.InvokeAsync(() =>
                            {
                                if (!publisher.Acknowledge(frame)) publisher.RequestBaseline();
                                return SessionProtocol.Admission(session.Connection, source.Authority.ReadAdmission(session.Connection) ?? throw new IOException("Connection was replaced."));
                            }, stop.Token).ConfigureAwait(false);
                            await Send([acknowledgedStatus], stop.Token).ConfigureAwait(false);
                            break;
                        case WireMessage.StateResync:
                            if (!await loop.InvokeAsync(() => publisher.RequestBaseline(frame), stop.Token).ConfigureAwait(false)) throw new InvalidDataException("Invalid state resync request.");
                            break;
                        case WireMessage.Observe:
                            var requested = SessionProtocol.ReadObservation(frame);
                            await loop.InvokeAsync(() => observation = requested, stop.Token).ConfigureAwait(false);
                            break;
                        case WireMessage.AdmissionQuery:
                            if (!frame.Payload.IsEmpty) throw new InvalidDataException("Admission query must be empty.");
                            await Send([await Status().ConfigureAwait(false)], stop.Token).ConfigureAwait(false);
                            break;
                        default: throw new InvalidDataException("Unexpected authenticated message.");
                    }
                }

                bool CanPrepare(EntityId role) => ReferenceEquals(source, loop.Runtime.World) && source.Authority.GetPhase(session.Connection) == AdmissionPhase.Preparing
                    && !source.HasPendingDurableMutation && source.Players.KnownRoles(session.Connection.Player).Any(g => g.Entity == role
                        && source.Players.Find(session.Connection.Player)!.IsMember(g.Faction));
                bool CanEnter(EntityId role) => CanPrepare(role) && source.Entities.TryResolve(role, out var character) && source.Players.CanControl(session.Connection.Player, character);

                Task<WireFrame> Status(bool authenticated = false) => loop.InvokeAsync(() =>
                    SessionProtocol.Admission(session.Connection, source.Authority.ReadAdmission(session.Connection) ?? throw new IOException("Connection was replaced."), authenticated), stop.Token);

                async Task Reply(Task<AuthorityReceipt> pending)
                {
                    try
                    {
                        var receipt = await pending.WaitAsync(stop.Token).ConfigureAwait(false);
                        await Send([CommandProtocol.EncodeReceipt(session.Connection.World, receipt)], stop.Token).ConfigureAwait(false);
                    }
                    catch { stop.Cancel(); throw; }
                }

                async Task ReadInputs()
                {
                    double messages = options.MessagesPerSecond, bytes = options.BytesPerSecond;
                    long lastInput = Stopwatch.GetTimestamp();
                    try
                    {
                        while (await WireFrame.ReadAsync(tls, stop.Token).ConfigureAwait(false) is { } frame)
                        {
                            long now = Stopwatch.GetTimestamp(); double elapsed = Stopwatch.GetElapsedTime(lastInput, now).TotalSeconds; lastInput = now;
                            messages = Math.Min(options.MessagesPerSecond, messages + elapsed * options.MessagesPerSecond) - 1;
                            bytes = Math.Min(options.BytesPerSecond, bytes + elapsed * options.BytesPerSecond) - frame.Payload.Length - 12;
                            if (messages < 0 || bytes < 0) throw new InvalidDataException("Connection input budget exceeded.");
                            if (Interlocked.Add(ref queuedBytes, frame.Payload.Length + 12) > 4 * WireFrame.MaximumPayload || !incoming.Writer.TryWrite(frame))
                                throw new InvalidDataException("Connection input queue exceeded.");
                        }
                    }
                    finally { incoming.Writer.TryComplete(); stop.Cancel(); }
                }

                async Task Publish()
                {
                    bool hadWorld = false;
                    try
                    {
                        using var timer = new PeriodicTimer(options.PublicationInterval);
                        while (await timer.WaitForNextTickAsync(stop.Token).ConfigureAwait(false))
                        {
                            var prepared = await loop.InvokeAsync(() =>
                            {
                                if (!sessions.IsCurrent(session)) throw new IOException("Authenticated connection was revoked or replaced.");
                                var phase = source.Authority.GetPhase(session.Connection);
                                if (phase == AdmissionPhase.Preparing && !hadWorld) return (Work: (PublicationWork?)null, Frames: (IReadOnlyList<WireFrame>?)Array.Empty<WireFrame>());
                                var work = publisher.Prepare(observation, options.Observation, loop.Runtime.Speed, out var ready);
                                return (Work: work, Frames: ready);
                            }, stop.Token).ConfigureAwait(false);
                            if (prepared.Work is { } encoding) await Task.Run(encoding.Encode, stop.Token).ConfigureAwait(false);
                            var frames = await loop.InvokeAsync<IReadOnlyList<WireFrame>>(() =>
                            {
                                if (!sessions.IsCurrent(session)) throw new IOException("Authenticated connection was revoked or replaced.");
                                var captured = (prepared.Work is { } work ? publisher.Complete(work) : prepared.Frames) ?? throw new IOException("Connection was replaced.");
                                if (captured.Count == 0) return captured;
                                bool withdrawn = captured.Any(f => f.Kind == WireMessage.StateWithdrawn);
                                hadWorld = !withdrawn;
                                return withdrawn ? [.. captured, SessionProtocol.Admission(session.Connection, source.Authority.ReadAdmission(session.Connection)!)] : captured;
                            }, stop.Token).ConfigureAwait(false);
                            if (frames.Count != 0) await Send(frames, stop.Token).ConfigureAwait(false);
                        }
                    }
                    finally { stop.Cancel(); }
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
            finally
            {
                await stop.CancelAsync().ConfigureAwait(false);
                Exception? backgroundFailure = null;
                foreach (var task in new[] { publishing, reading }.OfType<Task>().Concat(replies))
                    try { await task.ConfigureAwait(false); }
                    catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
                    catch (Exception e) { backgroundFailure ??= e; }
                try
                {
                    if (session is not null && source is not null && !loop.Stopping.IsCancellationRequested)
                        await loop.InvokeAsync(() => source.ReleasePlayerConnection(session.Connection)).ConfigureAwait(false);
                }
                finally
                {
                    if (session is not null) source?.Authority.Close(session.Connection);
                    commands?.Dispose();
                    if (reading is not null) client.Dispose(); // abort authenticated background IO; discovery replies close TLS gracefully
                }
                if (backgroundFailure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(backgroundFailure).Throw();
            }

            async Task Send(IReadOnlyList<WireFrame> frames, CancellationToken token)
            {
                await writes.WaitAsync(token).ConfigureAwait(false);
                try
                {
                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(options.WriteTimeout);
                    foreach (var frame in frames) await WireFrame.WriteAsync(tls, frame, deadline.Token).ConfigureAwait(false);
                }
                finally { writes.Release(); }
            }
        }
    }
}
