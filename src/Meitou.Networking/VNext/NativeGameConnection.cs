using System.Buffers.Binary;
using System.Diagnostics;
using System.Threading.Channels;
using Meitou.Networking.VNext.Transport;

namespace Meitou.Networking.VNext;

public sealed record QuicGameTransportOptions(string NativeDirectory, string ShimSha256, string CaFile, string? ServerName = null);
public readonly record struct GameLaneMessage(QuicLane Lane, ReadOnlyMemory<byte> Payload);

/// <summary>One bounded native connection owner. Stream framing and native ownership are independent per lane.</summary>
public sealed class NativeGameConnection : IAsyncDisposable
{
    readonly NativeQuicEndpoint endpoint;
    readonly object gate = new();
    readonly CancellationTokenSource stop = new();
    readonly SemaphoreSlim signal = new(0, 1);
    readonly Channel<GameLaneMessage> incoming = Channel.CreateBounded<GameLaneMessage>(128);
    readonly BandwidthScheduler scheduler;
    readonly Dictionary<ulong, ScheduledSend> submitted = [];
    readonly List<ScheduledSend> waiting = [];
    readonly Dictionary<ulong, TaskCompletionSource> ownershipCompletions = [];
    readonly Dictionary<ulong, (Action? Submitted, Action? Released)> sendObservers = [];
    public Action<ReadOnlyMemory<byte>, bool>? StateSendObserved { get; set; }
    readonly ReliableLaneFramer[] framers = [new(ScheduledLane.Control), new(ScheduledLane.Lifecycle), new(ScheduledLane.Bulk)];
    readonly TaskCompletionSource connected = new(TaskCreationOptions.RunContinuationsAsynchronously);
    readonly Task pump;
    Task? disposal;
    QuicTransportStatistics finalStatistics;
    bool terminal;
    int incomingBytes;
    ulong nextKey = 1;
    ulong controlSession, controlView;
    readonly int[] queuedLaneBytes = new int[4], queuedLaneItems = new int[4];
    public NativeGameConnection(NativeQuicEndpoint endpoint)
    {
        this.endpoint = endpoint;
        long origin = Stopwatch.GetTimestamp();
        scheduler = new(() => Stopwatch.GetElapsedTime(origin));
        scheduler.RegisterPeer(1);
        pump = Task.Run(Pump);
    }
    public Task Connected => connected.Task;
    public QuicTransportStatistics Statistics { get { lock (gate) return terminal ? finalStatistics : endpoint.GetStatistics(); } }
    public async ValueTask<GameLaneMessage> ReadAsync(CancellationToken cancellationToken = default)
    {
        var message = await incoming.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        Interlocked.Add(ref incomingBytes, -message.Payload.Length);
        return message;
    }
    public bool TrySend(QuicLane lane, ReadOnlySpan<byte> body, Action? submitted = null, Action? released = null)
    {
        lock (gate)
        {
            int length = body.Length + (lane == QuicLane.State ? 0 : 4);
            int chunk = lane == QuicLane.State ? 1150 : NativeQuicEndpoint.MaximumChunkBytes;
            int count = (length + chunk - 1) / chunk;
            if (queuedLaneBytes[(int)lane] + length > 240 * 1024 || queuedLaneItems[(int)lane] + count > (lane == QuicLane.Control ? 64 : 256)) return false;
            Send(lane, body, submitted: submitted, released: released); return true;
        }
    }
    public int QueuedLifecycleBytes { get { lock (gate) return queuedLaneBytes[(int)QuicLane.Lifecycle]; } }
    public bool TrySendLifecyclePublication(IReadOnlyList<byte[]> frames, Action? submitted = null, Action? released = null)
    {
        lock (gate)
        {
            long bytes = frames.Sum(frame => frame.Length + 4L);
            int items = frames.Sum(frame => (frame.Length + 4 + NativeQuicEndpoint.MaximumChunkBytes - 1) / NativeQuicEndpoint.MaximumChunkBytes);
            // Admission is atomic across the complete framed publication. Small critical batches do
            // not sit behind a queue of ordinary UI state; that state uses the independent bulk lane.
            if (bytes > 64 * 1024 || queuedLaneBytes[(int)QuicLane.Lifecycle] + bytes > 64 * 1024 ||
                queuedLaneItems[(int)QuicLane.Lifecycle] + items > 256) return false;
            for (int index = 0; index < frames.Count; index++)
                Send(QuicLane.Lifecycle, frames[index], submitted: index == 0 ? submitted : null, released: index + 1 == frames.Count ? released : null);
            return true;
        }
    }
    public void Send(QuicLane lane, ReadOnlySpan<byte> body, ulong stateKey = 0, Action? submitted = null, Action? released = null) => Enqueue(lane, body, stateKey, false, submitted, released);
    Task Enqueue(QuicLane lane, ReadOnlySpan<byte> body, ulong stateKey, bool observeOwnership, Action? submittedObserver = null, Action? releasedObserver = null)
    {
        var completions = new List<Task>();
        byte[] bytes = lane == QuicLane.State ? body.ToArray() : framers[(int)lane].Encode(body);
        lock (gate)
        {
            stop.Token.ThrowIfCancellationRequested();
            var scheduledLane = Map(lane);
            int chunk = lane == QuicLane.State ? 1150 : NativeQuicEndpoint.MaximumChunkBytes;
            for (int offset = 0; offset < bytes.Length; offset += chunk)
            {
                byte[] part = bytes.AsSpan(offset, Math.Min(chunk, bytes.Length - offset)).ToArray();
                ulong key = lane == QuicLane.State ? stateKey : nextKey++;
                var outcome = scheduler.Enqueue(1, scheduledLane, key, part.Length, () => part);
                if (outcome is not (ScheduleOutcome.Queued or ScheduleOutcome.Coalesced))
                    throw new IOException($"Connection {lane} queue capacity exhausted ({outcome}).");
                if (submittedObserver is not null || releasedObserver is not null)
                    sendObservers[key] = (offset == 0 ? submittedObserver : null, offset + chunk >= bytes.Length ? releasedObserver : null);
                if (lane != QuicLane.State) { queuedLaneBytes[(int)lane] += part.Length; queuedLaneItems[(int)lane]++; }
                if (observeOwnership)
                {
                    var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    ownershipCompletions.Add(key, completion); completions.Add(completion.Task);
                }
            }
            NotifyPump();
        }
        return Task.WhenAll(completions);
    }
    void NotifyPump()
    {
        if (signal.CurrentCount == 0)
            try { signal.Release(); } catch (SemaphoreFullException) { } catch (ObjectDisposedException) { }
    }
    public T WithScheduler<T>(Func<BandwidthScheduler, T> operation) { lock (gate) return operation(scheduler); }
    public void SetControlEpoch(ulong session, ulong view)
    {
        lock (gate)
        {
            if (session == 0 || view == 0 || (controlSession != 0 && (session != controlSession || view < controlView))) throw new InvalidDataException("Invalid authenticated control epoch.");
            controlSession = session; controlView = view;
        }
    }
    byte[] EncodeControl(WireFrame frame)
    {
        lock (gate)
        {
            if (frame.Kind == WireMessage.ViewBegin) { var epoch = GameViewProtocol.ReadEpoch(frame); SetControlEpoch(epoch.Session, epoch.View); }
            return GameControlCodec.Encode(frame, controlSession, controlView);
        }
    }
    /// <summary>Small protocol metadata leaves descriptor credit for user operations. False retains
    /// the caller's cumulative ACK rather than filling the business-operation queue.</summary>
    public bool TrySendProtocolControl(WireFrame frame)
    {
        lock (gate)
        {
            if (queuedLaneItems[(int)QuicLane.Control] >= 1 || queuedLaneBytes[(int)QuicLane.Control] >= 32 * 1024) return false;
            return TrySend(QuicLane.Control, EncodeControl(frame));
        }
    }
    public Task SendControlAsync(WireFrame frame)
    {
        lock (gate) return Enqueue(QuicLane.Control, EncodeControl(frame), 0, true);
    }
    public void SendControl(WireFrame frame)
    {
        lock (gate) Send(QuicLane.Control, EncodeControl(frame));
    }
    public WireFrame DecodeControl(ReadOnlySpan<byte> bytes)
    {
        var frame = GameControlCodec.Decode(bytes);
        lock (gate)
        {
            if (frame.Kind == WireMessage.Authenticated && controlSession == 0) SetControlEpoch(frame.SessionEpoch, frame.ViewEpoch);
            else if (frame.Kind == WireMessage.ViewBegin)
            {
                var epoch = GameViewProtocol.ReadEpoch(frame);
                if (frame.SessionEpoch != epoch.Session || frame.ViewEpoch != epoch.View) throw new InvalidDataException("View fence header/body epoch disagreement.");
                SetControlEpoch(epoch.Session, epoch.View);
            }
            if (controlSession == 0)
            {
                if (frame.SessionEpoch != 0 || frame.ViewEpoch != 0) throw new InvalidDataException("Epoch preceded authentication.");
            }
            else if (frame.SessionEpoch != controlSession || frame.ViewEpoch == 0 || frame.ViewEpoch > controlView || (!endpoint.IsServer && frame.ViewEpoch != controlView))
                throw new InvalidDataException("Control epoch mismatch.");
        }
        return frame;
    }
    public async ValueTask<WireFrame> ReadControlAsync(CancellationToken token = default)
    {
        var message = await ReadAsync(token).ConfigureAwait(false);
        if (message.Lane != QuicLane.Control) throw new InvalidDataException("Control handshake expected.");
        return DecodeControl(message.Payload.Span);
    }
    static ScheduledLane Map(QuicLane lane) => lane switch
    { QuicLane.Control => ScheduledLane.Control, QuicLane.Lifecycle => ScheduledLane.Lifecycle, QuicLane.Bulk => ScheduledLane.Bulk, QuicLane.State => ScheduledLane.State, _ => throw new InvalidDataException("Invalid lane.") };
    static QuicLane Map(ScheduledLane lane) => lane switch
    { ScheduledLane.Control => QuicLane.Control, ScheduledLane.Lifecycle => QuicLane.Lifecycle, ScheduledLane.Bulk => QuicLane.Bulk, _ => QuicLane.State };
    void Receive(QuicLane lane, ReadOnlyMemory<byte> bytes)
    {
        if (Interlocked.Add(ref incomingBytes, bytes.Length) > 512 * 1024 || !incoming.Writer.TryWrite(new(lane, bytes)))
            throw new IOException("Connection receive queue capacity exhausted.");
    }
    async Task Pump()
    {
        byte[] buffer = new byte[NativeQuicEndpoint.MaximumChunkBytes];
        try
        {
            while (!stop.IsCancellationRequested)
            {
                bool progressed = false;
                int events = 0;
                while (events < 128 && endpoint.TryPoll(buffer, out var item))
                {
                    events++;
                    progressed = true;
                    switch (item.Kind)
                    {
                        case QuicTransportEventKind.Connected:
                            if (!endpoint.IsServer) endpoint.OpenReliableLanes();
                            connected.TrySetResult();
                            break;
                        case QuicTransportEventKind.StreamBytes:
                            framers[(int)item.Lane].Feed(buffer.AsSpan(0, item.Length), bytes => Receive(item.Lane, bytes));
                            break;
                        case QuicTransportEventKind.Datagram: Receive(QuicLane.State, buffer.AsSpan(0, item.Length).ToArray()); break;
                        case QuicTransportEventKind.SendBufferReleased:
                        case QuicTransportEventKind.DatagramTerminal:
                            lock (gate) if (submitted.Remove(item.SendToken, out var send))
                            {
                                scheduler.CompleteSend(send);
                                if (send.Lane == ScheduledLane.State) Observe(() => StateSendObserved?.Invoke(send.Payload, true));
                                else if (sendObservers.Remove(send.Key, out var observed)) Observe(observed.Released);
                                if (send.Lane != ScheduledLane.State)
                                {
                                    queuedLaneBytes[(int)Map(send.Lane)] -= send.Payload.Length;
                                    queuedLaneItems[(int)Map(send.Lane)]--;
                                }
                                if (send.Lane == ScheduledLane.Control && ownershipCompletions.Remove(send.Key, out var completion)) completion.TrySetResult();
                            }
                            break;
                        case QuicTransportEventKind.StreamReceiveEnded:
                            framers[(int)item.Lane].Complete();
                            throw new EndOfStreamException("Reliable game lane closed.");
                        case QuicTransportEventKind.Closed:
                        case QuicTransportEventKind.ShutdownInitiated: throw new IOException($"QUIC connection closed ({item.NativeStatus:X8}).");
                    }
                }
                lock (gate)
                {
                    // Native UDP payload counters are actual traffic, but omit IP overhead; expose that limitation to callers.
                    finalStatistics = endpoint.GetStatistics();
                    while (scheduler.TryAdmit(out var next)) waiting.Add(next!);
                    for (int index = 0; index < waiting.Count;)
                    {
                        var send = waiting[index];
                        if (!endpoint.TrySend(Map(send.Lane), send.Payload, send.Reservation)) { index++; continue; }
                        progressed = true;
                        submitted.Add(send.Reservation, send); waiting.RemoveAt(index);
                        if (send.Lane == ScheduledLane.State) Observe(() => StateSendObserved?.Invoke(send.Payload, false));
                        else if (sendObservers.TryGetValue(send.Key, out var observed)) Observe(observed.Submitted);
                    }
                }
                if (progressed) await Task.Yield();
                else await signal.WaitAsync(TimeSpan.FromMilliseconds(2), stop.Token).ConfigureAwait(false);
            }
        }
        catch (Exception error)
        {
            connected.TrySetException(error);
            incoming.Writer.TryComplete(error);
            await stop.CancelAsync().ConfigureAwait(false);
        }
        finally
        {
            lock (gate)
            {
                try { finalStatistics = endpoint.GetStatistics(); } catch (Exception) { /* preserve original terminal failure */ }
                terminal = true; endpoint.Dispose();
            }
            incoming.Writer.TryComplete();
            lock (gate) { foreach (var completion in ownershipCompletions.Values) completion.TrySetException(new IOException("Native game connection closed before send ownership release.")); ownershipCompletions.Clear(); }
        }
    }
    static void Observe(Action? observer) { try { observer?.Invoke(); } catch { /* diagnostics cannot affect transport */ } }
    public void Close()
    {
        lock (gate) if (!terminal) { stop.Cancel(); NotifyPump(); }
    }
    public ValueTask DisposeAsync()
    {
        lock (gate) return new(disposal ??= DisposeCore());
    }
    async Task DisposeCore()
    {
        await stop.CancelAsync().ConfigureAwait(false);
        await pump.ConfigureAwait(false);
        signal.Dispose();
        stop.Dispose();
    }
}

/// <summary>Typed business DTO envelope on the v21 control stream; deliberately excludes old transport state messages.</summary>
public static class GameControlCodec
{
    public static byte[] Encode(WireFrame frame, ulong sessionEpoch = 0, ulong viewEpoch = 0)
    {
        Validate(frame.Kind);
        if (frame.Payload.Length > 16 * 1024 - 26) throw new InvalidDataException("Control payload exceeds lane bound.");
        byte[] body = new byte[frame.Payload.Length + 22];
        "MTC2"u8.CopyTo(body);
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(4), (ushort)frame.Kind);
        BinaryPrimitives.WriteUInt64LittleEndian(body.AsSpan(6), sessionEpoch);
        BinaryPrimitives.WriteUInt64LittleEndian(body.AsSpan(14), viewEpoch);
        frame.Payload.Span.CopyTo(body.AsSpan(22));
        return body;
    }
    public static WireFrame Decode(ReadOnlySpan<byte> body)
    {
        if (body.Length < 22 || body.Length > 16 * 1024 - 4 || !body[..4].SequenceEqual("MTC2"u8)) throw new InvalidDataException("Invalid v21 control body.");
        var kind = (WireMessage)BinaryPrimitives.ReadUInt16LittleEndian(body[4..]); Validate(kind);
        return new(kind, body[22..].ToArray()) { SessionEpoch = BinaryPrimitives.ReadUInt64LittleEndian(body[6..]), ViewEpoch = BinaryPrimitives.ReadUInt64LittleEndian(body[14..]) };
    }
    static void Validate(WireMessage kind)
    {
        if (!Enum.IsDefined(kind) || kind is WireMessage.StateChunk or WireMessage.StateAck or WireMessage.StateWithdrawn)
            throw new InvalidDataException("Invalid v21 control kind.");
    }
}
