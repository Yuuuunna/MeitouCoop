using System.Diagnostics;
using Meitou.Simulation;
using Meitou.Simulation.Dialogue;

namespace Meitou.Game;

sealed partial class PlayerInterface
{
    sealed record PendingTalk(object World, CharacterSnapshot Target, CharacterId Speaker, CharacterId[] Selection, bool Quick, bool WasHostile, long Started);
    PendingTalk? pendingTalk;

    bool TalkOffered(CharacterId target, CharacterId speaker) => session.CanTalk(target, speaker) || session.TalkHintPending(target, speaker);

    void RequestTalk(CharacterId speaker, CharacterId target)
    {
        if (session.CanTalk(target, speaker)) Say(new TalkOrder(speaker, target) { Tick = session.CommandTick });
        else if (session.TalkHintPending(target, speaker) && session.CurrentSnapshot.Characters.FirstOrDefault(c => c.Id == target) is { } character)
            WaitForTalkHint(character, speaker, [speaker], quick: false);
        else Notice("This character cannot talk right now.");
    }

    void WaitForTalkHint(CharacterSnapshot target, CharacterId speaker, IReadOnlyList<CharacterId> selected, bool quick)
        => pendingTalk = new(session.WorldIdentity, target, speaker, selected.ToArray(), quick, Hostile(target), Stopwatch.GetTimestamp());

    internal void PollPendingTalk()
    {
        if (pendingTalk is not { } pending) return;
        if (!ReferenceEquals(pending.World, session.WorldIdentity)) { pendingTalk = null; return; }
        var snapshot = session.CurrentSnapshot;
        if (snapshot.Characters.FirstOrDefault(c => c.Id == pending.Target.Id) is not { IsPlayer: false } target ||
            target.Faction != pending.Target.Faction || target.Body is { Dead: true } or { Unconscious: true } || !target.CarriedBy.IsNone ||
            !snapshot.Characters.Any(c => c.Id == pending.Speaker && c.IsPlayer && c.Body is not ({ Dead: true } or { Unconscious: true }))) { pendingTalk = null; return; }
        if (Stopwatch.GetElapsedTime(pending.Started) > TimeSpan.FromSeconds(2))
        { pendingTalk = null; Notice("This character cannot talk right now."); return; }
        bool available = session.CanTalk(target.Id, pending.Speaker);
        if (session.TalkHintPending(target.Id, pending.Speaker)) return;
        pendingTalk = null;
        if (!pending.Quick)
        {
            if (available) Say(new TalkOrder(pending.Speaker, target.Id) { Tick = session.CommandTick });
            else Notice("This character cannot talk right now.");
        }
        else if (pending.WasHostile || !Hostile(target)) QuickOrderFor(target, pending.Selection)?.Do?.Invoke();
    }
}
