namespace Meitou.Simulation.Bodies;

/// <summary>
/// Tick-boundary controller activity shared by hunger, feeding and food fetching. Worker phases only read the captured state.
/// </summary>
internal sealed class HungerActivity
{
    /// <summary>
    /// The current connection generations that finished admission, copied together under the authority lock.
    /// </summary>
    readonly HashSet<PlayerId> _activePlayers = [];

    /// <summary>
    /// Recent offline spans by character slot; cold-object identity prevents a reused slot from inheriting protection.
    /// </summary>
    SlotHistory?[] _slots = [];

    /// <summary>
    /// Captures validated controllers after inputs, before any parallel body updates or automatic feeding.
    /// </summary>
    internal void Capture(World world)
    {
        if (!world.Players.RequiresExplicitControl) return;

        world.Authority.CopyActivePlayers(_activePlayers);
        var table = world.Characters;
        if (_slots.Length < table.HighWater) Array.Resize(ref _slots, table.HighWater);
        long oldest = world.Tick - Math.Max(1, world.SpeedSteps) + 1;

        for (int slot = 0; slot < table.HighWater; slot++)
        {
            if (!table.Next[slot].Alive || table.Cold(slot) is not { } cold)
            {
                _slots[slot] = null;
                continue;
            }

            bool paused = world.Players.KnownControl(cold.Entity) is { } grant && world.Players.Validate(grant)
                && !_activePlayers.Contains(grant.Player);
            var history = _slots[slot];
            if (history is not null && !ReferenceEquals(history.Cold, cold)) history = null;
            if (paused) history ??= new SlotHistory(cold);
            history?.Capture(world.Tick, oldest, paused);
            _slots[slot] = history is { Paused: false, Spans.Count: 0 } ? null : history;
        }
    }

    /// <summary>
    /// Whether this character's current controller was inactive at the tick boundary.
    /// </summary>
    internal bool IsPaused(int slot, CharacterCold cold) => slot < _slots.Length
        && _slots[slot] is { Paused: true } history && ReferenceEquals(history.Cold, cold);

    /// <summary>
    /// Removes offline ticks from a batched medical step without changing its injury, blood or knockout time.
    /// </summary>
    internal float ElapsedSeconds(World world, int slot, CharacterCold cold, float seconds)
    {
        if (slot >= _slots.Length || _slots[slot] is not { } history || !ReferenceEquals(history.Cold, cold)) return seconds;
        if (history.Paused) return 0;

        long first = world.Tick - (long)MathF.Round(seconds / world.TickSeconds) + 1;
        long pausedTicks = 0;
        foreach (var span in history.Spans)
            pausedTicks += Math.Max(0, Math.Min(world.Tick, span.Last) - Math.Max(first, span.First) + 1);

        return MathF.Max(0, seconds - pausedTicks * world.TickSeconds);
    }

    /// <summary>
    /// An inclusive run of inactive controller ticks.
    /// </summary>
    readonly record struct OfflineSpan(long First, long Last);

    /// <summary>
    /// Only the short history a batched body step can cover; multiplayer disables the world's far cadence.
    /// </summary>
    sealed class SlotHistory(CharacterCold cold)
    {
        /// <summary>
        /// The exact character occupying this slot.
        /// </summary>
        internal readonly CharacterCold Cold = cold;

        /// <summary>
        /// Inactive spans retained for the current body cadence.
        /// </summary>
        internal readonly List<OfflineSpan> Spans = [];

        /// <summary>
        /// The controller's captured inactive state.
        /// </summary>
        internal bool Paused;

        /// <summary>
        /// Extends an inactive run and discards time no subsequent medical step can charge.
        /// </summary>
        internal void Capture(long tick, long oldest, bool paused)
        {
            Paused = paused;
            int expired = 0;
            while (expired < Spans.Count && Spans[expired].Last < oldest) expired++;
            if (expired > 0) Spans.RemoveRange(0, expired);

            if (Spans.Count > 0 && Spans[0].First < oldest) Spans[0] = Spans[0] with { First = oldest };
            if (!paused) return;

            if (Spans.Count > 0 && Spans[^1].Last == tick - 1) Spans[^1] = Spans[^1] with { Last = tick };
            else Spans.Add(new OfflineSpan(tick, tick));
        }
    }
}
