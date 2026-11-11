using Meitou.Networking;
using Meitou.Simulation;

namespace Meitou.Game.Host;
public sealed partial class RemotePlayerSession
{
    readonly Dictionary<(CharacterId Speaker, CharacterId Target), TalkHintEntry> _talkHints = [];
    double _nextTalkQueryAt;
    sealed class TalkHintEntry(TalkHintQuery request, Task<TalkHintReply> pending)
    {
        public TalkHintReply? Reply;
        public bool Completed;
        public double CompletedAt, UsedAt;
        public TalkHintQuery Request { get; } = request;
        public Task<TalkHintReply> Pending { get; } = pending;
    }

    public bool TalkHintPending(CharacterId target, CharacterId? speaker = null)
    {
        if (!CanControl || speaker is not { } actor || !_view!.Snapshot.Characters.Any(c => c.Id == target) || !_view.Roles.Any(r => r.Character == actor && r.Available && !r.Dead))
            return false;
        _ = CanTalk(target, actor);
        return !_talkHints.TryGetValue((actor, target), out var hint) || !hint.Completed || hint.Reply?.Hint.Status is not (TalkHintStatus.Available or TalkHintStatus.Unavailable);
    }

    /// <summary>
    /// Nonblocking hover query: unknown/pending is unavailable. Results expire and actions still undergo server validation.
    /// </summary>
    public bool CanTalk(CharacterId target, CharacterId? speaker = null)
    {
        if (!CanControl || speaker is not { } actor || !_view!.Snapshot.Characters.Any(c => c.Id == target))
            return false;
        var role = _view.Roles.FirstOrDefault(r => r.Character == actor && r.Available && !r.Dead);
        if (role is null || _view.Factions.FirstOrDefault(f => f.Faction == role.Faction)is not { } member)
            return false;
        var grant = new ExpectedRoleGrant(role.Entity, role.Faction, role.Revision);
        var key = (actor, target);
        if (_talkHints.TryGetValue(key, out var cached))
        {
            cached.UsedAt = _elapsed;
            if (cached.Request.Grant == grant && cached.Request.MembershipRevision == member.MembershipRevision && (!cached.Completed || _elapsed - cached.CompletedAt < .5))
                return cached.Reply?.Hint.Status == TalkHintStatus.Available;
            if (!cached.Completed)
                return false;
            _talkHints.Remove(key);
        }

        if (_elapsed < _nextTalkQueryAt || _talkHints.Values.Count(h => !h.Completed) >= 16)
            return false;
        if (_talkHints.Count == 64)
        {
            var oldest = _talkHints.Where(p => p.Value.Completed).MinBy(p => p.Value.UsedAt);
            _talkHints.Remove(oldest.Key);
        }

        _nextTalkQueryAt = _elapsed + .1; // at most ten new hover queries per second
        var query = new TalkHintQuery(Guid.NewGuid(), actor, target, grant, member.MembershipRevision);
        try
        {
            _talkHints.Add(key, new(query, _connection.TalkHintAsync(query)) { UsedAt = _elapsed });
        }
        catch (Exception error)when (error is InvalidOperationException or IOException or ArgumentException)
        {
        }

        return false;
    }

    void PumpTalkHints()
    {
        foreach (var hint in _talkHints.Values)
        {
            if (hint.Completed || !hint.Pending.IsCompleted)
                continue;
            hint.Completed = true;
            hint.CompletedAt = _elapsed;
            try
            {
                var reply = hint.Pending.GetAwaiter().GetResult();
                var query = hint.Request;
                if (reply.Query == query.Query && reply.Speaker == query.Speaker && reply.Target == query.Target && reply.GrantRevision == query.Grant.Revision && reply.MembershipRevision == query.MembershipRevision)
                    hint.Reply = reply;
            }
            catch (Exception error)when (error is IOException or OperationCanceledException or InvalidDataException)
            {
            }
        }
    }
}