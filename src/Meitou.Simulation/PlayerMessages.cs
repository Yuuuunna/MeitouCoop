namespace Meitou.Simulation;

public enum PlayerMessageKind { Notice, Dialogue }
public sealed record PlayerMessage(ulong Id, PlayerMessageKind Kind, string Owner, string Text, bool Player = false);
public sealed record PlayerMessageBatch(ulong HighWater, IReadOnlyList<PlayerMessage> Items)
{
    public static PlayerMessageBatch Empty { get; } = new(0, []);
}

/// <summary>Recipient-owned, non-destructive UI history. Not gameplay state or a checkpoint record.</summary>
public sealed class PlayerMessageHistory
{
    readonly object gate = new();
    readonly Queue<PlayerMessage> entries = [];
    ulong sequence;
    PlayerMessageBatch? captured;
    internal void Add(PlayerMessageKind kind, string owner, string text, bool player = false)
    {
        if (text.Length == 0) return;
        if (owner.Length > 256) owner = owner[..256];
        if (text.Length > 4096) text = text[..4096];
        lock (gate)
        {
            if (entries.Count == 200) entries.Dequeue();
            entries.Enqueue(new(checked(++sequence), kind, owner, text, player));
            captured = null;
        }
    }
    public PlayerMessageBatch Capture()
    {
        lock (gate) return captured ??= new(sequence, entries.ToArray());
    }
}
