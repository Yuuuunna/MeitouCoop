using System.Runtime.CompilerServices;

namespace Meitou.Simulation.Editing;

/// <summary>
/// Transient, server-issued permission to finish one dialogue editor. Never restored from a save.
/// </summary>
public static class CharacterEditSessions
{
    internal sealed record Session(Guid Id, PlayerId Player, CharacterId Character, CharacterId Surgeon,
        EditKind Kind, CharacterControl? Control, int Faction, ulong Membership, bool Paid, bool RenameOnly = false)
    {
        public bool Applied;
    }
    static readonly ConditionalWeakTable<World, Dictionary<Guid, Session>> Sessions = new();

    public static Guid Open(World world, PlayerId? issuer, CharacterId character, CharacterId surgeon, EditKind kind, bool paid)
    {
        CloseForPlayer(world, issuer ?? PlayerId.Local);
        return OpenCore(world, issuer, character, surgeon, kind, paid, false);
    }

    public static IReadOnlyDictionary<CharacterId, Guid> OpenAnimals(World world, PlayerId? issuer, IReadOnlyList<CharacterId> characters)
    {
        CloseForPlayer(world, issuer ?? PlayerId.Local);
        var result = new Dictionary<CharacterId, Guid>();
        foreach (var character in characters.Distinct())
        {
            var id = OpenCore(world, issuer, character, CharacterId.None, EditKind.Recruit, true, true);
            if (id != Guid.Empty) result[character] = id;
        }
        return result;
    }

    static Guid OpenCore(World world, PlayerId? issuer, CharacterId character, CharacterId surgeon, EditKind kind, bool paid, bool renameOnly)
    {
        if (kind is not (EditKind.Surgery or EditKind.Recruit) || !world.Characters.TryResolveNext(character, out int slot)
            || !world.Characters.IsAlive(character) || world.Characters.Cold(slot) is not { IsPlayer: true, Appearance: { } look } cold
            || renameOnly != (look.Animal is not null))
            return Guid.Empty;
        var actor = issuer ?? PlayerId.Local;
        var control = world.Players.ControlOf(character);
        if ((issuer is not null || world.Players.RequiresExplicitControl) && (control is null || control.Value.Player != actor)) return Guid.Empty;
        if (kind == EditKind.Surgery && (!world.Characters.TryResolveNext(surgeon, out int npc) || !world.Characters.IsAlive(surgeon)
            || System.Numerics.Vector3.Distance(world.Characters.Next[slot].Position, world.Characters.Next[npc].Position) > CharacterEditing.SurgeonReach)) return Guid.Empty;
        var session = new Session(Guid.NewGuid(), actor, character, surgeon, kind, control, cold.Faction,
            world.Players.Find(actor)?.MembershipRevision(cold.Faction) ?? 0, paid, renameOnly);
        Sessions.GetOrCreateValue(world)[session.Id] = session;
        return session.Id;
    }

    public static void CloseForPlayer(World world, PlayerId player)
    {
        var all = Sessions.GetOrCreateValue(world);
        foreach (var id in all.Where(pair => pair.Value.Player == player).Select(pair => pair.Key).ToArray()) all.Remove(id);
    }

    internal static Session? Find(World world, EditCharacter edit)
    {
        var actor = edit.Issuer ?? PlayerId.Local;
        if (edit.SessionId == Guid.Empty || !Sessions.GetOrCreateValue(world).TryGetValue(edit.SessionId, out var session)
            || session.Player != actor || session.RenameOnly && edit.Appearance is not null || session.Id != edit.SessionId || session.Character != edit.Character || session.Surgeon != edit.Surgeon || session.Kind != edit.Kind) return null;
        if (session.Control is { } grant && (!world.Players.Validate(grant)
            || world.Players.Find(actor)?.MembershipRevision(session.Faction) != session.Membership)) return null;
        return session;
    }
}