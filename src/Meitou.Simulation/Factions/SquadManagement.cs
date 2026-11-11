using System.Numerics;
using Meitou.Data;
using Meitou.Data.Fcs;
using Meitou.Simulation.Ai;
using Meitou.Simulation.Movement;
using Meitou.Simulation.Player;
using Meitou.Simulation.Population;

namespace Meitou.Simulation.Factions;

/// <summary>
/// A change to the player's squads (docs/game/ui-screens.md "Squads tab"), applied by <see cref="SquadManagement"/>.
/// </summary>
public abstract record SquadCommand : FactionCommand;

/// <summary>
/// NEW SQUAD: an empty squad of the player's faction at <paramref name="At"/> (the camera's focus).
/// </summary>
public sealed record NewSquadCommand(Vector3 At) : SquadCommand;

/// <summary>
/// A squad box's name was edited (at most <see cref="SquadManagement.NameLength"/> characters).
/// </summary>
public sealed record RenameSquadCommand(int Squad, string Name) : SquadCommand;

/// <summary>
/// DELETE: removes an empty squad that is not the player's last.
/// </summary>
public sealed record RemoveSquadCommand(int Squad) : SquadCommand;

/// <summary>
/// A squad box dropped in the squads list: the squad takes place <paramref name="Index"/> in the order (-1: the end).
/// </summary>
public sealed record ReorderSquadCommand(int Squad, int Index) : SquadCommand;

/// <summary>
/// The squad bar shows <paramref name="Squad"/> (a squad tab); -1 is the original's change_squad (Tab): the next squad in order that has members.
/// </summary>
public sealed record ActiveSquadCommand(int Squad) : SquadCommand;

/// <summary>
/// A portrait dropped on DRAG HERE TO DISMISS and confirmed: the character leaves the player's faction.
/// </summary>
public sealed record DismissCommand(CharacterId Character) : SquadCommand;

/// <summary>
/// The player's squads (docs/game/ui-screens.md "Squads tab", from the original's <c>SquadManagementScreen</c>): their order, the active squad, and
/// the squad screen's commands with the original's limits and refusals (CONSTANTS <c>max squads</c>, <c>max squad size</c>, <c>max faction size</c>).
/// A player squad's first member leads it.
/// </summary>
public static class SquadManagement
{
    /// <summary>
    /// The squad name box's limit (<c>setMaxTextLength(0x20)</c>, <b>Observed</b>).
    /// </summary>
    public const int NameLength = 32;
    /// <summary>
    /// The faction a dismissed character joins (<c>1233-gamedata.base</c>, Tech Hunters), else <c>nofac</c>; and the squad template of its new squad
    /// ("Wandering Squad").
    /// </summary>
    public const string DismissFaction = "1233-gamedata.base", DismissFallbackFaction = "nofac", DismissTemplate = "5006-squad.mod";

    /// <summary>
    /// The record name of the player faction's squad of the dead (<c>FUN_1407f4960</c>; <see cref="Squad.Dead"/>).
    /// </summary>
    public const string DeadSquadName = "__DEAD_SQUAD__";

    /// <summary>
    /// The player's own squads in order: of the player's faction, not a platoon's active half and not the squad of the dead.
    /// </summary>
    public static List<Squad> PlayerSquads(World world) => PlayerSquads(world, world.Player.Faction);

    /// <summary>
    /// The player's characters alive (what CONSTANTS <c>max faction size</c> counts).
    /// </summary>
    public static int FactionHeads(World world) => FactionHeads(world, world.Player.Faction);

    public static Squad NewSquad(World world, GameDatabase db, Vector3 at, string factionName, string? templateId)
        => NewSquad(world, db, at, factionName, world.Player.Faction, templateId);

    /// <summary>
    /// Keeps the active squad one of the player's with members: when it is gone, or has none, the first in order with members (the squad tab refresh,
    /// <c>FUN_140416140</c>, <b>Verified</b>); when no squad has members the active one stays, or the first when it is gone.
    /// </summary>
    internal static void KeepActive(World world)
    {
        foreach (var player in world.Players.All) KeepActive(world, player.State);
    }

    /// <summary>
    /// Applies a player's squad command: new, rename, remove, move to, reorder, make active, or dismiss.
    /// </summary>
    internal static void Apply(World world, PopulationData? data, SquadCommand command)
    {
        if (world.CommandPlayer(command) is not { } player) return;
        var permission = command is ActiveSquadCommand ? FactionPermission.None
            : command is MoveToSquadCommand or DismissCommand ? FactionPermission.AssignCharacters : FactionPermission.ManageSquads;
        if (world.CommandFaction(command, permission) is not { Index: >= 0 } state) return;
        int faction = state.Index;
        var constants = data?.Bodies.Constants;
        void Notice(string text) => world.Post(command.Issuer ?? PlayerId.Local, text);
        switch (command)
        {
            case NewSquadCommand create:
                if (data is null || !float.IsFinite(create.At.X) || !float.IsFinite(create.At.Y) || !float.IsFinite(create.At.Z)) return;

                // The original counts the squad boxes against `max squads` (FUN_140491b50).
                if (PlayerSquads(world, faction).Count >= constants!.MaxSquads)
                {
                    Notice("Maximum number of squads reached.");
                    return;
                }
                NewSquad(world, data.Db, create.At, FactionName(world, data, faction), faction);
                break;
            case RenameSquadCommand rename:
                if (Own(world, faction, rename.Squad) is { } named)
                    named.Name = rename.Name.Length > NameLength ? rename.Name[..NameLength] : rename.Name;
                break;
            case RemoveSquadCommand remove:
                if (Own(world, faction, remove.Squad) is not { } doomed) return;

                // FUN_140491d20: an empty squad only, and never the last.
                if (doomed.Members.Count > 0 ||
                    world.StoredPlayerRoles?.PendingCount(doomed.Id) > 0)
                {
                    Notice("The squad needs to be empty before it can be removed.");
                    return;
                }
                if (PlayerSquads(world, faction).Count == 1)
                {
                    Notice("You must have at least one squad.");
                    return;
                }
                world.Squads.Remove(doomed.Id);
                world.SystemOf<PopulationSystem>()?.ForgetSquad(doomed.Id);
                Renumber(PlayerSquads(world, faction));
                KeepActive(world);
                break;
            case MoveToSquadCommand move:
                Move(world, faction, constants?.MaxSquadSize ?? int.MaxValue, move);
                break;
            case ReorderSquadCommand reorder:
                if (Own(world, faction, reorder.Squad) is not { } moved) return;
                var order = PlayerSquads(world, faction);
                order.Remove(moved);
                order.Insert(reorder.Index < 0 || reorder.Index > order.Count ? order.Count : reorder.Index, moved);
                Renumber(order);
                break;
            case ActiveSquadCommand active:
                if (active.Squad >= 0)
                {
                    if (Own(world, faction, active.Squad) is { } shown) { player.Faction = faction; player.Squad = shown.Id; }
                }
                else { player.Faction = faction; NextSquad(world, player); }
                break;
            case DismissCommand dismiss:
                if (data is not null && world.MayOrder(command, dismiss.Character)) Dismiss(world, data, dismiss.Character, faction, command.Issuer);
                break;
        }
    }

    /// <summary>
    /// The player's squad of id <paramref name="id"/>; null when there is none.
    /// </summary>
    static Squad? Own(World world, int faction, int id) => world.Squads.Find(id) is { } s && Mine(world, faction, s) ? s : null;

    /// <summary>
    /// Whether a squad is the player's: of the player's faction, no platoon's and not the squad of the dead.
    /// </summary>
    static bool Mine(World world, int faction, Squad s) => s.Faction == faction && s.PlatoonId < 0 && !s.Dead &&
        (!world.Players.RequiresExplicitControl || s.PlayerManaged);

    /// <summary>
    /// The death handler's player rule (<c>FUN_1407a6200</c>): a character of the player's faction that has died leaves its squad (the next member
    /// leads) for the faction's squad of the dead, made the first time (<c>FUN_1407f4960</c>: the factory with no template, record name
    /// <see cref="DeadSquadName"/>). NPC squads keep their dead. Serial.
    /// </summary>
    internal static void BuryDead(World world, GameDatabase? db)
    {
        var table = world.Characters;
        foreach (int faction in world.PlayerFactionIndices())
        {
            Squad? dead = null;
            foreach (var squad in PlayerSquads(world, faction))
            {
                bool moved = false;
                for (int i = squad.Members.Count - 1; i >= 0; i--)
                {
                    var id = squad.Members[i];
                    if (!table.TryResolveNext(id, out int slot) || table.Cold(slot) is not { DiedTick: >= 0 } cold
                        || cold.Faction != faction) continue;
                    dead ??= DeadSquad(world, db, table.Next[slot].Position, faction);
                    squad.Members.RemoveAt(i);
                    dead.Members.Add(id);
                    cold.SquadId = dead.Id;
                    moved = true;
                }
                if (moved) Relead(table, squad);
            }
        }
    }

    /// <summary>
    /// The player faction's squad of the dead, made at <paramref name="at"/> the first time.
    /// </summary>
    static Squad DeadSquad(World world, GameDatabase? db, Vector3 at, int faction)
    {
        foreach (var s in world.Squads.All)
            if (s.Dead && s.Faction == faction) return s;
        var template = db?.OfType(FcsRecordType.SquadTemplate).FirstOrDefault(r => r.BaseName == "blank squad");
        var squad = new Squad
        {
            Id = world.Squads.NextId(),
            TemplateId = template?.StringId ?? "blank squad",
            Name = DeadSquadName,
            Dead = true,
            PlayerManaged = true,
            Faction = faction,
            Position = at,
            HomeCentre = new Vector2(at.X, at.Z),
            HomeRadius = 100,
        };
        world.Squads.Add(squad);
        return squad;
    }

    /// <summary>
    /// The player faction's name; <c>Nameless</c> when it has none.
    /// </summary>
    static string FactionName(World world, PopulationData data, int faction) =>
        world.Factions.Find(faction)?.Name
        ?? ((uint)faction < (uint)data.Factions.Count && data.Factions[faction].Name is { Length: > 0 } name ? name : "Nameless");

    /// <summary>
    /// Numbers the squads' <see cref="Squad.Index"/> in the list's order.
    /// </summary>
    static void Renumber(List<Squad> order)
    {
        for (int i = 0; i < order.Count; i++) order[i].Index = i;
    }

    /// <summary>
    /// change_squad (<c>FUN_1407f6380</c>): with more than one squad, the next one after the active in order (wrapping) that has members; none:
    /// unchanged.
    /// </summary>
    static void NextSquad(World world, PlayerState player)
    {
        var squads = PlayerSquads(world, player.Faction);
        if (squads.Count < 2) return;
        int at = squads.FindIndex(s => s.Id == player.Squad);
        for (int k = 1; k < squads.Count; k++)
        {
            var next = squads[(at + k + squads.Count) % squads.Count];
            if (next.Members.Count > 0)
            {
                player.Squad = next.Id;
                return;
            }
        }
    }

    /// <summary>
    /// A portrait drop (<c>FUN_14049b050</c>): into another squad that already has <c>max squad size</c> members only with Shift; with Shift onto a
    /// member the two swap places (<c>FUN_140792e60</c> within a squad); else the character is put in at the place (<c>FUN_140796620</c>, the end
    /// when none).
    /// </summary>
    static void Move(World world, int faction, int maxSize, MoveToSquadCommand move)
    {
        var table = world.Characters;
        if (Own(world, faction, move.Squad) is not { } target || !table.TryResolveNext(move.Character, out int slot)
                || table.Cold(slot) is not { IsPlayer: true } cold
            || Own(world, faction, cold.SquadId) is not { } source) return;
        if (cold.Faction != faction || !world.MayOrder(move, move.Character)) return;
        if (world.StoredPlayerRoles?.PendingCount(source.Id) > 0 || world.StoredPlayerRoles?.PendingCount(target.Id) > 0) return;
        int from = source.Members.IndexOf(move.Character);
        if (from < 0) return;
        bool atMember = move.Index >= 0 && move.Index < target.Members.Count;
        if (!(move.Swap && atMember) && source != target && target.Members.Count >= maxSize) return;
        if (move.Issuer is not null && move.Swap && atMember && move.SwapWith != target.Members[move.Index]) return;
        if (move.Swap && atMember)
        {
            var other = target.Members[move.Index];
            if (!world.MayOrder(move, other)) return;
            if (source == target)
            {
                (source.Members[from], source.Members[move.Index]) = (other, move.Character);
                ResetRoles(table, source, Math.Min(from, move.Index), Math.Max(from, move.Index));
            }
            else
            {
                source.Members[from] = other;
                target.Members[move.Index] = move.Character;
                Join(table, other, source);
                Join(table, move.Character, target);

                // Engine choice: the original's cross-squad swap is two puts (FUN_140796620), which reset the roles from the put place on; here the
                // two swapped places.
                ResetRoles(table, source, from, from);
                ResetRoles(table, target, move.Index, move.Index);
            }
        }
        else
        {
            source.Members.RemoveAt(from);
            int at = move.Index < 0 || move.Index > target.Members.Count ? target.Members.Count : move.Index;
            target.Members.Insert(at, move.Character);
            if (source != target) Join(table, move.Character, target);
            ResetRoles(table, target, at, target.Members.Count - 1);
        }
        Relead(table, source);
        if (source != target) Relead(table, target);
    }

    /// <summary>
    /// Makes a character a member of <paramref name="squad"/>: its squad and no stop order left from the old one.
    /// </summary>
    static void Join(CharacterTable table, CharacterId id, Squad squad)
    {
        if (!table.TryResolveNext(id, out int slot)) return;
        var cold = table.Cold(slot)!;
        cold.SquadId = squad.Id;
    }

    /// <summary>
    /// Folds <paramref name="from"/> into <paramref name="into"/> and removes it (the refugee package's merge, docs/game/ai.md "Package classes": the
    /// original hands each member to the target squad, then removes the platoon from its faction): the members join <paramref name="into"/> in the
    /// squad role (a leader is a plain member again; slaves stay slaves), their AI minds are made again for it, and the emptied squad leaves the
    /// registry. Serial phases only, and not while the registry is being walked.
    /// </summary>
    internal static void Merge(World world, Squad from, Squad into)
    {
        var table = world.Characters;
        foreach (var id in from.Members.ToArray())
        {
            if (!table.TryResolveNext(id, out int slot)) continue;
            var cold = table.Cold(slot)!;
            cold.SquadId = into.Id;
            if (cold.Role == (int)SquadRole.Leader) cold.Role = (int)SquadRole.Squad1;
            cold.Ai = null;
            into.Members.Add(id);
        }
        from.Members.Clear();
        from.Leader = CharacterId.None;
        if (into.Leader.IsNone && into.Members.Count > 0) into.Leader = into.Members[0];
        world.Squads.Remove(from.Id);
        world.SystemOf<PopulationSystem>()?.ForgetSquad(from.Id);
    }

    /// <summary>
    /// A put or swap resets the member type of the places <paramref name="first"/> to <paramref name="last"/> to 0 (<c>FUN_140796620</c>,
    /// <c>FUN_140792e60</c>), so a Squad2 or Slave member dropped there, and whoever shifted behind it, becomes a plain member. Place 0 is left to
    /// <see cref="Relead"/>.
    /// </summary>
    static void ResetRoles(CharacterTable table, Squad squad, int first, int last)
    {
        for (int i = Math.Max(first, 1); i <= last && i < squad.Members.Count; i++)
            if (table.TryResolveNext(squad.Members[i], out int slot)) table.Cold(slot)!.Role = (int)SquadRole.Squad1;
    }

    /// <summary>
    /// The first member leads (role 2), the one that led before goes back to role 0 (<c>FUN_140796620</c> / <c>FUN_140792e60</c>: index 0 is made the
    /// leader).
    /// </summary>
    static void Relead(CharacterTable table, Squad squad)
    {
        squad.Leader = squad.Members.Count > 0 ? squad.Members[0] : CharacterId.None;
        for (int i = 0; i < squad.Members.Count; i++)
        {
            if (!table.TryResolveNext(squad.Members[i], out int slot)) continue;
            var cold = table.Cold(slot)!;
            if (i == 0) cold.Role = (int)SquadRole.Leader;
            else if (cold.Role == (int)SquadRole.Leader) cold.Role = (int)SquadRole.Squad1;
        }
    }

    /// <summary>
    /// Dismiss (<c>FUN_14049b050</c>, the confirmation's <c>FUN_14048fa30</c>): refused for the faction's only character ("Cannot dismiss the only
    /// remaining character in the faction."). The character is deselected, joins Tech Hunters (else No Faction) and leaves in a new squad of its own
    /// of the "Wandering Squad" template, whose AI packages then run it.
    /// </summary>
    static void Dismiss(World world, PopulationData data, CharacterId id, int ownerFaction, PlayerId? actor)
    {
        var table = world.Characters;
        if (!table.TryResolveNext(id, out int slot) || table.Cold(slot) is not { IsPlayer: true } cold || cold.Faction != ownerFaction) return;
        if (FactionHeads(world, ownerFaction) <= 1)
        {
            world.Post(actor ?? PlayerId.Local, "Cannot dismiss the only remaining character in the faction.");
            return;
        }
        int faction = data.FactionIndex(DismissFaction);
        if (faction < 0) faction = data.FactionIndex(DismissFallbackFaction);
        if (faction < 0) return;
        if (world.Squads.Find(cold.SquadId) is { } old)
        {
            old.Members.Remove(id);
            if (Own(world, ownerFaction, old.Id) is not null) Relead(table, old);
            else if (old.Leader == id) old.Leader = SquadRegistry.Successor(old.Members, table);
        }
        world.Players.RevokeControl(id);
        foreach (var player in world.Players.All) { player.State.Selection.Remove(id); player.State.InventoryShown.Remove(id); }
        var at = table.Next[slot].Position;
        var template = data.Db.Find(DismissTemplate);
        var squad = new Squad
        {
            Id = world.Squads.NextId(),
            TemplateId = template?.StringId ?? DismissTemplate,
            Name = template?.Name ?? "",
            Faction = faction,
            Position = at,
            HomeCentre = new Vector2(at.X, at.Z),
            HomeRadius = 100,
            SpawnSize = 1,
        };
        cold.Faction = faction;
        cold.RoadPreference = data.RoadPreferenceOf(faction);
        cold.SquadId = squad.Id;
        cold.Role = (int)SquadRole.Leader;
        cold.IsPlayer = false;
        cold.Orders = CharacterOrders.None;
        cold.Ai = null;
        cold.Engaged = false;
        cold.Fleeing = false;
        cold.Pending = null;
        cold.Jobs.Clear();
        cold.Save = null;   // no longer in the player's platoon file
        table.Next[slot].Mode = SpeedMode.Walk;
        TaskStart.Stand(table, slot);
        squad.Members.Add(id);
        squad.Leader = id;
        world.Squads.Add(squad);
        KeepActive(world);
    }

    /// <summary>
    /// A new, empty squad of the player's faction at <paramref name="at"/>, of the SQUAD_TEMPLATE "blank squad" (the original's <c>FUN_1407f4420</c> with no template; <paramref name="templateId"/>: that template), last in
    /// the order and named like the original's platoons, the faction's name and a number (<c>Nameless_1</c>).
    /// </summary>
    public static Squad NewSquad(World world, GameDatabase db, Vector3 at, string factionName)
        => NewSquad(world, db, at, factionName, world.Player.Faction);


    public static List<Squad> PlayerSquads(World world, int faction)
    {
        var list = new List<Squad>();
        if (faction < 0) return list;
        foreach (var s in world.Squads.All)
            if (Mine(world, faction, s)) list.Add(s);
        list.Sort((a, b) => a.Index != b.Index ? a.Index.CompareTo(b.Index) : a.Id.CompareTo(b.Id));
        return list;
    }


    public static Squad NewSquad(World world, GameDatabase db, Vector3 at, string factionName, int faction, string? templateId = null)
    {
        var template = templateId is not null ? db.Find(templateId) : db.OfType(FcsRecordType.SquadTemplate).FirstOrDefault(r => r.BaseName == "blank squad");
        var squads = PlayerSquads(world, faction);
        int n = squads.Count;
        while (squads.Any(s => s.Name == $"{factionName}_{n}")) n++;
        var squad = new Squad
        {
            Id = world.Squads.NextId(),
            TemplateId = template?.StringId ?? "blank squad",
            Name = $"{factionName}_{n}",
            PlayerManaged = true,
            Index = squads.Count == 0 ? 0 : squads[^1].Index + 1,
            Faction = faction,
            Position = at,
            HomeCentre = new Vector2(at.X, at.Z),
            HomeRadius = 100,
        };
        world.Squads.Add(squad);
        if (world.Players.RequiresExplicitControl) world.Factions.GetOrCreate(faction).PlayerOwned = true;
        return squad;
    }


    public static int FactionHeads(World world, int faction)
    {
        var table = world.Characters;
        int heads = 0;
        for (int i = 0; i < table.Current.Length; i++)
            if (table.Current[i].Alive && table.Cold(i) is { IsPlayer: true, DiedTick: < 0 } cold && cold.Faction == faction && cold.Medical?.Dead != true) heads++;
        return heads + (world.StoredPlayerRoles?.PendingFactionHeads(faction) ?? 0);
    }


    static void KeepActive(World world, PlayerState player)
    {
        if (!player.Exists) { player.Squad = -1; return; }
        if (world.Squads.Find(player.Squad) is { } active && Mine(world, player.Faction, active)
            && (world.Players.RequiresExplicitControl || active.Members.Count > 0)) return;
        var squads = PlayerSquads(world, player.Faction);
        player.Squad = (squads.FirstOrDefault(s => s.Members.Count > 0) ?? squads.FirstOrDefault())?.Id ?? -1;
    }

}

/// <summary>
/// A portrait dropped on a squad box: the character goes into <paramref name="Squad"/> at <paramref name="Index"/> (-1: the end). With <paramref name="Swap"/> (Shift held) and a
/// member at <paramref name="Index"/>, the two change places instead.
/// </summary>
public sealed record MoveToSquadCommand(CharacterId Character, int Squad, int Index = -1, bool Swap = false) : SquadCommand
{
    /// <summary>
    /// The displayed target member when swapping. Attributed swaps must name it explicitly.
    /// </summary>
    public CharacterId? SwapWith { get; init; }
}