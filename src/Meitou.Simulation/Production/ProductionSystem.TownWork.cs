using Meitou.Data;
using Meitou.Data.Fcs;
using Meitou.Data.Gameplay;
using Meitou.Data.Gameplay.Ai;
using Meitou.Data.Gameplay.Construction;
using Meitou.Data.Save;
using Meitou.Simulation.Aggro;
using Meitou.Simulation.Ai;
using Meitou.Simulation.Bodies;
using Meitou.Simulation.Carrying;
using Meitou.Simulation.Combat;
using Meitou.Simulation.Factions;
using Meitou.Simulation.Locks;
using Meitou.Simulation.Perception;
using Meitou.Simulation.Player;
using Meitou.Simulation.Population;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Numerics;
using Meitou.Data.Gameplay.Bodies;
using Meitou.Data.World;
using Meitou.Simulation.Construction;
using Meitou.Simulation.Items;
using Meitou.Simulation.Movement;

namespace Meitou.Simulation.Production;
/// <summary>
/// The machines of NPC towns (docs/game/buildings-production.md "NPC town machines"): a town building an NPC worker (goals 189 / 190, AiSystem Work)
/// stands at has one canonical level-machine site owned by its registered faction, shared with inventories and saving, made when the town registers or on first use.
/// The workers are the AI's: the decision step keeps AiMind.WorkOperate (Task_OperateMachine, row
/// 87) or AiMind.CollectFrom (Task_EmptyMachine, row 92) while they are at the building, and this system applies them.
/// </summary>
public sealed partial class ProductionSystem
{
    /// <summary>
    /// The town catalogue used to resolve a turret on its first player order.
    /// </summary>
    readonly PopulationData? _towns;

    /// <summary>
    /// The towns' buildings by run-time key with the faction owning their town, the objects of their layouts by <see cref="TownLayoutBuilding.Id"/>
    /// (with their building, the faction owning their town and the town's site index), and the revision of the towns' building lists they were
    /// made from (<see cref="TownBuildingIndex"/>).
    /// </summary>
    sealed record TownIndex(int Revision, Dictionary<string, (TownBuilding Building, int Owner, int Site)> Buildings,
        Dictionary<string, (TownBuilding House, TownLayoutBuilding Piece, int Owner, int Site)> Pieces);

    /// <summary>
    /// The current <see cref="TownIndex"/>, replaced whole: the think phase's power checks read it from every worker (<see cref="MachinePowered"/>).
    /// </summary>
    volatile TownIndex? _townIndex;

    /// <summary>
    /// Held while <see cref="_townIndex"/> is made again, so two workers that find it out of date do not both make it.
    /// </summary>
    readonly Lock _townIndexGate = new();

    /// <summary>
    /// The registered towns (site indices) whose buildings have been given their machines (<see cref="TownMachines"/>); cleared when the towns'
    /// lists change.
    /// </summary>
    readonly HashSet<int> _townSitesMade = [];

    /// <summary>
    /// The machine buildings and layout objects (<paramref name="Piece"/> set) of registered towns whose machine is not made yet (in a ruin until it
    /// stands), with the faction owning their town.
    /// </summary>
    readonly List<(TownBuilding Building, TownLayoutBuilding? Piece, int Owner)> _townWaiting = [];

    /// <summary>
    /// The ruins, town layouts and purchased interiors last checked for removed canonical layout machines.
    /// </summary>
    (long Ruins, int Buildings, long Emptied) _piecesChecked = (-1, -1, -1);

    /// <summary>
    /// How many of <see cref="FixtureRegistry.RebuiltHouses"/> had their objects' machines dropped (<see cref="DropRebuilt"/>).
    /// </summary>
    int _rebuiltSeen;

    /// <summary>
    /// The research contributions the town and outpost workers made this tick (<see cref="TownOperate"/>), which the player's operators continue
    /// (<c>FUN_140836b70</c>'s falloff counts every bench worked in the tick; serial).
    /// </summary>
    int _townResearch;

    /// <summary>
    /// The counter of a town farm's random start among its farm draws (above the plantings' and the harvest rolls').
    /// </summary>
    const ulong RandomStart = 1UL << 41;

    /// <summary>
    /// How far a loaded farm's plants grow above its growth at most.
    /// </summary>
    const float PlantSpread = 0.2f;

    /// <summary>
    /// The canonical placement key shared by town work, machine inventories and saves.
    /// </summary>
    public static string TownWorkId(TownBuilding building) => building.Key;

    /// <summary>
    /// The site of the town building a player's order names by its <see cref="TownWorkId"/> (a turret's 146 USE_TURRET, a machine's 87 or its
    /// loot, docs/game/ui-input.md "Context menu verbs": whoever owns it), made with its machine on the first order (serial: the commands' phase);
    /// null for an id naming no standing town building that is a machine.
    /// </summary>
    BuildSite? TownMachineSite(World world, string id)
    {
        if (world.Buildings.Find(id) is { } known) return known;
        if (TownBuildingIndex(world)?.Buildings is not { } buildings) return null;
        if (buildings.TryGetValue(id, out var found))
            return world.Fixtures.IsDestroyed(found.Building) ? null : world.Fixtures.GetTownMachineSite(world, found.Building);

        foreach (var (building, _, _) in buildings.Values)
        {
            if (!id.StartsWith(building.Key + "/", StringComparison.Ordinal) || world.Fixtures.IsDestroyed(building)) continue;
            if (building.LayoutBuildings.FirstOrDefault(p => p.Id == id) is { } piece)
                return world.Fixtures.GetTownPieceMachineSite(world, building, piece);
        }
        return null;
    }

    /// <summary>
    /// Removes purchased-house layout machines with their furniture. Shared grid contents are handled once by the fixture removal.
    /// </summary>
    internal void RemoveTownInterior(World world, string parent, bool spill)
    {
        foreach (var site in world.Buildings.All.Where(s => s.LevelMachine && FixtureRegistry.TownMachineParent(s) == parent).ToArray())
        {
            if (world.Machines.Find(site.Id) is { } machine)
            {
                bool shared = machine.Storage is not null && ReferenceEquals(machine.Storage, world.Fixtures.Find(site.Id)?.Contents);
                if (spill) DropAll(world, machine, includeStorage: !shared);
                world.Machines.Remove(site.Id);
            }
            world.SavedMachines?.InvalidatePiece(site);
            world.Fixtures.ForgetTownWorkTarget(world, site.Id);
            world.Buildings.Remove(site);
        }
    }

    /// <summary>
    /// The selected canonical town machine owned by the local player's faction, or null when none is available.
    /// </summary>
    public string? OwnTownMachineOf(World world, string? key) => OwnTownMachineOf(world, key, world.Player.Faction);

    /// <summary>
    /// The selected canonical town machine owned by the recipient's faction. Read only; the network applies its inventory access grants separately.
    /// </summary>
    public string? OwnTownMachineOf(World world, string? key, int faction)
    {
        if (key is null || faction < 0 || world.Buildings.Find(key) is not { LevelMachine: true, Destroyed: false } site) return null;
        if (site.Owner != faction || !world.Fixtures.CurrentTownMachine(site)) return null;

        return world.Machines.Find(site.Id) is null ? null : site.Id;
    }

    /// <summary>
    /// The town building of a run-time key and the faction owning its town; null for none.
    /// </summary>
    (TownBuilding Building, int Owner, int Site)? TownBuildingOf(World world, string key) =>
        TownBuildingIndex(world) is { } index && index.Buildings.TryGetValue(key, out var found) ? found : null;

    /// <summary>
    /// The layout object of a town building of <see cref="TownLayoutBuilding.Id"/> <paramref name="key"/>, with its building, the faction owning its
    /// town and the town's site index; null for none.
    /// </summary>
    (TownBuilding House, TownLayoutBuilding Piece, int Owner, int Site)? TownPieceOf(World world, string key) =>
        TownBuildingIndex(world) is { } index && index.Pieces.TryGetValue(key, out var found) ? found : null;

    /// <summary>
    /// The level-town index of a canonical building or layout machine, null for a player-built or natural site.
    /// </summary>
    int? TownSiteOf(World world, string siteId) => TownBuildingOf(world, siteId)?.Site ?? TownPieceOf(world, siteId)?.Site;

    /// <summary>
    /// The towns' buildings by run-time key with the faction owning their town, and their layout objects (<see cref="TownIndex"/>), made again
    /// when the lists change (<see cref="PopulationData.BuildingsRevision"/>, which also has <see cref="TownMachines"/> look at every registered
    /// town again); null without towns.
    /// </summary>
    TownIndex? TownBuildingIndex(World world)
    {
        if ((_towns ?? world.SystemOf<PopulationSystem>()?.Data) is not { } data) return null;
        if (_townIndex is { } current && current.Revision == data.BuildingsRevision) return current;

        lock (_townIndexGate)
        {
            if (_townIndex is { } made && made.Revision == data.BuildingsRevision) return made;

            var buildings = new Dictionary<string, (TownBuilding Building, int Owner, int Site)>(StringComparer.Ordinal);
            var pieces = new Dictionary<string, (TownBuilding House, TownLayoutBuilding Piece, int Owner, int Site)>(StringComparer.Ordinal);
            foreach (var site in data.Sites)
            {
                int owner = site.Index < data.SiteFactions.Count ? data.SiteFactions[site.Index] : -1;
                foreach (var b in site.Buildings)
                {
                    buildings[b.Key] = (b, owner, site.Index);
                    foreach (var piece in b.LayoutBuildings) pieces[piece.Id] = (b, piece, owner, site.Index);
                }
            }

            _townSitesMade.Clear();
            _townWaiting.Clear();
            return _townIndex = new TownIndex(data.BuildingsRevision, buildings, pieces);
        }
    }

    /// <summary>
    /// The machine of a town building or of a layout object of one by its key (<see cref="TownLayoutBuilding.Id"/>), made on the first call
    /// (<see cref="TownWork"/>, <see cref="PieceWork"/>); null for a key naming neither or one that is no machine. Serial phases only.
    /// </summary>
    public Machine? TownWork(World world, string key)
    {
        if (TownBuildingOf(world, key) is { } found) return TownWork(world, found.Building, found.Owner);

        return TownPieceOf(world, key) is { } piece ? PieceWork(world, piece.House, piece.Piece, piece.Owner) : null;
    }

    /// <summary>
    /// The machine of a town building (made on the first call: empty, owned by the town's faction <paramref name="owner"/>, or by the player's when
    /// the level data gives the building to it, <see cref="PopulationData.LevelOwnerOf"/>: a take out of that one is no theft), null for a building
    /// that neither works nor stores. Serial phases only.
    /// </summary>
    public Machine? TownWork(World world, TownBuilding building, int owner) => TownWork(world, building);

    /// <summary>
    /// A layout producer, farm or battery uses its fixture-registered canonical site, current owner and shared native persistence.
    /// </summary>
    public Machine? PieceWork(World world, TownBuilding house, TownLayoutBuilding piece, int owner)
    {
        var site = world.Buildings.Find(piece.Id) ?? world.Fixtures.GetTownPieceMachineSite(world, house, piece);
        return site is { Complete: true, Destroyed: false } && world.Fixtures.CurrentTownMachine(site) ? Ensure(world, site) : null;
    }

    /// <summary>
    /// A town farm's plants after its load (<b>Verified</b>, decompile: the building factory <c>FUN_140581770</c> runs the farm's load before its
    /// setup, which gives each plant the farm's growth + 0.2 x U[0, 1), the constant at <c>14168a4a8</c>, then marks the first harvested ones -10).
    /// </summary>
    static void SpreadPlants(World world, string id, FarmState farm)
    {
        ulong key = Rng.StableHash(id);
        for (int i = 0; i < farm.Plants.Length; i++)
        {
            float spread = PlantSpread * Rng.Float(Rng.Hash(world.Seed, key, RngPurpose.Farm, RandomStart + 1 + (ulong)i));
            farm.Plants[i] = i < farm.Harvested ? -10 : farm.Growth + spread;
        }
    }

    /// <summary>
    /// Every registered town's buildings that are machines get them (serial): the original makes every building of a loaded zone as its class,
    /// ProductionBuilding and FarmBuilding among them, with its production state from the saved GAMESTATE_BUILDING, so a mine's output and a farm's
    /// crop are there from the start for its workers, the player's loot and the AI's input finder; a farm grows and an unattended producer (an
    /// automatic well, max operators 0) works whether anyone is there (ProductionBuilding::vfunc_28 runs it each frame, #763). A ruin waits until it
    /// stands. The objects of its buildings' layouts are buildings of their own and get theirs alike (<see cref="IsPieceMachine"/>).
    /// </summary>
    void TownMachines(World world)
    {
        if ((_towns ?? world.SystemOf<PopulationSystem>()?.Data) is not { } data || TownBuildingIndex(world) is null) return;

        DropGonePieces(world, data);
        DropRebuilt(world);
        foreach (var site in data.Sites)
        {
            if (!world.Fixtures.IsRegistered(site.Index) || !_townSitesMade.Add(site.Index)) continue;

            int owner = site.Index < data.SiteFactions.Count ? data.SiteFactions[site.Index] : -1;
            foreach (var b in site.Buildings) WaitForMachines(b, owner);
        }

        if (_townWaiting.Count > 0) _townWaiting.RemoveAll(w => MadeTownMachine(world, w.Building, w.Piece, w.Owner));
    }

    /// <summary>
    /// The machines of layout objects that are gone, checked when the ruins, the towns' lists or the bought houses change: setting a house
    /// destroyed destroys its interior's objects (<c>Building::setDestroyed</c> <c>FUN_140557280</c> -> <c>FUN_14054f4d0</c>,
    /// docs/game/buildings-production.md "A ruin holds no furniture"), buying it does too (<see cref="Locks.FixtureRegistry.IsEmptied"/>,
    /// docs/simulation.md "Town house purchases"), and a house laid out again has other objects. Each such machine goes with its items; an object of
    /// a house that is a ruin waits for it to stand again (<see cref="MadeTownMachine"/>) and is made new then; a bought house's never comes back.
    /// </summary>
    void DropGonePieces(World world, PopulationData data)
    {
        var seen = (world.Fixtures.RuinsRevision, data.BuildingsRevision, world.Fixtures.EmptiedRevision);
        if (seen == _piecesChecked) return;
        _piecesChecked = seen;

        foreach (var town in data.Sites)
            foreach (var house in town.Buildings)
                if (world.Fixtures.IsDestroyed(house) || world.Fixtures.IsEmptied(house.Key))
                {
                    world.SavedMachines?.InvalidateLayout(house);
                    world.Fixtures.SavedPieces?.InvalidateHouse(house.Key);
                }

        foreach (var site in world.Buildings.All.Where(s => s.LevelMachine && FixtureRegistry.TownMachineParent(s) is not null).ToArray())
            if (IsPieceGone(world, site)) DropPiece(world, site);
    }

    /// <summary>
    /// The machines of the objects of each house a town override furnished anew since the last look (<see cref="FixtureRegistry.RebuiltHouses"/>)
    /// go with their items, its layout's names the same or not: the override's housing clears the interior and lays it out again
    /// (<c>FUN_14092e4b0</c>), so each object made again is new.
    /// </summary>
    void DropRebuilt(World world)
    {
        var rebuilt = world.Fixtures.RebuiltHouses;
        if (_rebuiltSeen == rebuilt.Count) return;

        for (; _rebuiltSeen < rebuilt.Count; _rebuiltSeen++) DropHouse(world, rebuilt[_rebuiltSeen]);
        world.Machines.Touch();
    }

    /// <summary>
    /// Drops the machines of a house's layout objects and marks its objects destroyed, so none made again takes the save's entry.
    /// </summary>
    void DropHouse(World world, string house)
    {
        foreach (var site in world.Buildings.All
            .Where(s => s.LevelMachine && FixtureRegistry.TownMachineParent(s) == house).ToArray()) DropPiece(world, site);
        if (TownBuildingOf(world, house) is { } found) world.SavedMachines?.InvalidateLayout(found.Building);
        world.Fixtures.SavedPieces?.InvalidateHouse(house);
    }

    /// <summary>
    /// Whether a town machine site is a layout object's that is gone: no longer in its house's layouts (another record or place under its id) or
    /// its house is a ruin.
    /// </summary>
    bool IsPieceGone(World world, BuildSite site)
    {
        if (TownPieceOf(world, site.Id) is not { } found) return true;
        return found.Piece.RecordId != site.Record || found.Piece.Position != site.Position || found.Piece.Rotation != site.Rotation
            || world.Fixtures.IsDestroyed(found.House) || world.Fixtures.IsEmptied(found.House.Key);
    }

    /// <summary>
    /// Removes the disappeared machine without spilling its destroyed contents; a surviving layout waits for the house to stand.
    /// </summary>
    void DropPiece(World world, BuildSite site)
    {
        world.SavedMachines?.InvalidatePiece(site);
        world.Machines.Remove(site.Id);
        world.Buildings.Remove(site);
        world.Fixtures.ForgetTownWorkTarget(world, site.Id);
        if (TownPieceOf(world, site.Id) is { } found && !world.Fixtures.IsEmptied(found.House.Key)
            && IsPieceMachine(Data(found.Piece.RecordId))) _townWaiting.Add((found.House, found.Piece, found.Owner));
    }

    /// <summary>
    /// A registered town's building waits for its machine when it is one, and so does each machine object of its layouts.
    /// </summary>
    void WaitForMachines(TownBuilding b, int owner)
    {
        if (IsTownMachine(Data(b.BuildingId))) _townWaiting.Add((b, null, owner));

        foreach (var piece in b.LayoutBuildings)
            if (IsPieceMachine(Data(piece.RecordId))) _townWaiting.Add((b, piece, owner));
    }

    /// <summary>
    /// Whether a waiting town building or layout object has its machine now: it had one, or its building stands and it gets it (a ruin keeps
    /// waiting); an object of a bought house gets none and waits no more (the purchase destroyed it).
    /// </summary>
    bool MadeTownMachine(World world, TownBuilding building, TownLayoutBuilding? piece, int owner)
    {
        string id = piece is null ? TownWorkId(building) : piece.Id;
        if (world.Machines.Find(id) is not null) return true;
        if (piece is not null && world.Fixtures.IsEmptied(building.Key)) return true;
        if (world.Fixtures.IsDestroyed(building)) return false;

        return (piece is null ? TownWork(world, building, owner) : PieceWork(world, building, piece, owner)) is not null;
    }

    /// <summary>
    /// Whether a layout object of <paramref name="data"/> gets its machine when its town registers: what a town building gets one for
    /// (<see cref="IsTownMachine"/>: an interior hydroponic farm, a refinery), and a battery bank, whose charge the town's frame moves
    /// (docs/game/buildings-production.md "Power"). A layout's generators are the frame's as a town's are (<see cref="NpcOutput"/>).
    /// </summary>
    static bool IsPieceMachine(MachineData? data) => IsTownMachine(data) || data is { IsBattery: true };

    /// <summary>
    /// Whether a town building of <paramref name="data"/> gets its machine when its town registers: a farm or a producer (a mine, well, refinery
    /// or rain collector), whose state its workers and the hours change. The other functionalities hold nothing an NPC changes: a research bench
    /// counts for the player's research alone, a crafting bench has no queue, a town's generators are the power frame's
    /// (<see cref="NpcGeneration"/>), and its storages are container fixtures; those get a machine only when a worker or the player's order
    /// comes (<see cref="TownWork"/>).
    /// </summary>
    static bool IsTownMachine(MachineData? data) => data is { Farm: not null } or { IsProducer: true };

    /// <summary>
    /// A town building's machine as the holder of its faction's goods (<see cref="Crime.StealSource"/>: the building is its own container, and it
    /// stands in the building its <c>isInsideBuilding</c> handle names, a layout object in its house, whose residents watch and own it); null for
    /// any other site.
    /// </summary>
    public Crime.StealSource? TownGoods(World world, string site) =>
        Site(world, site) is { LevelMachine: true } s
            ? new(site, s.Owner, site, s.Position, s.Record, TownBuildingOf(world, site)?.Building.InsideOf ?? s.Parent)
            : null;

    /// <summary>
    /// A take out of another faction's town machine (<see cref="Take"/>): the stolen mark the taken items get
    /// (<see cref="Crime.CrimeSystem.OwnerMark"/>; none for any other take), then, when the taker has <paramref name="room"/> for them with it, the
    /// Theft and the steal roll (<see cref="Crime.CrimeSystem.Steal"/>), else the Theft alone (no roll, no thievery XP, #796). True: caught or no
    /// room, nothing moves.
    /// </summary>
    static bool CaughtTaking(World world, Crime.StealSource? goods, CharacterId taker, CharacterCold cold, Func<StolenMark, bool> room,
        out StolenMark mark, PlayerId? actor = null)
    {
        mark = StolenMark.None;
        if (goods is { } owned && actor is { } player
            && world.Players.Find(player)?.HasPermission(owned.Owner, FactionPermission.ManageAssets) == true) return false;
        if (goods is not { Owner: >= 0 } g || g.Owner == cold.Faction || !Crime.CrimeSystem.IsStealing(world, g)) return false;

        mark = Crime.CrimeSystem.OwnerMark(world, g.Building, g.Owner);
        bool fits = room(mark);
        if (world.SystemOf<Crime.CrimeSystem>() is not { } crimes || !world.Characters.TryResolveNext(taker, out int thief)) return !fits;

        if (!fits) crimes.PendSteal(world, thief, g, Crime.CrimeSystem.TakeKind.RightClick);
        return !fits || crimes.Steal(world, thief, g, Crime.CrimeSystem.TakeKind.RightClick);
    }

    /// <summary>
    /// State 66 <c>haveSomeResourcesFrom</c> (<c>FUN_140597530</c>, <b>Observed</b>): the character carries the machine's output item.
    /// </summary>
    public static bool CarriesOutputOf(CharacterCold cold, Machine m) => m.Output is { } o && Carries(cold, o.Recipe.Item);

    /// <summary>
    /// State 67 <c>haveSomeResourcesFor</c> (<c>FUN_1405a45f0</c>, <b>Observed</b>): the character carries an item one of the machine's inputs takes.
    /// </summary>
    public static bool CarriesInputOf(CharacterCold cold, Machine m) => m.Inputs.Any(input => input.Rate > 0 && Carries(cold, input.Item));

    /// <summary>
    /// Whether the character carries <paramref name="item"/> (in its inventory or its backpack's).
    /// </summary>
    internal static bool Carries(CharacterCold cold, string item) =>
        cold.Inventory is { } inv && (inv.All().Any(i => i.Record == item) || inv.Backpack?.Contents?.All().Any(i => i.Record == item) == true);

    /// <summary>
    /// <c>Task_FillMachine</c> (row 93, <c>FUN_140344510</c>, <b>Verified</b>) at a machine with a production component: the carried items (inventory
    /// and worn backpack) its inputs accept go into them up to each input's capacity, a unit at a time.
    /// </summary>
    void FillInputs(World world, CharacterCold cold, Machine m)
    {
        if (cold.Inventory is not { } inv) return;
        bool moved = false;
        foreach (var input in m.Inputs)
        {
            if (input.Rate <= 0) continue;
            foreach (var item in inv.All().Concat(inv.Backpack?.Contents?.All() ?? []).Where(i => i.Record == input.Item).ToList())
            {
                int n = Math.Min(input.Recipe.Capacity - input.Count, item.Quantity);
                if (n <= 0) break;
                input.Count += n;
                item.Quantity -= n;
                if (item.Quantity == 0) (inv.Items.Contains(item) ? inv : inv.Backpack!.Contents!).Remove(item);
                moved = true;
            }
        }

        if (moved) world.Machines.Touch();
    }

    /// <summary>
    /// The machine a town building has made, null before its town registered or a worker came; for an outpost's workplace (a stand-in known by its
    /// site id, <see cref="TownBuilding.RuntimeKey"/>) the player's own machine.
    /// </summary>
    public static Machine? TownWorkOf(World world, TownBuilding building) => world.Machines.Find(building.RuntimeKey ?? TownWorkId(building));

    /// <summary>
    /// The machine a worker works at a town building: <see cref="TownWorkOf"/>, else a town building's machine made now (<see cref="TownWork"/>);
    /// an outpost's workplace has no machine made for it.
    /// </summary>
    Machine? MachineFor(World world, TownBuilding building, int owner) =>
        TownWorkOf(world, building) ?? (building.RuntimeKey is null ? TownWork(world, building, owner) : null);

    /// <summary>
    /// The workers of town machines at their building this tick (serial, before the players' operators): the AI's workers that stand at their
    /// building doing row 87 or 221 operate it (<see cref="TownOperate"/>), those doing row 92 empty its output into their inventory and those doing
    /// row 93 fill its inputs.
    /// </summary>
    void TownWorkers(World world, float dt)
    {
        var table = world.Characters;
        _townResearch = 0;
        for (int i = 0; i < table.HighWater; i++)
        {
            if (!table.Next[i].Alive || table.Cold(i)is not { Ai: { WorkOperate: not null } or { CollectFrom: not null } or { FillTo: not null } } cold)
                continue;
            ref var n = ref table.Next[i];
            var mind = cold.Ai!;
            var building = mind.WorkOperate ?? mind.CollectFrom ?? mind.FillTo!;
            if (!Able(cold) || mind.Behaviour != Ai.AiBehaviour.Work || !AtTownWork(world, i, building, mind.WorkRange)) continue;
            if (MachineFor(world, building, mind.WorkOwner) is not { } m || !Live(world, m)) continue;
            if (Site(world, m.Site) is not { Complete: true, Destroyed: false } site || site.Record != building.BuildingId
                || site.Owner != cold.Faction && !(cold.SlaveState == 1 && cold.SlaveOwner == site.Owner)) continue;

            if (mind.FillTo is not null) FillInputs(world, cold, m);
            else if (mind.CollectFrom is not null) TakeOutput(world, cold, n.Position, m);
            else if (mind.WorkOperate is not null && MachinePowered(world, m)) TownOperate(world, i, cold, m, dt);
        }
    }

    /// <summary>
    /// State 82 <c>gotInventoryRoomForResources</c> of a machine (<c>FUN_14059bd80</c>, <b>Verified</b>): the inventory or the worn backpack has room
    /// for a unit of its output item.
    /// </summary>
    internal bool RoomForOutput(CharacterCold cold, Machine m) =>
        m.Output is { } o && cold.Inventory is { } inv && _items.Create(o.Recipe.Item, 1) is { } unit && inv.HasRoomFor(unit);

    /// <summary>
    /// Whether a worker stands at the town building (<see cref="Ai.AiSystem.AtWorkplace"/>), idle or resting; in a world without the AI system
    /// (nothing reads the building's use nodes there), within <paramref name="reach"/> of the building's position.
    /// </summary>
    static bool AtTownWork(World world, int slot, TownBuilding building, float reach)
    {
        ref readonly var n = ref world.Characters.Next[slot];
        if ((CharacterTask)n.Task is not (CharacterTask.Idle or CharacterTask.Rest)) return false;
        if (world.SystemOf<Ai.AiSystem>() is { } ai) return ai.AtWorkplace(world, in n, building);

        return Ai.AiSystem.AtWork(in n, building.Position, reach);
    }

    /// <summary>
    /// One worker's frame at a town machine, rows 87 and 221 alike (<c>Task_OperateMachine</c>'s update reads no row, <c>FUN_14035bc90</c>,
    /// <b>Verified</b>): the building's work step at the worker's skill (<see cref="WorkStep"/>, as the player's operator) and the operating XP. A
    /// research bench's work counts only for a player-owned one (<c>FUN_14029b8d0</c>), so only at an outpost, where it adds to the tick's
    /// contributions (<see cref="_townResearch"/>); a crafting bench's only for a queued craft (<c>FUN_1402b5a40</c>). A crafting bench does not
    /// refuse its operator: its <c>dontNeedWorkRightNow</c> (slot <c>0x4e8</c>) is a constant 0 (<c>FUN_1402ab3c0</c>). At a training dummy
    /// (function 7) the operating XP is skipped and the work step is the dummy's training (<see cref="TrainingXp"/>).
    /// </summary>
    void TownOperate(World world, int slot, CharacterCold cold, Machine m, float dt)
    {
        if (!(m.Data.IsProducer || m.Farm is not null || m.Data.IsResearch || m.Data.IsCrafting || m.Data.IsTraining)
            || m.Data.IsAutomatic || m.Operators >= m.Data.MaxOperators || m.Broken
            || Site(world, m.Site) is not { Complete: true, Destroyed: false } site || !world.Fixtures.CurrentTownMachine(site)
            || site.Owner != cold.Faction && !(cold.SlaveState == 1 && cold.SlaveOwner == site.Owner)) return;

        // A player research bench refuses operation without current research; NPC benches still train their operator.
        if (m.Data.IsResearch && world.IsPlayerFaction(site.Owner) && ResearchFor(world, m).Queue.Count == 0) return;
        if (m.Data.IsTraining)
        {
            m.Operators++;
            cold.Operating = true;
            TrainingXp(cold, m.Data, dt);
            return;
        }

        m.Operators++;
        cold.Operating = WorkStep(world, slot, cold, m, dt, ref _townResearch);
        OperatingXp(world, cold, m.Data.Stat, dt);
    }

    /// <summary>
    /// The residents pretend-operating a workstation or training dummy of their home (row 221, <see cref="Ai.AiMind.PretendAt"/>, set once the seat
    /// act has them there): at a dummy its training; at a workstation <see cref="TownOperate"/> on the piece's own machine (<see cref="PieceWork"/>),
    /// as <c>Task_OperateMachine</c>'s update does for any machine (<c>FUN_14035bc90</c>: the operating XP, then the building's work step, slot
    /// <c>0x4b0</c>), so a refinery or hydroponic farm at home fills its output; a research or crafting piece's step makes nothing for them (the
    /// player's faction only, a queued craft only). A piece with no machine data gives the operating XP alone.
    /// </summary>
    void HomePretenders(World world, float dt)
    {
        var table = world.Characters;
        for (int i = 0; i < table.HighWater; i++)
        {
            if (!table.Next[i].Alive || table.Cold(i) is not { Ai: { PretendAt: { Record: { } record } spot } mind } cold) continue;

            bool there = mind.Behaviour == Ai.AiBehaviour.Sit && (CharacterTask)table.Next[i].Task == CharacterTask.Idle;
            if (!Able(cold) || !there || Data(record) is not { } data) continue;

            if (data.IsTraining) TrainingXp(cold, data, dt);
            else if (spot.Piece is { } piece && TownWork(world, piece) is { } m) PretendOperate(world, i, cold, m, dt);
            else OperatingXp(world, cold, data.Stat, dt);
        }
    }

    /// <summary>
    /// One pretending resident's frame at its piece's machine: <see cref="TownOperate"/> while the piece has power (state 93, a start condition of
    /// 221 as of 87, which <see cref="TownWorkers"/> holds the AI's workers to alike).
    /// </summary>
    void PretendOperate(World world, int slot, CharacterCold cold, Machine m, float dt)
    {
        if (Live(world, m) && MachinePowered(world, m)) TownOperate(world, slot, cold, m, dt);
    }

    /// <summary>
    /// State 93 BUILDING_HAS_POWER of a house's layout object (<c>FUN_140596ab0</c> -> <c>FUN_140299370</c>): its machine's power, true for an
    /// object that is no machine (a building class below 2) or draws nothing. Makes the machine on the first ask (<see cref="TownWork(World,
    /// string)"/>); serial phases only.
    /// </summary>
    public bool PiecePowered(World world, string piece) => TownWork(world, piece) is not { } m || MachinePowered(world, m);

    /// <summary>
    /// <c>Task_EmptyMachine</c> with flag 0 (row 92, <c>FUN_14033cd10</c>, <b>Verified</b>) at a machine with an output, the character standing at
    /// <paramref name="at"/>: the output's item goes into its inventory (the worn backpack when the main grid is full) a unit at a time while there
    /// is room, up to what the town wants of it (<see cref="FillDemand.Count"/>, the task's slot 5); a producer whose status was 1 (output full) then
    /// goes back to 2. The task then ends, failed when nothing moved (<see cref="Ai.AiSystem.CollectDone"/>).
    /// </summary>
    void TakeOutput(World world, CharacterCold cold, Vector3 at, Machine m)
    {
        if (cold.Inventory is not { } inv) return;

        int moved = 0;
        if (m.Output is { Count: > 0 } output && _items.Create(output.Recipe.Item, 1) is { } one)
        {
            int wanted = FillDemand.Count(world, _towns, cold, at, one, m.Site, m.Data.Id);
            while (moved < wanted && output.Count > 0 && _items.Create(output.Recipe.Item, 1) is { } unit && inv.HasRoomFor(unit) && inv.Add(unit))
            {
                output.Count--;
                moved++;
            }
        }

        Ai.AiSystem.CollectDone(cold, moved > 0);
        if (moved == 0) return;

        if (m.Status == 1) m.Status = 2;
        world.Machines.Touch();
    }
}
