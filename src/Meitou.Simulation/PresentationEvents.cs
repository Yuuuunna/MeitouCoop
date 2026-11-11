using System.Numerics;

namespace Meitou.Simulation;

public sealed record FloaterEvent(ulong Id, Floater Value);
public sealed record SoundEventEntry(ulong Id, SoundEvent Value);
public sealed record SpeechLogEvent(ulong Id, CharacterId Speaker, SaidLine Line);
public sealed record BloodVisualEvent(ulong Id, BloodVisual Value);
public sealed record BlockSparkEvent(ulong Id, CharacterId Who);
public sealed record PresentationEventBatch(ulong HighWater, IReadOnlyList<FloaterEvent> Floaters)
{
    public IReadOnlyList<SoundEventEntry> Sounds { get; init; } = [];
    public IReadOnlyList<SpeechLogEvent> Speech { get; init; } = [];
    public IReadOnlyList<BloodVisualEvent> Blood { get; init; } = [];
    public IReadOnlyList<BlockSparkEvent> Sparks { get; init; } = [];
    public static PresentationEventBatch Empty { get; } = new(0, []);
}

/// <summary>
/// Bounded transient display journal, independent of simulation hashes and checkpoint history.
/// </summary>
internal sealed class PresentationEventJournal(TimeProvider? clock = null)
{
    internal const int Capacity = 512;
    static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(3);
    readonly TimeProvider _clock = clock ?? TimeProvider.System;
    readonly object _gate = new();
    readonly Queue<Entry> _entries = [];
    ulong _sequence;
    sealed record SpeechData(CharacterId Speaker, SaidLine Line);
    sealed record Entry(ulong Id, long Born, Floater? Floater, SoundEvent? Sound, PlayerId? TargetOwner, PlayerId? SourceOwner,
        SpeechData? Speech = null, BloodVisual? Blood = null, CharacterId? Spark = null);

    internal void Add(BloodVisual value)
    {
        if (value.Mark.Race is { Length: > 4096 }) throw new ArgumentException("Blood effect race identity is too long.", nameof(value));
        lock (_gate)
        {
            Trim(); if (_entries.Count == Capacity) _entries.Dequeue();
            _entries.Enqueue(new(checked(++_sequence), _clock.GetTimestamp(), null, null, null, null, Blood: value));
        }
    }
    internal void AddSpark(CharacterId who)
    {
        lock (_gate)
        {
            Trim(); if (_entries.Count == Capacity) _entries.Dequeue();
            _entries.Enqueue(new(checked(++_sequence), _clock.GetTimestamp(), null, null, null, null, Spark: who));
        }
    }

    internal void Add(Floater value, PlayerId? targetOwner, PlayerId? sourceOwner)
    {
        if (value.Part.Length > 4096) value = value with { Part = value.Part[..4096] };
        lock (_gate)
        {
            Trim();
            if (_entries.Count == Capacity) _entries.Dequeue();
            _entries.Enqueue(new(checked(++_sequence), _clock.GetTimestamp(), value, null, targetOwner, sourceOwner));
        }
    }
    internal void Add(SoundEvent value, PlayerId? owner)
    {
        value = value with { Item = value.Kind == SoundKind.ShacklesOn ? value.Item is { Length: > 4096 } item ? item[..4096] : value.Item : null };
        lock (_gate)
        {
            Trim();
            if (_entries.Count == Capacity) _entries.Dequeue();
            _entries.Enqueue(new(checked(++_sequence), _clock.GetTimestamp(), null, value, owner, null));
        }
    }
    internal void Add(CharacterId speaker, SaidLine line, PlayerId? owner)
    {
        if (line.Owner.Length > 256) line = line with { Owner = line.Owner[..256] };
        if (line.Text.Length > 4096) line = line with { Text = line.Text[..4096] };
        lock (_gate)
        {
            Trim();
            if (_entries.Count == Capacity) _entries.Dequeue();
            _entries.Enqueue(new(checked(++_sequence), _clock.GetTimestamp(), null, null, owner, null, new(speaker, line)));
        }
    }
    void Trim()
    {
        long now = _clock.GetTimestamp();
        while (_entries.TryPeek(out var first) && _clock.GetElapsedTime(first.Born, now) > Lifetime) _entries.Dequeue();
    }
    internal PresentationEventBatch Capture(PlayerId player, IReadOnlySet<CharacterId> visible, Func<Vector3, bool> nearby)
    {
        lock (_gate)
        {
            Trim();
            var floaters = new List<FloaterEvent>(); var sounds = new List<SoundEventEntry>(); var speech = new List<SpeechLogEvent>();
            var blood = new List<BloodVisualEvent>(); var sparks = new List<BlockSparkEvent>();
            foreach (var e in _entries)
            {
                if (e.Blood is { } mark && nearby(mark.Mark.Position))
                    blood.Add(new(e.Id, mark with { Mark = mark.Mark with { Who = visible.Contains(mark.Mark.Who) ? mark.Mark.Who : CharacterId.None } }));
                if (e.Spark is { } who && visible.Contains(who)) sparks.Add(new(e.Id, who));
                if (e.Floater is { } floater && (floater.Kind != FloaterKind.Text || e.TargetOwner == player) &&
                    (floater.Target.IsNone ? nearby(floater.Position) : visible.Contains(floater.Target)))
                    floaters.Add(new(e.Id, floater with { TargetIsPlayer = e.TargetOwner == player, SourceIsPlayer = e.SourceOwner == player }));
                if (e.Speech is { } spoken && visible.Contains(spoken.Speaker))
                    speech.Add(new(e.Id, spoken.Speaker, spoken.Line with { Player = e.TargetOwner == player }));
                if (e.Sound is not { } sound) continue;
                bool personal = sound.Kind is SoundKind.ShacklesOn or SoundKind.BountyPaid or SoundKind.DismantleDone;
                bool positional = sound.Kind is SoundKind.BoltStuck or SoundKind.TurretShot or SoundKind.WallBroken;
                if (personal ? e.TargetOwner != player : !nearby(sound.Position) || !positional && !visible.Contains(sound.Subject)) continue;
                sounds.Add(new(e.Id, sound with
                {
                    Subject = visible.Contains(sound.Subject) ? sound.Subject : CharacterId.None,
                    Other = visible.Contains(sound.Other) ? sound.Other : CharacterId.None,
                }));
            }
            return new(_sequence, floaters.ToArray()) { Sounds = sounds.ToArray(), Speech = speech.ToArray(), Blood = blood.ToArray(), Sparks = sparks.ToArray() };
        }
    }
}

public sealed partial class World
{
    readonly PresentationEventJournal _presentationEvents = new();
    internal bool HasFloaterSink => Floaters is not null || Players.RequiresExplicitControl;
    internal bool HasSoundSink => Sounds is not null || Players.RequiresExplicitControl;
    internal PlayerId? PresentationOwner(CharacterId character)
        => Characters.TryResolveNext(character, out int slot) && Characters.Cold(slot) is { } cold
            ? Players.KnownControl(cold.Entity)?.Player : null;

    /// <summary>
    /// Authoritative producers only. Text is private to the target's controller; hits/labels require authorized visibility.
    /// </summary>
    public void EmitFloater(Floater value, PlayerId? sourcePlayer = null)
    {
        Floaters?.Enqueue(value);
        if (Players.RequiresExplicitControl) _presentationEvents.Add(value, PresentationOwner(value.Target), sourcePlayer);
    }
    public void SaidPublic(CharacterId speaker, string owner, string text, bool player)
    {
        if (text.Length == 0) return;
        if (!Players.RequiresExplicitControl) Said(owner, text, player);
        else _presentationEvents.Add(speaker, new SaidLine(owner, text.ReplaceLineEndings(" "), player), PresentationOwner(speaker));
    }
}