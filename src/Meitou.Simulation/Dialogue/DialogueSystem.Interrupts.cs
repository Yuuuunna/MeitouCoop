namespace Meitou.Simulation.Dialogue;

public sealed partial class DialogueSystem
{
    readonly SortedDictionary<(ulong Player, CharacterId Npc), SortedDictionary<int, string>> playerInterrupts = new(
        Comparer<(ulong Player, CharacterId Npc)>.Create((a, b) => a.Player != b.Player ? a.Player.CompareTo(b.Player) : CompareIds(a.Npc, b.Npc)));

    void SetInterrupt(Conversation context, CharacterId npc, string line)
    {
        if (context.Actor is { } actor)
        {
            if (!playerInterrupts.TryGetValue((actor.Value, npc), out var factions)) playerInterrupts.Add((actor.Value, npc), factions = []);
            factions[context.Faction] = line;
        }
        else interrupts[npc] = line; // legacy or an unscoped world-script follow-up
    }

    public bool HasInterrupt(CharacterId npc, PlayerId? player, int? faction = null)
        => player is { } actor && playerInterrupts.TryGetValue((actor.Value, npc), out var factions) &&
            (faction is null || factions.ContainsKey(faction.Value)) || interrupts.ContainsKey(npc);

    bool TakeInterrupt(CharacterId npc, PlayerId? player, int faction, out string line)
    {
        if (player is { } actor && playerInterrupts.TryGetValue((actor.Value, npc), out var factions) && factions.Remove(faction, out line!))
        {
            if (factions.Count == 0) playerInterrupts.Remove((actor.Value, npc));
            return true;
        }
        return interrupts.Remove(npc, out line!);
    }
}
