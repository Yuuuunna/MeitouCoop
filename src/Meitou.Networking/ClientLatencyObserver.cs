using System.Numerics;
using Meitou.Diagnostics;
using Meitou.Simulation;

namespace Meitou.Networking;

/// <summary>Presentation sidecar. A pose event identifies the actual source view, never the receive thread's newer view.</summary>
public sealed class ClientLatencyObserver(ILatencyTraceSink sink)
{
    void Emit(LatencyTraceEvent value) => LatencyTrace.Emit(sink, value with { Endpoint = LatencyEndpoint.Client });
    const int Capacity = 4096;
    readonly Lock gate = new();
    readonly Dictionary<ulong, Sample> samples = [];
    sealed class Sample(LatencyTraceEvent key, CharacterId character, Vector3 start)
    {
        public readonly LatencyTraceEvent Key = key;
        public readonly CharacterId Character = character;
        public readonly Vector3 Start = start;
        public bool Received;
        public ulong LastAppliedPacket;
    }

    public void Intent(ReplicationIdentity identity, ulong request, SimCommand command, ExpectedCommandAuthority expected, PlayerWorldView? view)
    { lock (gate) IntentCore(identity, request, command, expected, view); }
    void IntentCore(ReplicationIdentity identity, ulong request, SimCommand command, ExpectedCommandAuthority expected, PlayerWorldView? view)
    {
        var key = new LatencyTraceEvent(LatencyStage.Intent, identity.World, identity.Player.Value, identity.Generation, request,
            Tick: view?.Snapshot.Tick ?? 0);
        if (command is not MoveOrder) return;
        foreach (var role in expected.Roles)
        {
            var entityKey = key with { Entity = role.Entity.Value };
            Emit(entityKey);
            if (samples.Remove(role.Entity.Value, out var old))
                Emit(old.Key with { Stage = LatencyStage.Terminal, Outcome = LatencyOutcome.Superseded });
            var character = view?.Roles.FirstOrDefault(r => r.Entity == role.Entity);
            var snapshot = character is null ? null : view?.Snapshot.Characters.FirstOrDefault(c => c.Id == character.Character);
            if (samples.Count >= Capacity || snapshot is null)
            {
                Emit(entityKey with { Stage = LatencyStage.Terminal, Outcome = samples.Count >= Capacity ? LatencyOutcome.Capacity : LatencyOutcome.Censored });
                continue;
            }
            samples[role.Entity.Value] = new Sample(entityKey, snapshot.Id, snapshot.Position);
        }
    }

    public void PublicationApplied(PlayerWorldView view, Func<CharacterId, (ulong Publication, long Tick, ulong PacketSequence)?>? motionSource = null)
    { lock (gate) PublicationAppliedCore(view, motionSource); }
    void PublicationAppliedCore(PlayerWorldView view, Func<CharacterId, (ulong Publication, long Tick, ulong PacketSequence)?>? motionSource)
    {
        Emit(new(LatencyStage.PublicationApplied, view.World, view.Player.Value, view.ConnectionGeneration,
            Publication: view.Publication, Tick: view.Snapshot.Tick));
        foreach (var sample in samples.Values)
        {
            var source = motionSource?.Invoke(sample.Character);
            if (source is { PacketSequence: not 0 } packet && packet.PacketSequence != sample.LastAppliedPacket)
            {
                sample.LastAppliedPacket = packet.PacketSequence;
                Emit(sample.Key with { Stage = LatencyStage.PublicationApplied, Publication = packet.Publication,
                    Tick = packet.Tick, Value = unchecked((long)packet.PacketSequence) });
            }
            if (sample.Received) continue;
            var c = view.Snapshot.Characters.FirstOrDefault(c => c.Id == sample.Character);
            if (c is null || c.Position == sample.Start) continue;
            if (motionSource is not null && source is null) continue;
            sample.Received = true;
            Emit(sample.Key with { Stage = LatencyStage.ClientRootChanged, Publication = source?.Publication ?? view.Publication, Tick = source?.Tick ?? view.Snapshot.Tick, Value = unchecked((long)(source?.PacketSequence ?? 0)) });
        }
    }

    public void RendererRootSubmitted(PlayerWorldView source, CharacterId character, Vector3 root, long frame, double presentationTick, ulong packetSequence = 0)
    { lock (gate) RendererRootSubmittedCore(source, character, root, frame, presentationTick, packetSequence); }
    void RendererRootSubmittedCore(PlayerWorldView source, CharacterId character, Vector3 root, long frame, double presentationTick, ulong packetSequence)
    {
        var role = source.Roles.FirstOrDefault(r => r.Character == character);
        if (role is null || !samples.TryGetValue(role.Entity.Value, out var sample) || !sample.Received || root == sample.Start) return;
        if (sample.Key.World != source.World || sample.Key.Connection != source.Player.Value || sample.Key.Generation != source.ConnectionGeneration) return;
        Emit(sample.Key with { Stage = LatencyStage.RendererRootSubmitted, Publication = source.Publication,
            Tick = source.Snapshot.Tick, Frame = frame, PresentationTick = presentationTick, Value = unchecked((long)packetSequence) });
        samples.Remove(role.Entity.Value);
    }

    public void Receipt(ReplicationIdentity identity, AuthorityReceipt receipt)
    { lock (gate) ReceiptCore(identity, receipt); }
    void ReceiptCore(ReplicationIdentity identity, AuthorityReceipt receipt)
    {
        Emit(new(LatencyStage.ReceiptReceived, identity.World, identity.Player.Value, identity.Generation,
            receipt.Request, Tick: receipt.Tick, Revision: receipt.Revision, Value: (long)receipt.Status));
        if (receipt.Status == AuthorityStatus.Evaluated) return;
        foreach (var pair in samples.Where(p => p.Value.Key.Request == receipt.Request).ToArray())
        {
            Emit(pair.Value.Key with { Stage = LatencyStage.Terminal, Outcome = LatencyOutcome.Rejected, Value = (long)receipt.Status });
            samples.Remove(pair.Key);
        }
    }

    public void NotDispatched(ulong request)
    {
        lock (gate) foreach (var pair in samples.Where(pair => pair.Value.Key.Request == request).ToArray())
        {
            Emit(pair.Value.Key with { Stage = LatencyStage.Terminal, Outcome = LatencyOutcome.Censored, Value = 1 });
            samples.Remove(pair.Key);
        }
    }
    public void Close()
    { lock (gate) CloseCore(); }
    void CloseCore()
    {
        foreach (var sample in samples.Values)
            Emit(sample.Key with { Stage = LatencyStage.Terminal, Outcome = LatencyOutcome.Censored });
        samples.Clear();
    }
}
