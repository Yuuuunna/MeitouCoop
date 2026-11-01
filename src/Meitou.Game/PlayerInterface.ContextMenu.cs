using System.Globalization;
using System.Numerics;
using Meitou.Data.Ui;
using Meitou.Engine.Input;
using Meitou.Simulation;

namespace Meitou.Game;

/// <summary>
/// The context menu (the original's ContextMenuGUI, <c>Kenshi_ContextMenu</c>; docs/game/ui-input.md "Context menu verbs"): holding the command (right) button on a target for
/// <see cref="ContextHoldSeconds"/> opens a menu of the orders the selection can give it, titled with its name; a quicker click gives the default order at once. The menu lives while that button is held: letting it go over a
/// verb gives it, anywhere else closes the menu (<see cref="ContextMenuRelease"/>). The verbs carry the original's labels and are listed in the original's order-number order (<see cref="VerbOrders"/>);
/// one method per target kind builds them: characters here, containers and doors below, sites in PlayerInterface.Build.cs, rocks in PlayerInterface.Rocks.cs.
/// </summary>
sealed partial class PlayerInterface
{
    /// <summary>How long the command button is held on a target before the menu opens (<b>Observed</b>: 0.2 s, FUN_140800250).</summary>
    public const double ContextHoldSeconds = 0.2;

    sealed record ContextMenu(string Title, Vector2 At, IReadOnlyList<(string Caption, Action Give)> Verbs);
    ContextMenu? contextMenu;
    // A right press on a target that has verbs: its menu (built again when the hold ends), where, when, and the order a quick click gives (null: none).
    (Func<Vector2, ContextMenu?> Menu, Vector2 At, long Since, Action? Quick)? contextPending;

    public bool ContextMenuOpen => contextMenu is not null;

    /// <summary>
    /// The original's order number behind each verb caption (<c>FUN_14079b2d0</c>'s labels; where several orders share a label, the one the menu offers): the menu lists its verbs
    /// in ascending order number, as the original's set of orders. Meitou's own verbs (not in the table) follow, in the order they were added.
    /// </summary>
    static readonly Dictionary<string, int> VerbOrders = new()
    {
        ["Build"] = 2, ["Steal"] = 3, ["Attack Target"] = 5, ["Talk To"] = 12, ["Attack All"] = 16, ["First Aid"] = 25, ["Inventory"] = 26, ["Trade"] = 26, ["Loot"] = 26,
        ["Stand Up"] = 28, ["Follow"] = 31, ["Bodyguard"] = 45, ["Repairs"] = 60, ["Attack Unprovoked"] = 61, ["Put Down"] = 69, ["Open"] = 72, ["Close"] = 73, ["Pick Lock"] = 76,
        ["Lock"] = 77, ["Unlock"] = 78, ["Use"] = 87, ["Repair"] = 95, ["Dismantle"] = 96, ["Training"] = 97, ["Put in Bed"] = 99, ["Get in"] = 107, ["Put in"] = 108,
        ["Knock Out"] = 109, ["Set Free"] = 110, ["Break Out"] = 111, ["Auto-Haul"] = 124, ["Corpse Disposal"] = 136, ["Man Turret"] = 146, ["Capture"] = 166,
        ["Use Tools"] = 185, ["Use Strength"] = 186, ["Unlock Shackles"] = 201, ["Escape"] = 207, ["Throw out of home"] = 223, ["Pick Up"] = 225, ["Smash"] = 226,
        ["Stealth KO"] = 228, ["Stealth Kill"] = 229, ["Shoot At"] = 235, ["Kidnap"] = 246, ["Splint Injuries"] = 249, ["Escape (skill)"] = 251, ["Escape (strength)"] = 252,
        ["Sleep"] = 258, ["Throw out of town"] = 273,
    };

    /// <summary>The only orders an animals-only selection is offered (<c>FUN_1407f23b0</c>): attack, attack unprovoked, bodyguard, follow and the smash orders.</summary>
    static readonly HashSet<int> AnimalVerbOrders = [5, 61, 45, 31, 123, 226, 227];

    /// <summary>A caption with a chance column: the menu shows <paramref name="chance"/> (0..100) beside the verb as the original's <c>Value</c> box (<c>FUN_1407f65e0</c>).</summary>
    static string WithChance(string caption, float chance) => caption + "\t" + Math.Clamp(chance, 0, 100).ToString("0", CultureInfo.InvariantCulture);

    static (string Caption, float? Chance) SplitChance(string caption) =>
        caption.IndexOf('\t') is var tab and >= 0 && float.TryParse(caption.AsSpan(tab + 1), NumberStyles.Float, CultureInfo.InvariantCulture, out float c) ? (caption[..tab], c) : (caption, null);

    static int? OrderOf(string caption) => VerbOrders.TryGetValue(SplitChance(caption).Caption, out int order) ? order : null;

    /// <summary>The menu for <paramref name="verbs"/>, as the original fills it: an animals-only selection keeps only its orders, the rest sorted by order number; null when none is left.</summary>
    ContextMenu? Menu(string title, Vector2 at, List<(string Caption, Action Give)> verbs)
    {
        var selected = Selected();
        IEnumerable<(string Caption, Action Give)> kept = verbs;
        if (selected.Count > 0 && selected.All(IsAnimal)) kept = kept.Where(v => OrderOf(v.Caption) is { } o && AnimalVerbOrders.Contains(o));
        var list = kept.Select((v, i) => (v, i)).OrderBy(x => OrderOf(x.v.Caption) ?? int.MaxValue).ThenBy(x => x.i).Select(x => x.v).ToList();
        return list.Count == 0 ? null : new ContextMenu(title, at, list);
    }

    /// <summary>
    /// The verbs the selection has for a character (<c>FUN_1407a6bc0</c>, docs/game/ui-input.md "Context menu verbs"), in the original's labels. Prisoners, carried bodies, the
    /// player's own, hostile and other characters each get their branch's verbs; then the medic verbs and the shackle verbs.
    /// </summary>
    List<(string Caption, Action Give)> Verbs(CharacterSnapshot target)
    {
        var verbs = new List<(string, Action)>();
        var selection = SelectedIds();
        if (selection.Count == 0) return verbs;
        var id = target.Id;
        bool down = target.Body is { } b && (b.Dead || b.Unconscious);
        var carriers = Selected().Where(c => c.Carrying == id).Select(c => c.Id).ToArray();
        // A carried body: carried by a selected character, only Put Down (and Meitou's cage stand-in); carried by someone not of the player's, nothing.
        if (carriers.Length > 0)
        {
            verbs.Add(("Put Down", () => Say(new Meitou.Simulation.Carrying.PutDownOrder(carriers) { Tick = session.CommandTick })));
            // Meitou stand-in for the original's cage use (docs/simulation.md "Arrest and jail"): carry the body to the nearest free cage.
            verbs.Add(("Put in Cage", () => Say(new Meitou.Simulation.Crime.PutInCageOrder(carriers) { Tick = session.CommandTick })));
            // Meitou stand-in for the bed's own "Put in Bed" (PlayerInterface.Beds.cs): carry the body to the bed the bed search finds.
            verbs.Add(("Put in Bed", () => Say(new Meitou.Simulation.Locks.PutInBedOrder(carriers) { Tick = session.CommandTick })));
            return verbs;
        }
        if (!target.CarriedBy.IsNone && session.CurrentSnapshot.Characters.FirstOrDefault(c => c.Id == target.CarriedBy) is not { IsPlayer: true }) return verbs;
        if (target.Prison is { } prison) PrisonerVerbs(verbs, target, prison, selection, down);
        else if (target.IsPlayer) OwnCharacterVerbs(verbs, target, selection, down);
        else OtherCharacterVerbs(verbs, target, selection, down);
        if (!target.IsPlayer && !down && !target.Asleep && target.CarriedBy.IsNone && target.Prison is null
            && target.Controller is { } controller)
        {
            int paymentFaction = session.ActiveFaction;
            verbs.Add(("Trade with player", () => Say(new Meitou.Simulation.Trading.OpenPlayerTrade(selection[0], controller, id, paymentFaction) { Tick = session.CommandTick })));
        }
        // The medic verbs, unless it is dead or a hostile one standing (the original: the lead's enemy test, vtable +0x3e8).
        if (target.Body is { Dead: false } && (down || target.IsPlayer || !Hostile(target)))
        {
            if (target.Body is { NeedsAid: true }) verbs.Add(("First Aid", () => Say(new Meitou.Simulation.FirstAid.TreatOrder(selection, id) { Tick = session.CommandTick })));
            if (target.Body is { NeedsSplint: true }) verbs.Add(("Splint Injuries", () => Say(new Meitou.Simulation.FirstAid.SplintOrder(selection, id) { Tick = session.CommandTick })));
        }
        // Locked shackles (the original's verbs 201, 185, 186): pick the lock, saw them off with a tool, or break them by strength; the wearer's own or a mate's (another faction's: a crime).
        if (target.ShacklesLocked && target.Body is not { Dead: true })
        {
            verbs.Add(("Unlock Shackles", () => Say(new Meitou.Simulation.Locks.ShackleOrder(Workers(), id, Meitou.Simulation.Locks.ShackleWork.Pick) { Tick = session.CommandTick })));
            verbs.Add(("Use Tools", () => Say(new Meitou.Simulation.Locks.ShackleOrder(Workers(), id, Meitou.Simulation.Locks.ShackleWork.Cut) { Tick = session.CommandTick })));
            verbs.Add(("Use Strength", () => Say(new Meitou.Simulation.Locks.ShackleOrder(Workers(), id, Meitou.Simulation.Locks.ShackleWork.Break) { Tick = session.CommandTick })));
            // The wearer works its own shackles when it is selected, else the selection.
            IReadOnlyList<CharacterId> Workers() => selection.Contains(id) ? [id] : selection;
        }
        // Meitou stand-in for dragging shackles onto a down or caged character's armour slot: a selected character carrying some puts them on, locked.
        if (target.CanBeShackled && Selected().Any(c => c.CarriesShackles))
            verbs.Add(("Put on Shackles", () => Say(new Meitou.Simulation.Locks.PutShacklesOrder(Selected().Where(c => c.CarriesShackles).Select(c => c.Id).ToArray(), id) { Tick = session.CommandTick })));
        return [.. verbs.Select(v => (v.Item1, JobOrOrder(v.Item1, id, v.Item2)))];   // Shift turns an order into a job (PlayerInterface.SquadAi.cs)
    }

    /// <summary>
    /// A caged character: Talk To (12) and Loot (26). Meitou cannot point at a cage, so the cage's own verbs come here (engine stand-ins): Pick Lock its locked cage (the prisoner its own, a mate from outside), and
    /// once it is open Escape (207, the prisoner when selected; an escape unless served) or Pick Up (225, a mate lifts it out; out of another faction's cage a crime, locks.md "Cages and beds").
    /// </summary>
    void PrisonerVerbs(List<(string, Action)> verbs, CharacterSnapshot target, PrisonView prison, IReadOnlyList<CharacterId> selection, bool down)
    {
        var id = target.Id;
        if (!down && TalkOffered(id, selection[0])) verbs.Add(("Talk To", () => RequestTalk(selection[0], id)));
        // 26 unless it is the player's and selected: Trade for the player's own, else Loot (a pickpocket while it is awake).
        if (target.CarriedBy.IsNone && !(target.IsPlayer && selection.Contains(id)))
            verbs.Add((target.IsPlayer ? "Trade" : "Loot", () => Say(new Meitou.Simulation.Carrying.OpenLootOrder([], id) { Tick = session.CommandTick })));
        if (prison.Locked)
        {
            if (!down) verbs.Add(("Pick Lock", () => Say(new Meitou.Simulation.Locks.PickLockOrder(selection, id) { Tick = session.CommandTick })));
        }
        else if (selection.Contains(id)) { if (!down) verbs.Add(("Escape", () => Say(new Meitou.Simulation.Locks.GetOutOrder([id]) { Tick = session.CommandTick }))); }
        else if (target.CarriedBy.IsNone)
        {
            // Its cage the player's: Set Free (110) and the pick-up verb; another faction's: the pick-up verb alone (the cage's own menu has Set Free).
            if (prison.CageOwner == session.ActiveFaction) verbs.Add(("Set Free", () => Say(new Meitou.Simulation.Locks.SetFreeOrder([], id) { Tick = session.CommandTick })));
            verbs.Add(PickUpVerb(target));
        }
    }

    /// <summary>
    /// One of the player's own characters: another one than the selection is followed (31) or guarded (45, <see cref="FollowOrder.Guard"/>), its inventory opened beside the selection's
    /// (26, labelled Trade for the player's own), and picked up (225), standing too when it carries no one (<c>FUN_1407a6bc0</c>, <b>Observed</b>). A selected one crawling by choice
    /// gets Stand Up (28, the selection's order).
    /// </summary>
    void OwnCharacterVerbs(List<(string, Action)> verbs, CharacterSnapshot target, IReadOnlyList<CharacterId> selection, bool down)
    {
        var id = target.Id;
        if (selection.Contains(id))
        {
            if (target.StayingLow) verbs.Add(("Stand Up", () => Say(new StandUpCommand(selection) { Tick = session.CommandTick })));
            return;
        }
        // Bodyguard (45): follows and defends the target; with Shift it is the Bodyguard job (PlayerInterface.SquadAi.cs).
        verbs.Add(("Bodyguard", () => Say(new FollowOrder(selection, id) { Guard = true, Tick = session.CommandTick })));
        verbs.Add(("Follow", () => Say(new FollowOrder(selection, id) { Tick = session.CommandTick })));
        if (!target.CarriedBy.IsNone) return;
        if (down || target.Carrying.IsNone) verbs.Add(("Pick Up", () => Say(new Meitou.Simulation.Carrying.PickUpOrder([], id) { Tick = session.CommandTick })));
        verbs.Add(("Trade", () => Say(new Meitou.Simulation.Carrying.OpenLootOrder([], id) { Tick = session.CommandTick })));
    }

    /// <summary>
    /// Throw out of home (223, <c>FUN_140793d80</c>): offered when the character lies in one of the player's buildings (Meitou: <see cref="CharacterSnapshot.InPlayerBuilding"/>); always a job
    /// (the dispatch makes 223 one with or without Shift), carried out by the intruder errand.
    /// </summary>
    void ThrowOutVerb(List<(string, Action)> verbs, CharacterSnapshot target, IReadOnlyList<CharacterId> selection)
    {
        if (!target.InPlayerBuilding) return;
        var id = target.Id;
        verbs.Add(("Throw out of home", () => Say(new Meitou.Simulation.Production.AddOrderJob(selection, Meitou.Data.Gameplay.Ai.TaskType.PICKUP_INTRUDERS, id) { Tick = session.CommandTick })));
    }

    /// <summary>
    /// Another faction's character (docs/game/ui-input.md "Context menu verbs"). Standing: Talk To (12), Stealth KO (228, with its chance) while a selected character sneaks; against a hostile one Attack
    /// All (16) and Attack Target (5), against anyone else Attack Unprovoked (61), Bodyguard (45) and Follow (31); Meitou adds Trade with a trader (its quick order). With the lead on a turret every
    /// attack becomes Shoot At (235). Down or asleep: Loot (26, a pickpocket on a sleeper), the pick-up verb (<see cref="PickUpVerb"/>) and Throw out of home (<see cref="ThrowOutVerb"/>).
    /// Not built: Throw out of town (273).
    /// </summary>
    void OtherCharacterVerbs(List<(string, Action)> verbs, CharacterSnapshot target, IReadOnlyList<CharacterId> selection, bool down)
    {
        var id = target.Id;
        if (down)
        {
            if (!target.CarriedBy.IsNone) return;
            verbs.Add(("Loot", () => Say(new Meitou.Simulation.Carrying.OpenLootOrder([], id) { Tick = session.CommandTick })));
            verbs.Add(PickUpVerb(target));
            ThrowOutVerb(verbs, target, selection);
            return;
        }
        if (target.Asleep)
        {
            // Not standing (the original's state 1): Loot (a pickpocket), the pick-up verb and Stealth KO; not hostile, also Attack Unprovoked, Bodyguard and Talk To.
            if (!target.CarriedBy.IsNone) return;
            verbs.Add(("Loot", () => Say(new Meitou.Simulation.Carrying.OpenLootOrder([], id) { Tick = session.CommandTick })));
            verbs.Add(PickUpVerb(target));
            ThrowOutVerb(verbs, target, selection);
            if (target.StealthKoChance is { } koChance)
                verbs.Add((WithChance("Stealth KO", koChance), () => Say(new Meitou.Simulation.Carrying.StealthKoOrder(selection, id) { Tick = session.CommandTick })));
            if (Hostile(target)) return;
            verbs.Add(("Attack Unprovoked", () => Say(new Meitou.Simulation.Combat.AttackOrder(selection, id) { Tick = session.CommandTick, Picked = true })));
            verbs.Add(("Bodyguard", () => Say(new FollowOrder(selection, id) { Guard = true, Tick = session.CommandTick })));
            if (TalkOffered(id, selection[0])) verbs.Add(("Talk To", () => RequestTalk(selection[0], id)));
            return;
        }
        bool onTurret = Selected().LastOrDefault() is { Turret: not null };
        bool hostile = Hostile(target);
        if (TalkOffered(id, selection[0])) verbs.Add(("Talk To", () => RequestTalk(selection[0], id)));
        if (target.IsTrader && !hostile) verbs.Add(("Trade", () => Say(new Meitou.Simulation.Trading.OpenTrade(selection[0], id) { Tick = session.CommandTick })));
        if (onTurret)
        {
            // The lead mans a turret: the attacks give way to Shoot At (235), which sends every selected gunner's fire at the target.
            var gunners = Selected().Where(c => c.Turret is not null).Select(c => c.Id).ToArray();
            verbs.Add(("Shoot At", () => Say(new Meitou.Simulation.Combat.ShootAtOrder(gunners, id) { Tick = session.CommandTick })));
            return;
        }
        if (target.StealthKoChance is { } chance)
            verbs.Add((WithChance("Stealth KO", chance), () => Say(new Meitou.Simulation.Carrying.StealthKoOrder(selection, id) { Tick = session.CommandTick })));
        if (hostile)
        {
            // Fighting back at someone attacking the squad is defence, not an assault on its faction (as the quick order).
            verbs.Add(("Attack All", () => AttackAll(selection, target)));
            verbs.Add(("Attack Target", () => Say(new Meitou.Simulation.Combat.AttackOrder(selection, id) { Tick = session.CommandTick, Picked = true, Defensive = target.FightsPlayer })));
            return;
        }
        verbs.Add(("Attack Unprovoked", () => Say(new Meitou.Simulation.Combat.AttackOrder(selection, id) { Tick = session.CommandTick, Picked = true })));
        verbs.Add(("Bodyguard", () => Say(new FollowOrder(selection, id) { Guard = true, Tick = session.CommandTick })));
        verbs.Add(("Follow", () => Say(new FollowOrder(selection, id) { Tick = session.CommandTick })));
    }

    /// <summary>How far from the clicked enemy Attack All looks for the others (an engine choice: the original's order 16 is "Attacking enemies", whose reach is <b>Unknown</b>).</summary>
    const float AttackAllRadius = 800;

    /// <summary>
    /// Attack All (16, "Attacking enemies"; <b>engine</b> reading): every standing hostile character within <see cref="AttackAllRadius"/> of the clicked one is a target, and each selected
    /// character attacks the nearest of them to itself.
    /// </summary>
    void AttackAll(IReadOnlyList<CharacterId> selection, CharacterSnapshot clicked)
    {
        var snapshot = session.CurrentSnapshot;
        var foes = snapshot.Characters.Where(c => c.Id == clicked.Id || StandingHostile(c) && Vector3.Distance(c.Position, clicked.Position) <= AttackAllRadius).ToList();
        var groups = new Dictionary<CharacterId, List<CharacterId>>();
        foreach (var id in selection)
        {
            if (snapshot.Characters.FirstOrDefault(c => c.Id == id) is not { } mine) continue;
            var foe = foes.MinBy(f => Vector3.DistanceSquared(f.Position, mine.Position))!;
            if (!groups.TryGetValue(foe.Id, out var list)) groups[foe.Id] = list = [];
            list.Add(id);
        }
        foreach (var (foeId, attackers) in groups)
            Say(new Meitou.Simulation.Combat.AttackOrder(attackers, foeId) { Tick = session.CommandTick, Defensive = foes.First(f => f.Id == foeId).FightsPlayer });
    }

    /// <summary>
    /// The pick-up verb (<c>FUN_140796f80</c>, docs/game/ui-input.md "Context menu verbs"): Pick Up (225) for an ally that is not a trader and for a caged character, else Kidnap (246), which
    /// shows the chance column (<c>Character::getKidnappingChance</c>) and gives the kidnap order (a sleeper too; the roll on arrival, docs/game/carry-loot.md "Kidnapping"). Not read: the
    /// original's tests of the target's owner object, its +0xf0 list and the AI state 8.
    /// </summary>
    (string, Action) PickUpVerb(CharacterSnapshot target)
    {
        var id = target.Id;
        bool friendly = AlliedFaction?.Invoke(target.Faction) == true && !target.IsTrader;
        if (friendly || target.Prison is not null) return ("Pick Up", () => Say(new Meitou.Simulation.Carrying.PickUpOrder([], id) { Tick = session.CommandTick }));
        return (WithChance("Kidnap", target.KidnapChance ?? 100), () => Say(new Meitou.Simulation.Carrying.PickUpOrder([], id) { Tick = session.CommandTick, Kidnap = true }));
    }

    /// <summary>
    /// A container (the original's building branch for a storage with an inventory): unlocked, Inventory (the player's) or Loot (26); another faction's locked one, Pick Lock (76). The
    /// menu's orders skip the quick click's Alt test (<c>FUN_1407fa650</c>), so Loot on another faction's container takes from it at once (a shop container's stock is stolen from);
    /// a locked one also has Use Tools and Use Strength (285, 286). Not built: Auto-Haul (124) on an interior container.
    /// </summary>
    List<(string Caption, Action Give)> ContainerVerbs(Meitou.Simulation.Locks.ContainerSpot container)
    {
        var verbs = new List<(string, Action)>();
        if (SelectedIds().Count == 0) return verbs;
        bool own = container.Owner == session.ActiveFaction;
        if (!container.Locked) verbs.Add((own ? "Inventory" : "Loot", () => OpenContainer(container.Id, true)));
        else
        {
            if (!own && container.Owner >= 0) verbs.Add(("Pick Lock", () => OpenContainer(container.Id, true)));
            verbs.Add(("Use Tools", () => Say(new Meitou.Simulation.Locks.ForceLockOrder([], container.Id, Meitou.Simulation.Locks.LockForce.Tools) { Tick = session.CommandTick })));
            verbs.Add(("Use Strength", () => Say(new Meitou.Simulation.Locks.ForceLockOrder([], container.Id, Meitou.Simulation.Locks.LockForce.Strength) { Tick = session.CommandTick })));
        }
        return verbs;
    }

    /// <summary>The open order for a container (Alt: another faction's locked one is picked, a shop's taken from).</summary>
    void OpenContainer(string id, bool alt)
    {
        closedMachine = null;   // a container whose window was closed opens again when it is asked for (the sim may not have dropped it yet, e.g. while paused)
        Say(new Meitou.Simulation.Items.OpenContainerOrder(SelectedIds(), id, alt) { Tick = session.CommandTick });
    }

    /// <summary>
    /// A door (the original's door branch): unlocked, the player's own gets Lock (77), every one Open (72) or Close (73); locked, the player's Unlock (140), another faction's Pick Lock (76).
    /// Lock and Unlock act on all the building's doors (Meitou's lock order is per building, engine choice). A locked one also has Use Tools, Use Strength and Smash (285, 286, 226: <see cref="Meitou.Simulation.Locks.ForceLockOrder"/>).
    /// </summary>
    List<(string Caption, Action Give)> DoorVerbs((Meitou.Simulation.Locks.DoorPick Door, bool Locked) pick)
    {
        var verbs = new List<(string, Action)>();
        if (SelectedIds().Count == 0) return verbs;
        var door = pick.Door;
        bool own = door.Owner == session.ActiveFaction;
        if (pick.Locked)
        {
            if (own) verbs.Add(("Unlock", () => Say(new Meitou.Simulation.Locks.ToggleDoorLocksOrder(door.Building) { Tick = session.CommandTick })));
            else verbs.Add(("Pick Lock", () => Say(new Meitou.Simulation.Locks.DoorOrder([], door.Id, true) { Tick = session.CommandTick })));
            verbs.Add(("Use Tools", () => Say(new Meitou.Simulation.Locks.ForceLockOrder([], door.Id, Meitou.Simulation.Locks.LockForce.Tools) { Tick = session.CommandTick })));
            verbs.Add(("Use Strength", () => Say(new Meitou.Simulation.Locks.ForceLockOrder([], door.Id, Meitou.Simulation.Locks.LockForce.Strength) { Tick = session.CommandTick })));
            verbs.Add(("Smash", () => Say(new Meitou.Simulation.Locks.ForceLockOrder([], door.Id, Meitou.Simulation.Locks.LockForce.Smash) { Tick = session.CommandTick })));
            return verbs;
        }
        if (own) verbs.Add(("Lock", () => Say(new Meitou.Simulation.Locks.ToggleDoorLocksOrder(door.Building) { Tick = session.CommandTick })));
        bool open = session.CurrentSnapshot.Doors.GetValueOrDefault((door.Building, door.Door)).Motion is Meitou.Simulation.Locks.DoorMotion.Open or Meitou.Simulation.Locks.DoorMotion.Opening;
        verbs.Add((open ? "Close" : "Open", () => Say(new Meitou.Simulation.Locks.DoorOrder([], door.Id) { Tick = session.CommandTick })));
        return verbs;
    }

    /// <summary>A right press on a character with verbs starts the hold timer (true); the quick order, if any, waits for the release.</summary>
    bool ContextPress(Vector2 p, Action? quick)
    {
        if (PickWhere(p, _ => true) is not { } id) return false;
        return ContextPressOn(p, at => session.CurrentSnapshot.Characters.FirstOrDefault(c => c.Id == id) is { } target ? Menu(target.Name, at, Verbs(target)) : null, quick);
    }

    /// <summary>A right press on a target whose <paramref name="menu"/> has verbs (true): the hold timer starts and <paramref name="quick"/> waits for the release.</summary>
    bool ContextPressOn(Vector2 p, Func<Vector2, ContextMenu?> menu, Action? quick)
    {
        if (menu(p) is null) return false;
        contextPending = (menu, p, System.Diagnostics.Stopwatch.GetTimestamp(), quick);
        return true;
    }

    /// <summary>Once a frame: a press held long enough opens the menu.</summary>
    void UpdateContextMenu()
    {
        if (contextPending is not { } pending || System.Diagnostics.Stopwatch.GetElapsedTime(pending.Since).TotalSeconds < ContextHoldSeconds) return;
        contextPending = null;
        contextMenu = pending.Menu(pending.At);
    }

    /// <summary>The right button let go: a press released before the menu opened gives the quick order.</summary>
    void ContextRelease()
    {
        if (contextPending is not { } pending) return;
        contextPending = null;
        pending.Quick?.Invoke();
    }

    /// <summary>
    /// A mouse button let go while the menu is open (<c>FUN_1407a7f10</c>, <c>FUN_1407ac8c0</c>): a verb row under the cursor gives its order, whichever button it is (the row's
    /// release handler tests none), and the menu closes. The menu opens on the held command button and closes when that button is let go, so the right release always ends it;
    /// a left release over a row acts, elsewhere nothing (the menu stays while the command button is held). True when the event was the menu's.
    /// </summary>
    bool ContextMenuRelease(MouseButton button, Vector2 p)
    {
        if (contextMenu is null) return false;
        var row = UiVisible ? MyGuiPresentation.HitTest(ArrangeUi(), p.X, p.Y) : null;
        if (row is { Action: "meitou:context" }) { SoundForAction("context", row.Argument); ContextAction(row.Argument); return true; }
        if (button != MouseButton.Right) return false;   // another button's release still ends what its press began (a drag, a window move)
        CloseContextMenu();
        return true;
    }

    void ContextAction(string argument)
    {
        var menu = contextMenu;
        contextMenu = null;
        if (menu is not null && int.TryParse(argument, NumberStyles.None, CultureInfo.InvariantCulture, out int i) && i < menu.Verbs.Count) menu.Verbs[i].Give();
    }

    /// <summary>Closes the menu (true when one was open).</summary>
    bool CloseContextMenu()
    {
        bool open = contextMenu is not null;
        contextMenu = null;
        contextPending = null;
        return open;
    }

    /// <summary>
    /// The menu as the original lays it out: a floating panel an eighth of the screen wide, the name on top, a button per verb, beside the cursor and kept on screen. When a verb has a
    /// chance, every button keeps the layout's width and the chance shows beside it as "N%", red at 0 to green at 100 (<c>ContextMenuGUI::show</c>); else the buttons fill the row.
    /// </summary>
    void AddContextMenu(List<UiControl> controls)
    {
        if (contextMenu is not { } menu) return;
        // Kenshi_ContextMenu: Root 0.125 x 0.0685 of the screen for the title and one option, the title 47.3 % of that height, an option the rest; in an option the button at
        // x 0.0208 w 0.7917 and the Value box at x 0.8125 w 0.1875, both y 0.0513 h 0.8974.
        float w = MathF.Max(200, Width * 0.125f), full = MathF.Max(64, Height * 0.0685185f);
        float titleH = full * 0.472973f, optionH = full - titleH, h = titleH + optionH * menu.Verbs.Count + 6;
        float x = Math.Clamp(menu.At.X + 12, 0, Math.Max(0, Width - w)), y = Math.Clamp(menu.At.Y, 0, Math.Max(0, Height - h));
        var ink = new Vector4(0.86f, 0.82f, 0.71f, 1);
        bool chances = menu.Verbs.Any(v => SplitChance(v.Caption).Chance is not null);
        float buttonW = chances ? w * 0.791667f : w * (1 - 2 * 0.0208333f);
        controls.Add(new("context-menu", "Widget", new(x, y, w, h), "", "", "Kenshi_FloatingPanelSkin", true, new Dictionary<string, string>(), new(0.1f, 0.09f, 0.08f, 0.94f), ink, Pickable: true));
        controls.Add(new("context-title", "TextBox", new(x + 6, y + 2, w - 12, titleH), menu.Title, "", "", true,
            new Dictionary<string, string> { ["FontName"] = "Kenshi_PaintedTextFont_Medium", ["TextAlign"] = "Center" }, default, ink));
        for (int i = 0; i < menu.Verbs.Count; i++)
        {
            var (caption, chance) = SplitChance(menu.Verbs[i].Caption);
            float rowY = y + titleH + i * optionH + optionH * 0.0512821f, rowH = optionH * 0.897436f;
            controls.Add(new($"context-verb-{i}", "Button", new(x + w * 0.0208333f, rowY, buttonW, rowH), Loc.Tr(caption),
                "meitou:context", "Kenshi_Button1Skin", true, new Dictionary<string, string> { ["FontName"] = "Kenshi_PaintedTextFont_Medium", ["TextAlign"] = "Center" },
                default, ink, Pickable: true, Argument: i.ToString(CultureInfo.InvariantCulture)));
            if (chance is not { } c) continue;
            float p = Math.Clamp(c * 0.01f, 0, 1);
            controls.Add(new($"context-chance-{i}", "TextBox", new(x + w * 0.8125f, rowY, w * 0.1875f, rowH), c.ToString("0", CultureInfo.InvariantCulture) + "%", "", "", true,
                new Dictionary<string, string> { ["FontName"] = "Kenshi_StandardFont_Medium", ["TextAlign"] = "Center" }, default, new Vector4(1 - p, p, 0, 1)));
        }
    }

    /// <summary>
    /// The <c>--scene menu</c> harness (GameHost.Scenes.cs): opens at once, with no hold, the context menu of the <paramref name="kind"/> ("character", "door", "container", "cage")
    /// nearest to <paramref name="near"/> that is on screen; its title and verbs are printed. False when there is none or it has no verbs.
    /// </summary>
    internal bool OpenSceneMenu(string kind, Vector3 near)
    {
        CloseContextMenu();
        var snap = session.CurrentSnapshot;
        (string Title, Vector3 At, List<(string Caption, Action Give)> Verbs)? found = kind switch
        {
            "character" => snap.Characters.Where(c => !c.IsPlayer).OrderBy(c => Vector3.DistanceSquared(c.Position, near)).Select(c => (c.Name, c.Position, Verbs(c))).FirstOrDefault(),
            "door" => snap.DoorPicks.OrderBy(d => Vector3.DistanceSquared(d.Position, near))
                .Select(d => (Loc.Tr("Door"), d.Position, DoorVerbs((d, snap.Doors.GetValueOrDefault((d.Building, d.Door)).Locked)))).FirstOrDefault(),
            "container" => snap.Containers.OrderBy(c => Vector3.DistanceSquared(c.Position, near)).Select(c => (c.Record, c.Position, ContainerVerbs(c))).FirstOrDefault(),
            "cage" => snap.Cages.OrderBy(c => Vector3.DistanceSquared(c.Position, near)).Select(c => (Loc.Tr("Cage"), c.Position, CageVerbs(c))).FirstOrDefault(),
            _ => null,
        };
        if (found is not { } f || f.Verbs is null || Project(f.At) is not { } px) return false;
        contextMenu = Menu(f.Title, px, f.Verbs);
        System.Console.WriteLine($"scene     menu {kind} '{f.Title}' at {f.At.X:0}, {f.At.Z:0}: {string.Join(" | ", f.Verbs.Select(v => v.Caption))}");
        return contextMenu is not null;
    }
}
