namespace Meitou.Simulation.Dialogue;

public sealed partial class DialogueSystem
{
    readonly SortedDictionary<(ulong Player, CharacterId Npc), SortedDictionary<int, string>> _playerInterrupts = new(
        Comparer<(ulong Player, CharacterId Npc)>.Create((a, b) => a.Player != b.Player ? a.Player.CompareTo(b.Player) : CompareIds(a.Npc, b.Npc)));

    void SetInterrupt(Conversation context, CharacterId npc, string line)
    {
        if (context.Actor is { } actor)
        {
            if (!_playerInterrupts.TryGetValue((actor.Value, npc), out var factions)) _playerInterrupts.Add((actor.Value, npc), factions = []);
            factions[context.Faction] = line;
        }
        else _interrupts[npc] = line; // legacy or an unscoped world-script follow-up
    }

    public bool HasInterrupt(CharacterId npc, PlayerId? player, int? faction = null)
        => player is { } actor && _playerInterrupts.TryGetValue((actor.Value, npc), out var factions) &&
            (faction is null || factions.ContainsKey(faction.Value)) || _interrupts.ContainsKey(npc);

    bool TakeInterrupt(CharacterId npc, PlayerId? player, int faction, out string line)
    {
        if (player is { } actor && _playerInterrupts.TryGetValue((actor.Value, npc), out var factions) && factions.Remove(faction, out line!))
        {
            if (factions.Count == 0) _playerInterrupts.Remove((actor.Value, npc));
            return true;
        }
        return _interrupts.Remove(npc, out line!);
    }
}