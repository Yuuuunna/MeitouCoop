using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Threading.Channels;
using Meitou.Simulation;

namespace Meitou.Networking;

/// <summary>A coherent presentation reference. Treat the contained views as read-only.</summary>
public sealed record RemoteSessionState(long Version, AdmissionMessage Admission, PlayerWorldView? View, Exception? Disconnection)
{
    public bool CanControl => Disconnection is null && !Admission.State.Busy && Admission.State.Phase == AdmissionPhase.Active && View is not null;
}

/// <summary>
/// One authenticated remote connection; owns transport and replication, never a Simulation.World.
/// Request cancellation stops waiting only: an accepted write may already have executed. Recover durable
/// mutations by their original request identity through lookup/resume; this class never reconnects or retries mutations.
/// </summary>
public sealed class RemoteGameSession : IAsyncDisposable
{
    readonly TcpClient tcp;
    readonly SslStream stream;
    readonly WorldViewCodec codec;
    readonly ClientStateReceiver receiver;
    readonly object gate = new();
    readonly object replicaGate = new();
    readonly CancellationTokenSource lifetime = new();
    readonly Channel<WireFrame> outbound = Channel.CreateBounded<WireFrame>(new BoundedChannelOptions(64)
    { SingleReader = true, FullMode = BoundedChannelFullMode.Wait, AllowSynchronousContinuations = false });
    readonly Dictionary<RequestKey, TaskCompletionSource<object>> pending = [];
    TaskCompletionSource changed = NewSignal();
    Task reader = Task.CompletedTask, writer = Task.CompletedTask;
    Task? disposal;
    RemoteSessionState state;
    int queuedBytes;
    const int MaximumQueuedBytes = 4 * 1024 * 1024;
    readonly record struct RequestKey(WireMessage Kind, Guid Query = default, ulong Command = 0);
    static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    RemoteGameSession(TcpClient tcp, SslStream stream, AdmissionMessage admission, WorldViewCodec codec)
    {
        this.tcp = tcp; this.stream = stream; this.codec = codec;
        receiver = new(admission.Identity, codec);
        state = new(1, admission, null, null);
    }

    public static async Task<RemoteGameSession> ConnectAsync(string host, int port, CompatibilityHello hello,
        AuthenticationRequest credential, WorldViewCodec codec, string? certificateSha256 = null, CancellationToken cancellationToken = default, bool deviceIdentity = false)
    {
        ArgumentNullException.ThrowIfNull(codec);
        var compatibility = new CompatibilityHandshake(hello);
        var authentication = AuthenticationProtocol.Encode(credential);
        if (deviceIdentity) authentication = authentication with { Kind = WireMessage.AuthenticateDevice };
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        var tcp = new TcpClient { NoDelay = true };
        SslStream? stream = null;
        try
        {
            await tcp.ConnectAsync(host, port, deadline.Token).ConfigureAwait(false);
            stream = await GameTls.ConnectAsync(tcp.GetStream(), host, certificateSha256, deadline.Token).ConfigureAwait(false);
            await WireFrame.WriteAsync(stream, new(WireMessage.Hello, CompatibilityHandshake.Encode(hello)), deadline.Token).ConfigureAwait(false);
            var reply = await WireFrame.ReadAsync(stream, deadline.Token).ConfigureAwait(false) ?? throw new EndOfStreamException();
            if (reply.Kind is not (WireMessage.Compatible or WireMessage.Incompatible)) throw new InvalidDataException("Expected compatibility response.");
            var mismatch = compatibility.Receive(CompatibilityHandshake.Decode(reply.Payload.Span));
            if (reply.Kind != WireMessage.Compatible || mismatch != CompatibilityFailure.None)
                throw new InvalidDataException($"Server compatibility rejected: {mismatch}.");
            await WireFrame.WriteAsync(stream, authentication, deadline.Token).ConfigureAwait(false);
            reply = await WireFrame.ReadAsync(stream, deadline.Token).ConfigureAwait(false) ?? throw new EndOfStreamException();
            if (reply.Kind == WireMessage.AuthenticationFailed) throw new AuthenticationException("Server rejected account credentials.");
            if (reply.Kind != WireMessage.Authenticated) throw new InvalidDataException("Expected authenticated admission.");
            var session = new RemoteGameSession(tcp, stream, SessionProtocol.ReadAdmission(reply), codec);
            session.writer = Task.Run(session.WriteLoop);
            session.reader = Task.Run(session.ReadLoop);
            return session;
        }
        catch
        {
            tcp.Dispose();
            if (stream is not null) await stream.DisposeAsync().ConfigureAwait(false);
            throw;
        }
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
    public void RequestEntry(EntityId role) => Queue(SessionProtocol.Enter(role));
    public void RefreshAdmission() => Queue(new(WireMessage.AdmissionQuery, ReadOnlyMemory<byte>.Empty));
    /// <summary>Requests a fresh complete view; the last validated view remains usable until replaced or withdrawn.</summary>
    public void RequestBaseline() { lock (gate) Queue(receiver.ResyncRequest()); }
    public void Observe(PlayerViewRequest request)
    {
        var frame = SessionProtocol.Observe(request);
        SessionProtocol.ReadObservation(frame); // apply the same bounds before queuing
        Queue(frame);
    }

    public Task<CommandReceiptMessage> CommandAsync(ulong request, SimCommand command, ExpectedCommandAuthority expected, CancellationToken cancellationToken = default)
    {
        lock (gate)
        {
            EnsureConnected();
            if (!state.CanControl) throw new InvalidOperationException("Control requires active admission and a usable world replica.");
            return Request<CommandReceiptMessage>(new(WireMessage.CommandReceipt, Command: request),
                CommandProtocol.Encode(state.Admission.Identity.World, request, command, expected), cancellationToken);
        }
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
        }
    }
    void EnsureConnected()
    {
        if (state.Disconnection is not null) throw new IOException("Remote session is disconnected.", state.Disconnection);
    }
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
                var frame = await WireFrame.ReadAsync(stream, lifetime.Token).ConfigureAwait(false) ?? throw new EndOfStreamException("Server closed the connection.");
                ReplicaResult? replicaResult = null;
                WireFrame? acknowledgement = null, resync = null;
                if (frame.Kind is WireMessage.StateChunk or WireMessage.StateWithdrawn)
                {
                    // Decode away from the gate used by State, input submission and the render thread.
                    // Never acquire gate while holding replicaGate: Stop/Admission use the reverse order.
                    lock (replicaGate)
                    {
                        lifetime.Token.ThrowIfCancellationRequested();
                        replicaResult = receiver.Receive(frame);
                        if (replicaResult == ReplicaResult.Applied) acknowledgement = receiver.Acknowledgement;
                        if (replicaResult == ReplicaResult.NeedsBaseline && frame.Kind != WireMessage.StateWithdrawn) resync = receiver.ResyncRequest();
                    }
                }
                lock (gate)
                {
                    EnsureConnected();
                    switch (frame.Kind)
                    {
                        case WireMessage.Admission:
                            var admission = SessionProtocol.ReadAdmission(frame);
                            CheckIdentity(admission.Identity);
                            if (admission.State.Sequence > state.Admission.State.Sequence)
                            {
                                if (admission.State.Busy || admission.State.Phase == AdmissionPhase.Preparing)
                                    lock (replicaGate) receiver.Replica.Reset();
                                Publish(state with { Admission = admission, View = VisibleView(admission) });
                            }
                            break;
                        case WireMessage.StateChunk:
                        case WireMessage.StateWithdrawn:
                            if (acknowledgement is not null) Queue(acknowledgement);
                            if (resync is not null) Queue(resync);
                            if (replicaResult is not null) Publish(state with { View = VisibleView(state.Admission) });
                            break;
                        case WireMessage.CommandReceipt:
                            var receipt = CommandProtocol.DecodeReceipt(frame);
                            if (receipt.World != state.Admission.Identity.World) throw new InvalidDataException("Receipt belongs to another world.");
                            Complete(new(frame.Kind, Command: receipt.Receipt.Request), receipt);
                            break;
                        case WireMessage.CreationReply:
                            var creation = CreationProtocol.ReadReply(frame, codec); CheckIdentity(creation.Identity);
                            Complete(new(frame.Kind, creation.Query), creation);
                            break;
                        case WireMessage.InvitationReply:
                            var invitation = InvitationProtocol.ReadReply(frame); CheckIdentity(invitation.Identity);
                            Complete(new(frame.Kind, invitation.Query), invitation);
                            break;
                        case WireMessage.TalkHintReply:
                            var hint = InteractionProtocol.ReadReply(frame); CheckIdentity(hint.Identity);
                            Complete(new(frame.Kind, hint.Query), hint);
                            break;
                        default: throw new InvalidDataException("Unexpected server message.");
                    }
                }
            }
        }
        catch (Exception error) { Stop(error); }
    }

    void CheckIdentity(ReplicationIdentity identity)
    {
        if (identity != state.Admission.Identity) throw new InvalidDataException("Reply belongs to another connection.");
    }

    PlayerWorldView? VisibleView(AdmissionMessage admission)
        => admission.State.Busy || admission.State.Phase == AdmissionPhase.Preparing ? null : receiver.Replica.Current;

    async Task WriteLoop()
    {
        try
        {
            await foreach (var frame in outbound.Reader.ReadAllAsync(lifetime.Token).ConfigureAwait(false))
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                deadline.CancelAfter(TimeSpan.FromSeconds(10));
                await WireFrame.WriteAsync(stream, frame, deadline.Token).ConfigureAwait(false);
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
            if (error is not ObjectDisposedException && disposal is null)
                Console.Error.WriteLine($"network   {DateTime.UtcNow:O} disconnected: {error}");
            lock (replicaGate) receiver.Replica.Reset();
            Publish(state with { View = null, Disconnection = error });
            foreach (var waiter in pending.Values) waiter.TrySetException(new IOException("Remote request interrupted; its server outcome may be unknown.", error));
            pending.Clear();
            outbound.Writer.TryComplete();
            lifetime.Cancel();
            tcp.Dispose();
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (gate) return new(disposal ??= DisposeCoreAsync());
    }

    async Task DisposeCoreAsync()
    {
        Stop(new ObjectDisposedException(nameof(RemoteGameSession)));
        await Task.WhenAll(reader, writer).ConfigureAwait(false);
        try { await stream.DisposeAsync().ConfigureAwait(false); }
        finally
        {
            while (outbound.Reader.TryRead(out _)) { }
            lifetime.Dispose();
        }
    }
}
