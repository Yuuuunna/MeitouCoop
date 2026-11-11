using System.Diagnostics;
using Meitou.Simulation.Dialogue;
using Meitou.Simulation;

namespace Meitou.Game.Ui;

/// <summary>
/// Deferred talk clicks whose eligibility is still pending, fenced by world identity and current speaker visibility.
/// </summary>
sealed class TalkRequests(PlayerInterface player)
{
    /// <summary>
    /// A pending click, its speaker, selection, hostility and wall-clock expiry.
    /// </summary>
    sealed record PendingTalk(object World, CharacterSnapshot Target, CharacterId Speaker, CharacterId[] Selection, bool Quick, bool WasHostile, long Started);

    /// <summary>
    /// The latest deferred talk click; another command or pointer press cancels it.
    /// </summary>
    PendingTalk? _pendingTalk;

    /// <summary>
    /// Whether talk is available or still awaiting an eligibility answer.
    /// </summary>
    internal bool TalkOffered(CharacterId target, CharacterId speaker) => player.Session.CanTalk(target, speaker) || player.Session.TalkHintPending(target, speaker);

    /// <summary>
    /// Submits a talk intent when eligible, otherwise waits for its pending hint.
    /// </summary>
    internal void RequestTalk(CharacterId speaker, CharacterId target)
    {
        if (player.Session.CanTalk(target, speaker)) player.Say(new TalkOrder(speaker, target) { Tick = player.Session.CommandTick });
        else if (player.Session.TalkHintPending(target, speaker) && player.Session.CurrentSnapshot.Characters.FirstOrDefault(c => c.Id == target) is { } character)
            WaitForTalkHint(character, speaker, [speaker], quick: false);
        else player.Notice("This character cannot talk right now.");
    }

    /// <summary>
    /// Remembers a pending click without issuing an attack fallback.
    /// </summary>
    internal void WaitForTalkHint(CharacterSnapshot target, CharacterId speaker, IReadOnlyList<CharacterId> selected, bool quick)
        => _pendingTalk = new(player.Session.WorldIdentity, target, speaker, selected.ToArray(), quick, player.PointerPanel.Hostile(target), Stopwatch.GetTimestamp());

    /// <summary>
    /// Resolves a pending hint within two seconds while its world, target and controlled speaker remain current.
    /// </summary>
    internal void Poll()
    {
        if (_pendingTalk is not { } pending) return;
        if (!ReferenceEquals(pending.World, player.Session.WorldIdentity))
        {
            _pendingTalk = null;
            return;
        }
        var snapshot = player.Session.CurrentSnapshot;
        if (snapshot.Characters.FirstOrDefault(c => c.Id == pending.Target.Id) is not { IsPlayer: false } target ||
            target.Faction != pending.Target.Faction || target.Body is { Dead: true } or { Unconscious: true } || !target.CarriedBy.IsNone ||
            !snapshot.Characters.Any(c => c.Id == pending.Speaker && c.IsPlayer && c.Body is not ({ Dead: true } or { Unconscious: true })))
        {
            _pendingTalk = null;
            return;
        }
        if (Stopwatch.GetElapsedTime(pending.Started) > TimeSpan.FromSeconds(2))

        {
            _pendingTalk = null;
            player.Notice("This character cannot talk right now.");
            return;
        }
        bool available = player.Session.CanTalk(target.Id, pending.Speaker);
        if (player.Session.TalkHintPending(target.Id, pending.Speaker)) return;
        _pendingTalk = null;
        if (!pending.Quick)
        {
            if (available) player.Say(new TalkOrder(pending.Speaker, target.Id) { Tick = player.Session.CommandTick });
            else player.Notice("This character cannot talk right now.");
        }
        else if (pending.WasHostile || !player.PointerPanel.Hostile(target)) player.QuickOrderFor(target, pending.Selection)?.Do?.Invoke();
    }

    /// <summary>
    /// Drops the pending talk click.
    /// </summary>
    internal void Cancel() => _pendingTalk = null;

}