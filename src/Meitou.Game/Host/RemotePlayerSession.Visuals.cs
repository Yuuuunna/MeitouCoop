using Meitou.Simulation;

namespace Meitou.Game.Host;
public sealed partial class RemotePlayerSession
{
    readonly Queue<BloodVisual> _bloodVisuals = [];
    readonly Queue<CharacterId> _blockSparks = [];
    bool _bloodEnabled, _sparksEnabled;
    ulong _bloodFloor, _sparkFloor;
    public void EnableBlood(bool enabled)
    {
        if (enabled && !_bloodEnabled)
            _bloodFloor = _connection.State.View?.Events.HighWater ?? _eventHighWater;
        _bloodEnabled = enabled;
        if (!enabled)
            _bloodVisuals.Clear();
    }

    public void EnableBlockSparks(bool enabled)
    {
        if (enabled && !_sparksEnabled)
            _sparkFloor = _connection.State.View?.Events.HighWater ?? _eventHighWater;
        _sparksEnabled = enabled;
        if (!enabled)
            _blockSparks.Clear();
    }

    public bool TryDequeueBlood(out BloodVisual blood)
    {
        blood = default;
        if (!HasWorldView)
        {
            ClearCombatVisuals();
            return false;
        }

        return _bloodVisuals.TryDequeue(out blood);
    }

    public bool TryDequeueBlockSpark(out CharacterId who)
    {
        who = default;
        if (!HasWorldView)
        {
            ClearCombatVisuals();
            return false;
        }

        return _blockSparks.TryDequeue(out who);
    }

    void ClearCombatVisuals()
    {
        _bloodVisuals.Clear();
        _blockSparks.Clear();
    }

    void ReceiveCombatVisuals(PresentationEventBatch events)
    {
        if (!_eventBaseline)
            return;
        if (_bloodEnabled)
            foreach (var e in events.Blood.Where(e => e.Id > _eventHighWater && e.Id > _bloodFloor).OrderBy(e => e.Id))
            {
                if (_bloodVisuals.Count == 128)
                    _bloodVisuals.Dequeue();
                _bloodVisuals.Enqueue(e.Value);
            }

        if (_sparksEnabled)
            foreach (var e in events.Sparks.Where(e => e.Id > _eventHighWater && e.Id > _sparkFloor).OrderBy(e => e.Id))
            {
                if (_blockSparks.Count == 128)
                    _blockSparks.Dequeue();
                _blockSparks.Enqueue(e.Who);
            }
    }
}