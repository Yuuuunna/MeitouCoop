using Meitou.Simulation;

namespace Meitou.Engine;

public sealed partial class RemotePlayerSession
{
    readonly Queue<BloodVisual> bloodVisuals = [];
    readonly Queue<CharacterId> blockSparks = [];
    bool bloodEnabled, sparksEnabled;
    ulong bloodFloor, sparkFloor;
    public void EnableBlood(bool enabled)
    {
        if (enabled && !bloodEnabled) bloodFloor = connection.State.View?.Events.HighWater ?? eventHighWater;
        bloodEnabled = enabled; if (!enabled) bloodVisuals.Clear();
    }
    public void EnableBlockSparks(bool enabled)
    {
        if (enabled && !sparksEnabled) sparkFloor = connection.State.View?.Events.HighWater ?? eventHighWater;
        sparksEnabled = enabled; if (!enabled) blockSparks.Clear();
    }
    public bool TryDequeueBlood(out BloodVisual blood)
    {
        blood = default;
        if (!HasWorldView) { ClearCombatVisuals(); return false; }
        return bloodVisuals.TryDequeue(out blood);
    }
    public bool TryDequeueBlockSpark(out CharacterId who)
    {
        who = default;
        if (!HasWorldView) { ClearCombatVisuals(); return false; }
        return blockSparks.TryDequeue(out who);
    }
    void ClearCombatVisuals() { bloodVisuals.Clear(); blockSparks.Clear(); }
    void ReceiveCombatVisuals(PresentationEventBatch events)
    {
        if (!eventBaseline) return;
        if (bloodEnabled)
            foreach (var e in events.Blood.Where(e => e.Id > eventHighWater && e.Id > bloodFloor).OrderBy(e => e.Id))
            {
                if (bloodVisuals.Count == 128) bloodVisuals.Dequeue(); bloodVisuals.Enqueue(e.Value);
            }
        if (sparksEnabled)
            foreach (var e in events.Sparks.Where(e => e.Id > eventHighWater && e.Id > sparkFloor).OrderBy(e => e.Id))
            {
                if (blockSparks.Count == 128) blockSparks.Dequeue(); blockSparks.Enqueue(e.Who);
            }
    }
}
