namespace Meitou.Simulation;

public enum PlayerMessageKind { Notice, Dialogue }
public sealed record PlayerMessage(ulong Id, PlayerMessageKind Kind, string Owner, string Text, bool Player = false);
public sealed record PlayerMessageBatch(ulong HighWater, IReadOnlyList<PlayerMessage> Items)
{
    public static PlayerMessageBatch Empty { get; } = new(0, []);
}

/// <summary>
/// Recipient-owned, non-destructive UI history. Not gameplay state or a checkpoint record.
/// </summary>
public sealed class PlayerMessageHistory
{
    readonly object _gate = new();
    readonly Queue<PlayerMessage> _entries = [];
    ulong _sequence;
    PlayerMessageBatch? _captured;
    internal void Add(PlayerMessageKind kind, string owner, string text, bool player = false)
    {
        if (text.Length == 0) return;
        if (owner.Length > 256) owner = owner[..256];
        if (text.Length > 4096) text = text[..4096];
        lock (_gate)
        {
            if (_entries.Count == 200) _entries.Dequeue();
            _entries.Enqueue(new(checked(++_sequence), kind, owner, text, player));
            _captured = null;
        }
    }
    public PlayerMessageBatch Capture()
    {
        lock (_gate) return _captured ??= new(_sequence, _entries.ToArray());
    }
}