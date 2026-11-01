using Meitou.Networking;
using Meitou.Simulation;

namespace Meitou.Engine;

public sealed partial class RemotePlayerSession
{
    sealed class TalkHintEntry(TalkHintQuery request, Task<TalkHintReply> pending)
    {
        public TalkHintQuery Request { get; } = request;
        public Task<TalkHintReply> Pending { get; } = pending;
        public TalkHintReply? Reply;
        public bool Completed;
        public double CompletedAt, UsedAt;
    }
    readonly Dictionary<(CharacterId Speaker, CharacterId Target), TalkHintEntry> talkHints = [];
    double nextTalkQueryAt;

    public bool TalkHintPending(CharacterId target, CharacterId? speaker = null)
    {
        if (!CanControl || speaker is not { } actor || !view!.Snapshot.Characters.Any(c => c.Id == target) ||
            !view.Roles.Any(r => r.Character == actor && r.Available && !r.Dead)) return false;
        _ = CanTalk(target, actor);
        return !talkHints.TryGetValue((actor, target), out var hint) || !hint.Completed ||
            hint.Reply?.Hint.Status is not (TalkHintStatus.Available or TalkHintStatus.Unavailable);
    }

    /// <summary>Nonblocking hover query: unknown/pending is unavailable. Results expire and actions still undergo server validation.</summary>
    public bool CanTalk(CharacterId target, CharacterId? speaker = null)
    {
        if (!CanControl || speaker is not { } actor || !view!.Snapshot.Characters.Any(c => c.Id == target)) return false;
        var role = view.Roles.FirstOrDefault(r => r.Character == actor && r.Available && !r.Dead);
        if (role is null || view.Factions.FirstOrDefault(f => f.Faction == role.Faction) is not { } member) return false;
        var grant = new ExpectedRoleGrant(role.Entity, role.Faction, role.Revision);
        var key = (actor, target);
        if (talkHints.TryGetValue(key, out var cached))
        {
            cached.UsedAt = elapsed;
            if (cached.Request.Grant == grant && cached.Request.MembershipRevision == member.MembershipRevision &&
                (!cached.Completed || elapsed - cached.CompletedAt < .5))
                return cached.Reply?.Hint.Status == TalkHintStatus.Available;
            if (!cached.Completed) return false;
            talkHints.Remove(key);
        }
        if (elapsed < nextTalkQueryAt || talkHints.Values.Count(h => !h.Completed) >= 16) return false;
        if (talkHints.Count == 64)
        {
            var oldest = talkHints.Where(p => p.Value.Completed).MinBy(p => p.Value.UsedAt);
            talkHints.Remove(oldest.Key);
        }
        nextTalkQueryAt = elapsed + .1; // at most ten new hover queries per second
        var query = new TalkHintQuery(Guid.NewGuid(), actor, target, grant, member.MembershipRevision);
        try { talkHints.Add(key, new(query, connection.TalkHintAsync(query)) { UsedAt = elapsed }); }
        catch (Exception error) when (error is InvalidOperationException or IOException or ArgumentException) { }
        return false;
    }

    void PumpTalkHints()
    {
        foreach (var hint in talkHints.Values)
        {
            if (hint.Completed || !hint.Pending.IsCompleted) continue;
            hint.Completed = true; hint.CompletedAt = elapsed;
            try
            {
                var reply = hint.Pending.GetAwaiter().GetResult();
                var query = hint.Request;
                if (reply.Query == query.Query && reply.Speaker == query.Speaker && reply.Target == query.Target &&
                    reply.GrantRevision == query.Grant.Revision && reply.MembershipRevision == query.MembershipRevision)
                    hint.Reply = reply;
            }
            catch (Exception error) when (error is IOException or OperationCanceledException or InvalidDataException) { }
        }
    }
}
