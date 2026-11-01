using System.Numerics;
using Meitou.Data;
using Meitou.Data.Gameplay;
using Meitou.Data.Gameplay.Bodies;
using Meitou.Data.Gameplay.Construction;
using Meitou.Data.World;
using Meitou.Simulation.Bodies;
using Meitou.Simulation.Items;

namespace Meitou.Simulation.Construction;

/// <summary>The numbers of <see cref="ConstructionSystem"/>; the engine choices where the original is Unknown say so (docs/simulation.md "Buildings").</summary>
public sealed class ConstructionSettings
{
    /// <summary>The <i>Building speed</i> option (0.5 to 2): multiplies build work. Its default is Unknown; 1 is neutral.</summary>
    public float BuildingSpeed { get; init; } = 1;
    /// <summary>The footprint radius of a BUILDING record (the host knows it from the mesh); null: 0. A dismantled building's refund falls within this far (X/Z, at least 20) of its position.</summary>
    public Func<string, float>? Footprint { get; init; }
    /// <summary>The X/Z gap from a point to a site's footprint hull, 0 inside (the host knows the hull); null, or a null answer: the gap to its position (<see cref="SiteReach"/>).</summary>
    public Func<BuildSite, Vector2, float?>? EdgeGap { get; init; }
    public IConstructionPlacement? Placement { get; init; }
}

/// <summary>
/// Player construction (docs/simulation.md "Buildings"; the original's rules in docs/game/buildings-production.md "Construction" and docs/game/ai-tasks.md "Build tasks").
/// <list type="bullet">
/// <item><b>Sites</b> (<see cref="World.Buildings"/>): <see cref="PlaceBuildings"/> creates one per plan that passes the data half of the placement rules (a BUILDING, unlocked by research, not
/// too close to another faction's town); the geometric half is the host's. <see cref="CancelSite"/> removes a site with nothing delivered, <see cref="PauseSite"/> sets its <c>pause</c> flag.</item>
/// <item><b>Builders</b>: <see cref="BuildOrder"/> (orders 2 and 95) and <see cref="DismantleOrder"/> (96) set <see cref="CharacterTask.Build"/> / <see cref="CharacterTask.Dismantle"/> and
/// <see cref="CharacterCold.BuildSite"/> (an unfinished site, or a damaged complete building to repair: <see cref="BuildSite.NeedsWork"/>); <c>Schedule</c> walks the character to the site (the
/// first-aid pattern), <c>SlowWorld</c> works it, all serial. At the site the builder pushes build work
/// each tick until the site stalls (<c>sum(min(delivered, required)) &lt;= progress</c>); then, if it carries a missing material, it fills the 5 s delivery bar
/// (<see cref="CharacterTask.DeliverMaterials"/>), hands over one unit per missing type and goes on building; empty-handed it fetches a missing material (<see cref="CharacterTask.FetchMaterials"/>,
/// task 247: <see cref="Source"/>) and comes back, or stops when there is none.</item>
/// <item><b>Work</b> per tick (<b>Verified</b>): <c>lerp(0.3, 2, engineering/100) x dt x Building speed x CONSTANTS build speed x 0.75^k</c>, k the builders that have worked the site this tick
/// (capped at 15; the reset is <b>Unknown</b>, per tick is the engine reading), progress += <c>build speed mult / (1200/11)</c> x that. Dismantling: <c>dt x lerp(0.05, 4, engineering/100) x 4</c> x rate
/// off the progress; at 0 the delivered materials (60 to 100 %, at least 1 each) fall on the ground, one item per unit.</item>
/// <item><b>Engineer job</b> (125, run by the production system's job lists): <see cref="EngineerSite"/> picks the best-scoring unpaused site (<c>max(2 (1 - d^2/9000^2), 0.0001) / (n + 1)</c>,
/// n builders on it), skipping a stalled site the character carries nothing for and no material source can feed; <see cref="StartBuilding"/> sends the character there.</item>
/// </list>
/// Placed after the carry system and before combat. Everything here is serial and reads only the world's state and the command order.
/// </summary>
public sealed class ConstructionSystem : ITickSystem
{
    /// <summary>The delivery bar fills at this much per second (<b>Verified</b>, <c>Task_AddMaterialsToBuilding</c>: 5 s).</summary>
    public const float BarRate = 0.2f;
    /// <summary>The 0.75 falloff counter is capped at this (<b>Verified</b>); above <see cref="CounterRestart"/> it restarts at 0.</summary>
    public const int CounterCap = 15, CounterRestart = 100;
    /// <summary>A fetch takes at most this many units from a building (<b>Verified</b>, <c>Task_EmptyMachine_BuildMats</c> slot 5 <c>FUN_1403411c0</c>).</summary>
    public const int PickupUnits = 2;
    /// <summary>Loose materials are looked for within this far of the builder (<b>Verified</b>, <c>FUN_14059d000</c>).</summary>
    public const float GroundRadius = 1200;
    /// <summary>
    /// Buildings holding materials count within this squared distance of the builder (about 6517; <b>Verified</b>, <c>FUN_14059d000</c>: the AI's default range mode 2, docs/game/buildings-production.md
    /// "Material delivery").
    /// </summary>
    public const float SourceRangeSquared = 4.246733e7f;
    public const string FurnitureFirst = "I have to dismantle the furniture first.";
    public const string UnmountFirst = "I have to unmount the buildings first.";

    // 0.75^k by repeated multiplication, so the factor never depends on a library's pow.
    static readonly float[] Falloff = BuildFalloff();

    static float[] BuildFalloff()
    {
        var table = new float[CounterCap + 1];
        float f = 1;
        for (int k = 0; k <= CounterCap; k++, f *= 0.75f) table[k] = f;
        return table;
    }

    readonly GameDatabase db;
    readonly GameConstants constants;
    readonly ItemFactory items;
    readonly XpService xp;
    readonly IReadOnlyList<PlacementTown> towns;
    readonly string playerFactionId;
    readonly PopulationData? population;
    readonly Dictionary<string, BuildingBuildData?> buildData = new(StringComparer.Ordinal);
    // A wall subsection (short piece, connector, wedge) to the BUILDINGs that list it in `wall subsections`.
    Dictionary<string, List<string>>? sectionOf;

    /// <summary>
    /// Research allows a plan: its record is unlocked, or it is a wall subsection of an unlocked BUILDING (a wall line lays those pieces; no research enables them, docs/game/buildings-production.md "Walls").
    /// </summary>
    bool Unlocked(World world, string record, int faction)
    {
        if (world.Factions.Find(faction)?.Research is not { } research) return false;
        if (research.IsUnlocked(record)) return true;
        if (sectionOf is null)
        {
            sectionOf = new(StringComparer.Ordinal);
            foreach (var r in db.OfType(Meitou.Data.Fcs.FcsRecordType.BUILDING))
                foreach (var s in r.GetReferences("wall subsections"))
                    (sectionOf.TryGetValue(s.TargetStringId, out var owners) ? owners : sectionOf[s.TargetStringId] = []).Add(r.StringId);
        }
        return sectionOf.TryGetValue(record, out var masters) && masters.Exists(research.IsUnlocked);
    }
    // Some character may still hold a site that is gone (the sweep in Schedule ends its task).
    bool sweep;

    public ConstructionSystem(GameDatabase db, GameConstants constants, BodyOptions options, ItemFactory items, IReadOnlyList<PlacementTown> towns, string playerFactionId, ConstructionSettings? settings = null)
    {
        this.db = db;
        this.constants = constants;
        this.items = items;
        this.towns = towns;
        this.playerFactionId = playerFactionId;
        Settings = settings ?? new ConstructionSettings();
        xp = new XpService(constants, options);
    }

    public ConstructionSystem(PopulationData data, ConstructionSettings? settings = null)
        : this(data.Db, data.Bodies.Constants, data.BodyOptions, data.Items, TownsOf(data), data.PlayerFaction >= 0 ? data.Factions[data.PlayerFaction].Id : "", settings)
    {
        population = data;
    }

    public ConstructionSettings Settings { get; }
    public GameConstants Constants => constants;

    static List<PlacementTown> TownsOf(PopulationData data)
    {
        var list = new List<PlacementTown>(data.Sites.Count);
        foreach (var site in data.Sites) list.Add(new PlacementTown(site.Town, new Vector2(site.Position.X, site.Position.Z)));
        return list;
    }

    /// <summary>The build data of a BUILDING record (null for any other record); read once.</summary>
    public BuildingBuildData? Data(string record)
    {
        if (!buildData.TryGetValue(record, out var data))
            buildData[record] = data = db.Find(record) is { Type: Meitou.Data.Fcs.FcsRecordType.BUILDING } r ? BuildingBuildData.From(r) : null;
        return data;
    }

    // ------------------------------------------------------------------ orders

    public void Inputs(World world, IReadOnlyList<SimCommand> commands)
    {
        var table = world.Characters;
        var reg = world.Buildings;
        foreach (var command in commands)
        {
            void Notice(string text) => world.Post(command.Issuer ?? PlayerId.Local, text);
            switch (command)
            {
                case PlaceBuildings place:
                {
                    if (world.CommandFaction(place, FactionPermission.ManageAssets) is not { Index: >= 0 } faction) break;
                    if (place.Plans.Count > 2048 || place.Issuer is not null && Settings.Placement is null) break;
                    var placed = new List<BuildSite>(place.Plans.Count);
                    foreach (var plan in place.Plans)
                        if (Place(world, plan, faction.Index, place.Issuer) is { } site) placed.Add(site);
                    TownForWalls(world, placed, faction.Index);
                    AttachWalls(world.Buildings, Data, placed);
                    foreach (int number in placed.Select(s => s.Town).Distinct())
                        if (reg.FindTown(number) is { } town) ReassessTown(world, town);
                    break;
                }
                case BuildOrder order:
                {
                    // Build and Repair are both Task_Build: an unfinished site is built, a damaged one (progress below its target) repaired; a whole building has nothing to do.
                    if (CommandSite(world, order, order.Site) is not { NeedsWork: true } site) break;
                    foreach (var id in world.CommandCharacters(order, order.Characters))
                        if (table.TryResolveNext(id, out int slot) && table.Cold(slot) is { IsPlayer: true } cold && Able(cold) && MayBuild(world, cold, site))
                        {
                            if (site.LevelPlacement is not null && site.Destroyed)
                            {
                                Ruin(world, site);
                                site.Destroyed = false;
                                world.Fixtures.RestoreTownShell(world, site);
                            }
                            if (site.Stalled && !Carries(cold, site) && Source(world, slot, cold, site) is null)
                                world.Bark(id, Loc.Tr("I don't have any building materials."));
                            Begin(world, slot, site, CharacterTask.Build, CharacterTask.Idle);
                        }
                    break;
                }
                case DismantleOrder order:
                {
                    if (CommandSite(world, order, order.Site) is not { LevelPlacement: null } site) break;
                    if (Blocker(reg, site) is { } text)
                    {
                        Notice(text);
                        break;
                    }
                    foreach (var id in world.CommandCharacters(order, order.Characters))
                        if (table.TryResolveNext(id, out int slot) && table.Cold(slot) is { IsPlayer: true } cold && Able(cold) && MayBuild(world, cold, site))
                            Begin(world, slot, site, CharacterTask.Dismantle, CharacterTask.Idle);
                    break;
                }
                case ConfirmDismantle confirm:
                    if (CommandSite(world, confirm, confirm.Site) is { LevelPlacement: null } doomed) DismantleAtOnce(world, doomed, confirm.Issuer);
                    break;
                case CancelSite cancel:
                    if (CommandSite(world, cancel, cancel.Site) is not { Complete: false, LevelPlacement: null } cancelled) break;
                    if (cancelled.Deliverable > 0) Notice("Dismantle it to get the materials back.");
                    else Remove(world, cancelled);
                    break;
                case PauseSite pause:
                    if (CommandSite(world, pause, pause.Site) is { } paused && paused.Paused != pause.Paused)
                    {
                        paused.Paused = pause.Paused;
                        reg.Touch();
                    }
                    break;
                case UpgradeSite up:
                    if (CommandSite(world, up, up.Site, root: false) is { LevelPlacement: null } upgraded && CanUpgrade(world, upgraded, upgraded.Owner) is { } higher) Swap(world, upgraded, higher, UpgradeProgressCap, upgrade: true);
                    break;
                case DowngradeSite down:
                    if (CommandSite(world, down, down.Site, root: false) is { LevelPlacement: null } downgraded && CanDowngrade(world, downgraded, downgraded.Owner) is { } lower) Swap(world, downgraded, lower, DowngradeProgressCap, upgrade: false);
                    break;
                case SetBuildingPublic open:
                    if (CommandSite(world, open, open.Building, root: false) is { } shown && shown.Public != open.Public)
                    {
                        shown.Public = open.Public;
                        if (shown.LevelPlacement is not null) world.Fixtures.SetPublic(shown.Id, open.Public);
                        reg.Touch();
                    }
                    break;
                // The panel enables its name box and tick only for a town of the player's faction (FUN_140722de0).
                case RenameTown rename:
                    if (CommandTown(world, rename, rename.Town) is { } renamed && rename.Name.Length is > 0 and <= 128 && renamed.Name != rename.Name)
                    {
                        renamed.Name = rename.Name;
                        reg.Touch();
                    }
                    break;
                case SetTownPublic tick:
                    if (CommandTown(world, tick, tick.Town) is { } outpost && outpost.Public != tick.Public)
                    {
                        outpost.Public = tick.Public;
                        reg.Touch();
                    }
                    break;
            }
        }
    }

    /// <summary>The progress an upgrade keeps at most, and a downgrade (material units; <b>Verified</b>, Building slots 75 / 76 cap +0x164 at 50 / 80).</summary>
    public const float UpgradeProgressCap = 50, DowngradeProgressCap = 80;

    /// <summary>
    /// The BUILDING a site of the player's can be upgraded to (docs/game/buildings-production.md "Upgrades"): its record's upgrade, unlocked by research. Engine scope: not walls (the
    /// original's WallBuilding upgrades the whole wall line; a gate is a GatewayBuilding and upgrades alone); a building with a machine gets one for the new record that keeps its stock
    /// (<see cref="Production.ProductionSystem"/>).
    /// </summary>
    public string? CanUpgrade(World world, BuildSite site) => CanUpgrade(world, site, world.Player.Faction);
    public string? CanUpgrade(World world, BuildSite site, int faction) =>
        Swappable(site, faction) && BuildingUpgrades.UpgradeOf(db, site.Record) is { } target && world.Factions.Find(faction)?.Research.IsUnlocked(target) == true && Data(target) is not null ? target : null;

    /// <summary>The BUILDING an unfinished site of the player's goes back to (the one that upgrades to it), shown while it is not complete; same scope as <see cref="CanUpgrade"/>.</summary>
    public string? CanDowngrade(World world, BuildSite site) => CanDowngrade(world, site, world.Player.Faction);
    public string? CanDowngrade(World world, BuildSite site, int faction) =>
        !site.Complete && Swappable(site, faction) && BuildingUpgrades.DowngradeOf(db, site.Record) is { } lower && Data(lower) is not null ? lower : null;

    bool Swappable(BuildSite site, int faction) =>
        faction >= 0 && site.Owner == faction && !site.Destroyed && site.WallParent is null && Data(site.Record) is { LinksTogether: false } or { IsWallGate: true };

    internal static BuildSite? CommandSite(World world, ConstructionSiteCommand command, string id, bool root = true)
    {
        var target = world.Buildings.Find(id);
        if (target is null || command.SiteRevision != 0 && world.BuildingRevision(target) != command.SiteRevision) return null;
        var site = root ? Root(world.Buildings, target) : target;
        if (site is null) return null;
        if (site != target && command.RootRevision != 0 && world.BuildingRevision(site) != command.RootRevision) return null;
        if (command.Issuer is not { } issuer) return site;
        return command.SiteRevision != 0 && (site == target || command.RootRevision != 0) && site.Owner >= 0
            && command.FactionGrantRevision != 0 && world.Players.Find(issuer) is { } player
            && player.MembershipRevision(site.Owner) == command.FactionGrantRevision && player.HasPermission(site.Owner, FactionPermission.ManageAssets) ? site : null;
    }

    static PlayerTown? CommandTown(World world, TownCommand command, int id)
    {
        if (world.CommandFaction(command, FactionPermission.ManageAssets) is not { } faction
            || world.Buildings.FindTown(id) is not { } town || town.Owner != faction.Index) return null;
        return command.Issuer is null && command.TownRevision == 0 || command.TownRevision != 0 && world.TownRevision(town) == command.TownRevision ? town : null;
    }

    /// <summary>
    /// Makes a site the BUILDING <paramref name="record"/> in place (Building slots 75 / 76, docs/game/buildings-production.md "Upgrades"): no longer complete or paused, progress capped
    /// at <paramref name="cap"/>, the new record's materials with what was delivered of each item carried over (engine reading of the re-applied state), the name the new record's.
    /// An <paramref name="upgrade"/> then takes each material still owed from the building's own stock, one unit at a time (<see cref="PullOwn"/>); a downgrade whose delivered
    /// materials already cover the new record is finished at once. Its doors stay while it is built (<see cref="BuildSite.Upgrading"/>).
    /// </summary>
    void Swap(World world, BuildSite site, string record, float cap, bool upgrade)
    {
        var data = Data(record)!;
        var had = new Dictionary<string, float>(StringComparer.Ordinal);
        foreach (var m in site.Materials) had[m.Item] = had.GetValueOrDefault(m.Item) + m.Delivered;
        var materials = new MaterialSlot[data.Materials.Count];
        for (int i = 0; i < materials.Length; i++)
        {
            var cost = data.Materials[i];
            float delivered = MathF.Min(had.GetValueOrDefault(cost.Item), cost.Required);
            had[cost.Item] = had.GetValueOrDefault(cost.Item) - delivered;
            materials[i] = new MaterialSlot(cost.Item, cost.Required) { Delivered = delivered };
        }
        site.Record = data.Id;
        site.Name = data.Name;
        site.Materials = materials;
        site.Target = data.Target;
        site.Rate = data.Rate;
        site.Complete = false;
        site.Paused = false;
        site.Upgrading = true;
        site.Progress = MathF.Min(site.Progress, MathF.Min(cap, site.Target));
        if (upgrade) PullOwn(world, site);
        if (!upgrade && site.Deliverable >= site.Target) Finish(world, site);
        else world.Buildings.Changed();
        SyncChildren(world, site);
    }

    /// <summary>
    /// An upgrade's materials from the building's own inventory (<b>Verified</b>, Building slot 75: for each material, int(required - delivered) times, an item of it is taken from the
    /// building's inventory and destroyed and one unit delivered, stopping when none is left). Meitou's inventory of a building is its machine's storage, then its input and output stock.
    /// </summary>
    static void PullOwn(World world, BuildSite site)
    {
        if (world.Machines.Find(site.Id) is not { } m) return;
        bool took = false;
        foreach (var slot in site.Materials)
            for (int owed = (int)(slot.Required - slot.Delivered); owed > 0; owed--)
            {
                if (m.Storage?.Items.Find(i => i.Record == slot.Item && i.Quantity > 0) is { } item) m.Storage.RemoveOne(item);
                else if (Array.Find(m.Inputs, i => i.Item == slot.Item && i.Count > 0) is { } input) input.Count--;
                else if (m.Output is { Count: > 0 } output && output.Recipe.Item == slot.Item) output.Count--;
                else break;
                slot.Delivered++;
                took = true;
            }
        if (took) world.Machines.Touch();
    }

    /// <summary>
    /// Why a site cannot be taken apart yet: a building standing in it (<b>Observed</b>; the original's texts, <b>Verified</b>, refuse while furniture or mounted buildings exist). Furniture is
    /// what is placed inside (interior furniture or a node), the rest counts as mounted.
    /// </summary>
    string? Blocker(BuildingRegistry reg, BuildSite site)
    {
        string? text = null;
        foreach (var other in reg.All)
        {
            if (other.Parent != site.Id) continue;
            var data = Data(other.Record);
            if (data is { IsInteriorFurniture: true } or { IsNode: true }) return FurnitureFirst;
            text = UnmountFirst;
        }
        return text;
    }

    BuildSite? Place(World world, PlannedBuilding plan, int faction, PlayerId? actor)
    {
        void Notice(string text) => world.Post(actor ?? PlayerId.Local, text);
        if (!float.IsFinite(plan.Position.X) || !float.IsFinite(plan.Position.Y) || !float.IsFinite(plan.Position.Z)
            || !float.IsFinite(plan.Rotation.LengthSquared()) || plan.Rotation.LengthSquared() < 1e-6f || plan.Floor < 0) return null;
        var data = Data(plan.Record);
        if (data is null || !Unlocked(world, plan.Record, faction))
        {
            Notice(PlacementRules.Message(PlacementFailure.NotAvailable));
            return null;
        }
        if (Settings.Placement?.Check(world, actor, faction, plan) is { } invalid && invalid != PlacementFailure.None)
        { Notice(PlacementRules.Message(invalid)); return null; }
        string factionRecord = population is not null && (uint)faction < (uint)population.Factions.Count ? population.Factions[faction].Id
            : faction == world.Player.Faction ? playerFactionId : "";
        var fail = plan.Parent is not null && Settings.Placement is not null ? PlacementFailure.None
            : PlacementRules.TownBlock(data, new Vector2(plan.Position.X, plan.Position.Z), towns, factionRecord);
        if (fail != PlacementFailure.None)
        {
            Notice(PlacementRules.Message(fail));
            return null;
        }
        var reg = world.Buildings;
        if (plan.Parent is { } parent && reg.Find(parent) is { } home && home.Owner != faction)
        { Notice(PlacementRules.Message(PlacementFailure.NotOwnedBuilding)); return null; }
        var materials = new MaterialSlot[data.Materials.Count];
        for (int i = 0; i < materials.Length; i++) materials[i] = new MaterialSlot(data.Materials[i].Item, data.Materials[i].Required);
        var site = new BuildSite
        {
            Id = reg.NewId(WorldLayout.ZoneOf(plan.Position.X, plan.Position.Z)),
            Record = data.Id,
            Name = data.Name,
            Position = plan.Position,
            Rotation = plan.Rotation,
            Floor = plan.Floor,
            Parent = plan.Parent,
            Owner = faction,
            Materials = materials,
            Target = data.Target,
            Rate = data.Rate,
        };
        site.Complete = site.Target <= 0;   // a total of 0 completes at once (Verified)
        site.Town = TownFor(world, site, data);
        reg.Add(site);
        return site;
    }

    // ------------------------------------------------------------------ player towns

    /// <summary>A ghost joins the nearest player town within this far (X/Z) (<b>Verified</b>, <c>FUN_1404d6d90</c>).</summary>
    public const float JoinTownRange = 5000;
    /// <summary>... or the town of the nearest player building within this far (<b>Verified</b>).</summary>
    public const float JoinBuildingRange = 2500;
    /// <summary>A new outpost takes in the player buildings whose nearest town within this far is it (<b>Verified</b>).</summary>
    public const float AdoptRange = 8000;
    /// <summary>Queued walls without a town take the nearest town within this far of their mean position, else a new outpost there (<b>Verified</b>, <c>FUN_1404db180</c>).</summary>
    public const float WallTownRange = 16000;

    /// <summary>
    /// The player town a new site belongs to (docs/game/buildings-production.md "Placement", Outposts): its parent building's; else the nearest player town within
    /// <see cref="JoinTownRange"/>; else the town of the nearest player building within <see cref="JoinBuildingRange"/>; else, for a <c>creates player town</c> building that is not a
    /// wall, a new outpost at its position, which also takes in the player buildings whose nearest town within <see cref="AdoptRange"/> it is. 0 when none.
    /// </summary>
    int TownFor(World world, BuildSite site, BuildingBuildData data)
    {
        var reg = world.Buildings;
        if (site.Parent is { } parent && reg.Find(parent) is { } home && home.Owner == site.Owner) return home.Town;
        int faction = site.Owner;
        PlayerTown? nearest = null;
        float best = float.MaxValue;
        foreach (var t in reg.Towns)
        {
            if (t.Owner != faction) continue;
            float d = DistanceXZ(t.Position, site.Position);
            if (d < best) { best = d; nearest = t; }
        }
        if (nearest is not null && best <= JoinTownRange) return nearest.Number;

        BuildSite? near = null;
        best = float.MaxValue;
        foreach (var s in reg.All)
        {
            if (s.Owner != faction) continue;
            float d = Vector3.Distance(s.Position, site.Position);
            if (d < best) { best = d; near = s; }
        }
        if (near is { Town: > 0 } && best < JoinBuildingRange) return near.Town;

        if (data.LinksTogether || !data.CreatesPlayerTown) return 0;
        var town = reg.AddTown(site.Position, faction);
        foreach (var s in reg.All)
            if (s.Owner == faction && NearestTown(reg, s.Position, AdoptRange, faction) == town.Number) s.Town = town.Number;
        return town.Number;
    }

    /// <summary>Confirm's second pass (<b>Verified</b>, <c>FUN_1404db180</c>): queued walls still without a town take the last player town of the queue, else the nearest town within
    /// <see cref="WallTownRange"/> of their mean position, else a new outpost there. A nearest town of another faction leaves them without one (engine: <see cref="BuildSite.Town"/> names player towns only).</summary>
    void TownForWalls(World world, List<BuildSite> placed, int faction)
    {
        int count = 0, last = 0;
        var sum = Vector3.Zero;
        foreach (var s in placed)
        {
            if (Data(s.Record) is { LinksTogether: true } && s.Town == 0)
            {
                count++;
                sum += s.Position;
            }
            else if (s.Town > 0) last = s.Town;
        }
        if (count == 0) return;
        int town = last;
        if (town == 0)
        {
            var mean = sum / count;
            town = NearestTown(world.Buildings, mean, WallTownRange, faction);
            if (town == 0) town = world.Buildings.AddTown(mean, faction).Number;
            else if (town < 0) return;
        }
        foreach (var s in placed)
            if (Data(s.Record) is { LinksTogether: true } && s.Town == 0) s.Town = town;
    }

    /// <summary>The nearest town (X/Z) within <paramref name="radius"/> of any faction: a player town's number, -1 for a placed town of the level data, 0 for none.</summary>
    int NearestTown(BuildingRegistry reg, Vector3 at, float radius, int faction)
    {
        int found = 0;
        float best = radius * radius;
        var p = new Vector2(at.X, at.Z);
        foreach (var t in towns)
        {
            float d = Vector2.DistanceSquared(t.Position, p);
            if (d <= best) { best = d; found = -1; }
        }
        foreach (var t in reg.Towns)
        {
            float d = Vector2.DistanceSquared(new Vector2(t.Position.X, t.Position.Z), p);
            if (d <= best) { best = d; found = t.Owner == faction ? t.Number : -1; }
        }
        return found;
    }

    /// <summary>A reassessed town's radius: this much beyond its farthest building, at least <see cref="TownRadiusStart"/>, within [1000, 20000] (<b>Verified</b>, <c>FUN_1405488f0</c>).</summary>
    public const float TownRadiusMargin = 200, TownRadiusStart = 1250, TownRadiusMin = 1000, TownRadiusMax = 20000;

    /// <summary>
    /// Moves a player town's centre to its buildings and sizes its radius to them (<c>Town::reassessTownPosition</c>, docs/game/buildings-production.md "Outposts"): the mean of its
    /// outdoor buildings, on walkable ground when some is near, then <see cref="TownRadiusMargin"/> beyond the farthest. Engine choice: a plain mean (the original weighs two building sets,
    /// <b>Unknown</b> which); a town without buildings keeps its place.
    /// </summary>
    public static void ReassessTown(World world, PlayerTown town)
    {
        var sum = Vector3.Zero;
        int count = 0;
        foreach (var s in world.Buildings.All)
            if (s.Owner == town.Owner && s.Town == town.Number && s.Parent is null) { sum += s.Position; count++; }
        if (count == 0) return;
        var centre = sum / count;
        if (WalkableSpots.Nearest(world.Walkability, new Vector2(centre.X, centre.Z), out var spot)) centre = new Vector3(spot.X, centre.Y, spot.Y);
        float radius = TownRadiusStart;
        foreach (var s in world.Buildings.All)
            if (s.Owner == town.Owner && s.Town == town.Number) radius = MathF.Max(radius, DistanceXZ(s.Position, centre) + TownRadiusMargin);
        town.Position = centre;
        town.Override = Math.Clamp(radius, TownRadiusMin, TownRadiusMax);
    }

    /// <summary>Reassesses every player town (<see cref="ReassessTown"/>).</summary>
    public static void ReassessTowns(World world)
    {
        foreach (var town in world.Buildings.Towns) ReassessTown(world, town);
    }

    static float DistanceXZ(Vector3 a, Vector3 b)
    {
        float dx = a.X - b.X, dz = a.Z - b.Z;
        return MathF.Sqrt(dx * dx + dz * dz);
    }

    /// <summary>Removes a site and the wall pieces attached to it (slot 73 forwards a dismantle to the children).</summary>
    void Remove(World world, BuildSite site)
    {
        var reg = world.Buildings;
        if (IsWall(site))
            foreach (var child in reg.All.Where(s => s.WallParent == site.Id).ToList())
                if (child.Owner == site.Owner) reg.Remove(child); else child.WallParent = null;
        reg.Remove(site);
        if (reg.FindTown(site.Town) is { } town) ReassessTown(world, town);
        sweep = true;
    }

    // ------------------------------------------------------------------ attached wall pieces

    bool IsWall(BuildSite site) => Data(site.Record) is { LinksTogether: true };

    /// <summary>The site that takes a site's work and orders: the root of its <see cref="BuildSite.WallParent"/> chain, else the site itself (a child's slot 69 forwards to its parent's).</summary>
    public static BuildSite? Root(BuildingRegistry reg, BuildSite? site)
    {
        for (int guard = 0; guard < 64 && site?.WallParent is { } up && reg.Find(up) is { } parent; guard++)
        {
            if (parent.Owner != site.Owner) return null;
            site = parent;
        }
        return site;
    }

    /// <summary>Two link points closer than the square root of this are joined (<b>Verified</b>; <c>WallLine.JoinDistanceSquared</c>).</summary>
    public const float JoinDistanceSquared = 10;

    /// <summary>
    /// Attaches each short piece, connector or wedge (<c>link type</c> 4, 1, 2) of <paramref name="sites"/> without a parent or children to a nearby wall site (<b>Observed</b>, <c>FUN_14029ac50</c>
    /// on Confirm and zone load; its check <c>FUN_14029a890</c>: another building, the same complete flag, the candidate's root taken). Nearby: one of the piece's link points or its middle within
    /// sqrt(<see cref="JoinDistanceSquared"/>) of a link point of the candidate (the original finder's radius is <b>Unknown</b>; the middle catches a crest short centred on a joint). Engine choice:
    /// a root that is a normal wall or single wins over one that is itself a subsection; gates are never parents (not WallBuildings).
    /// </summary>
    public static void AttachWalls(BuildingRegistry reg, Func<string, BuildingBuildData?> data, IEnumerable<BuildSite> sites)
    {
        foreach (var child in sites)
        {
            if (child.WallParent is not null || data(child.Record) is not { LinksTogether: true, LinkType: 1 or 2 or 4 } cd || reg.Find(child.Id) != child) continue;
            if (reg.All.Any(s => s.WallParent == child.Id)) continue;
            var (c0, c1) = LinkEnds(cd, child);
            var mid = (c0 + c1) / 2;
            BuildSite? pick = null, fallback = null;
            foreach (var w in reg.All)
            {
                if (w == child || w.Owner != child.Owner || data(w.Record) is not { LinksTogether: true } wd) continue;
                var (w0, w1) = LinkEnds(wd, w);
                if (!Joined(c0, w0, w1) && !Joined(c1, w0, w1) && !Joined(mid, w0, w1)) continue;
                var root = Root(reg, w);
                if (root is null) continue;
                if (root == child || root.Complete != child.Complete || data(root.Record) is not { IsWallGate: false } rd) continue;
                if (rd.LinkType is 0 or 3)
                {
                    pick = root;
                    break;
                }
                fallback ??= root;
            }
            if ((pick ?? fallback) is not { } parent) continue;
            child.WallParent = parent.Id;
            child.Progress = Share(parent, child);
            reg.Touch();
        }
    }

    static bool Joined(Vector3 p, Vector3 a, Vector3 b) => Vector3.DistanceSquared(p, a) < JoinDistanceSquared || Vector3.DistanceSquared(p, b) < JoinDistanceSquared;

    // A wall's start (its position) and end (position + rotation x (0, 0, link length)), as WallLine.Ends.
    static (Vector3 Start, Vector3 End) LinkEnds(BuildingBuildData wall, BuildSite site) =>
        (site.Position, site.Position + Vector3.Transform(new Vector3(0, 0, wall.LinkLength), Quaternion.Normalize(site.Rotation)));

    /// <summary>An attached piece's progress (<b>Verified</b>, parent slot 71): <c>min(parent progress / parent target, 1) x child target</c>.</summary>
    static float Share(BuildSite parent, BuildSite child) =>
        (parent.Target > 0 ? MathF.Min(parent.Progress / parent.Target, 1) : 1) * child.Target;

    /// <summary>Sets the attached pieces' progress and complete flag from their wall's (slots 71, 72).</summary>
    void SyncChildren(World world, BuildSite parent)
    {
        if (!IsWall(parent)) return;
        var reg = world.Buildings;
        foreach (var c in reg.All)
        {
            if (c.WallParent != parent.Id || c.Owner != parent.Owner) continue;
            c.Progress = Share(parent, c);
            if (c.Complete == parent.Complete) continue;
            c.Complete = parent.Complete;
            if (c.Complete) c.Destroyed = c.Ruined = false;
            reg.Changed();
        }
    }

    // ------------------------------------------------------------------ builders

    static bool IsWork(byte task) => task is (byte)CharacterTask.Build or (byte)CharacterTask.Dismantle or (byte)CharacterTask.DeliverMaterials or (byte)CharacterTask.FetchMaterials;

    /// <summary>A character that can work a site: awake, free to act, not in a fight, not carrying or carried, not in a cage.</summary>
    static bool Able(CharacterCold cold) =>
        cold.Medical is not { Incapacitated: true } && !cold.InCombat && cold.CarriedBy.IsNone && cold.Carrying.IsNone && !cold.IsImprisoned;

    static bool MayBuild(World world, CharacterCold worker, BuildSite site) => world.MayUseAssets(worker, site.Owner);

    static bool MayFetch(World world, CharacterCold worker, BuildSite site, BuildSite source) => source.Owner == site.Owner
        && world.MayUseAssets(worker, source.Owner) && (!world.Players.RequiresExplicitControl || !worker.IsPlayer
            || world.Fixtures.Find(source.Id)?.Lock is not { EffectivelyLocked: true } && world.Machines.Find(source.Id) is not { Closed: true });

    /// <summary>A builder's reach of a site (<see cref="SiteReach.Building"/>, a wall's <see cref="SiteReach.Wall"/>).</summary>
    float ReachOf(BuildSite site) => IsWall(site) ? SiteReach.Wall : SiteReach.Building;

    /// <summary>
    /// Whether the worker is near enough its goal (<see cref="SiteReach.Within"/>): a site by its footprint, a ground item by its position (within <see cref="PlayerSystem.PickUpReach"/>,
    /// twice that once stopped).
    /// </summary>
    bool Arrived(in CharacterHot n, (Vector3 At, BuildSite? Site, float Reach) goal) =>
        SiteReach.Within(n, goal.Site is { } site ? SiteReach.Gap(Settings.EdgeGap, site, n.Position) : Vector2.Distance(new(n.Position.X, n.Position.Z), new(goal.At.X, goal.At.Z)), goal.Reach);

    /// <summary>
    /// Where a worker goes and from how near it works: the site, or for a fetcher its source (a storage by its machine's <c>use range</c>, the original's <c>UseableStuff::getReachRange</c>;
    /// a ground item). Null when the fetcher's source is gone.
    /// </summary>
    (Vector3 At, BuildSite? Site, float Reach)? Goal(World world, CharacterCold cold, byte task, BuildSite site)
    {
        if (task != (byte)CharacterTask.FetchMaterials) return (site.Position, site, ReachOf(site));
        if (cold.FetchItem is { } item) return world.GroundItems.Find(item) is { } loose && (loose.OwnedBy < 0 || loose.OwnedBy == site.Owner)
            && loose.Item.Record == cold.FetchRecord ? (loose.Position, null, PlayerSystem.PickUpReach) : null;
        return cold.FetchSite is { } from && world.Buildings.Find(from) is { Complete: true } store && MayFetch(world, cold, site, store) && world.Machines.Find(from) is { } machine
            ? (store.Position, store, machine.Data.UseRange) : null;
    }

    /// <summary>Gives the character a site to work (serial phases): it stands, then walks there unless it is there already. <paramref name="resume"/> is the task to go back to (kept if already working).</summary>
    void Begin(World world, int slot, BuildSite site, CharacterTask task, CharacterTask resume)
    {
        var table = world.Characters;
        var cold = table.Cold(slot)!;
        if (!MayBuild(world, cold, site)) return;
        ref var n = ref table.Next[slot];
        var keep = IsWork(n.Task) ? cold.ResumeTask : resume;
        MovementSystem.Stop(table, slot);
        n.Task = (byte)task;
        n.Mode = SpeedMode.Free;
        n.TaskTime = 0;
        cold.BuildSite = site.Id;
        cold.BuildBar = 0;
        ClearFetch(cold);
        cold.ResumeTask = keep;
        if (!Arrived(n, (site.Position, site, ReachOf(site))))
        {
            n.Goal = new Vector2(site.Position.X, site.Position.Z);
            n.Flags |= (ushort)MoveFlags.NeedPath;
        }
        sweep = true;
    }

    /// <summary>Ends the work (serial phases): back to the task before it, path dropped.</summary>
    static void End(CharacterTable table, int slot)
    {
        var cold = table.Cold(slot)!;
        ref var n = ref table.Next[slot];
        if (IsWork(n.Task))
        {
            MovementSystem.Stop(table, slot);
            n.Task = (byte)cold.ResumeTask;
            n.TaskTime = 0;
        }
        cold.BuildSite = null;
        cold.BuildBar = 0;
        ClearFetch(cold);
        cold.ResumeTask = CharacterTask.Idle;
    }

    static void ClearFetch(CharacterCold cold)
    {
        cold.FetchSite = null;
        cold.FetchItem = null;
        cold.FetchRecord = null;
    }

    /// <summary>The engineer job's start (<see cref="Production.ProductionSystem"/> runs the lists): the character works <paramref name="site"/> and then goes back to <paramref name="resume"/>.</summary>
    public void StartBuilding(World world, int slot, BuildSite site, CharacterTask resume) => Begin(world, slot, site, CharacterTask.Build, resume);

    public void Schedule(World world)
    {
        var reg = world.Buildings;
        if (reg.Count == 0 && !sweep) return;
        var table = world.Characters;
        var next = table.Next;
        float dt = world.TickSeconds;
        for (int i = 0; i < table.HighWater; i++)
        {
            ref var n = ref next[i];
            if (!n.Alive) continue;
            var cold = table.Cold(i)!;
            if (cold.BuildSite is not { } id) continue;
            if (!IsWork(n.Task))
            {
                // Another order replaced the work.
                cold.BuildSite = null;
                cold.BuildBar = 0;
                ClearFetch(cold);
                continue;
            }
            var site = Root(reg, reg.Find(id));
            if (site is null || !Able(cold) || !MayBuild(world, cold, site) || (n.Task != (byte)CharacterTask.Dismantle && !site.NeedsWork))
            {
                End(table, i);
                continue;
            }
            if (Goal(world, cold, n.Task, site) is not { } goal)
            {
                BackToSite(ref n, cold);
                continue;
            }
            const ushort Path = (ushort)MoveFlags.AnyPath;
            if (Arrived(n, goal))
            {
                // There: stand still and let the slow world work.
                if ((n.Flags & Path) != 0)
                {
                    n.Flags = (ushort)(n.Flags & ~Path);
                    n.PathCursor = 0;
                    cold.PathRequest = 0;
                }
                n.TaskTime = 0;
                continue;
            }
            // On the way, or not started (a path that failed or ended short is asked for again after a wait).
            if ((n.Flags & Path) != 0) continue;
            n.TaskTime -= dt;
            if (n.TaskTime > 0) continue;
            n.Goal = new Vector2(goal.At.X, goal.At.Z);
            n.Mode = SpeedMode.Free;
            n.Flags |= (ushort)MoveFlags.NeedPath;
            n.TaskTime = PursuitSystem.RepathSeconds;
        }
        if (reg.Count == 0) sweep = false;
    }

    // ------------------------------------------------------------------ the work

    public void SlowWorld(World world)
    {
        var reg = world.Buildings;
        if (reg.Count == 0) return;
        foreach (var s in reg.All)
        {
            s.BuilderCounter = 0;
            if (s.Dismantling)
            {
                s.Dismantling = false;
                reg.Touch();
            }
            if (s is { Destroyed: true, Ruined: false, LevelPlacement: null })
            {
                Ruin(world, s);
                if (s.WallParent is null) SyncChildren(world, s);
                else if (reg.Find(s.WallParent) is { } wall)
                {
                    // A destroyed attached piece follows its wall again (what the original does with it is Unknown).
                    s.Destroyed = s.Ruined = false;
                    s.Complete = wall.Complete;
                    s.Progress = Share(wall, s);
                }
            }
        }
        var table = world.Characters;
        var next = table.Next;
        float dt = world.TickSeconds;
        for (int i = 0; i < table.HighWater; i++)
        {
            ref var n = ref next[i];
            if (!n.Alive || !IsWork(n.Task)) continue;
            var cold = table.Cold(i)!;
            if (cold.BuildSite is not { } id) continue;
            var site = Root(reg, reg.Find(id));
            if (site is null || !Able(cold) || !MayBuild(world, cold, site))
            {
                End(table, i);
                continue;
            }
            if (Goal(world, cold, n.Task, site) is not { } goal)
            {
                BackToSite(ref n, cold);
                continue;
            }
            if (!Arrived(n, goal)) continue;
            if (cold.StaggeredUntil > world.Tick) continue;   // a stagger interrupts the work
            if (n.Velocity.X == 0 && n.Velocity.Z == 0) n.Yaw = MathF.Atan2(goal.At.X - n.Position.X, goal.At.Z - n.Position.Z);
            switch ((CharacterTask)n.Task)
            {
                case CharacterTask.Build: BuildStep(world, i, cold, site, dt); break;
                case CharacterTask.DeliverMaterials: DeliverStep(world, i, cold, site, dt); break;
                case CharacterTask.Dismantle: DismantleStep(world, i, cold, site, dt); break;
                case CharacterTask.FetchMaterials: FetchStep(world, i, cold); break;
            }
        }
    }

    static float Engineering(CharacterCold cold) =>
        (cold.Stats?[StatsEnumerated.Engineering] ?? 0) * (cold.Medical?.StatMultiplier(StatsEnumerated.Engineering) ?? 1);

    static float Lerp(float a, float b, float t) => a + (b - a) * t;

    void BuildStep(World world, int slot, CharacterCold cold, BuildSite site, float dt)
    {
        var table = world.Characters;
        if (!site.NeedsWork)
        {
            End(table, slot);
            return;
        }
        if (site.Stalled)
        {
            // Everything given is built: hand over what is carried, else fetch a missing material (the planner's 115 -> 247), else the work ends.
            if (Carries(cold, site))
            {
                table.Next[slot].Task = (byte)CharacterTask.DeliverMaterials;
                cold.BuildBar = 0;
            }
            else if (Source(world, slot, cold, site) is { } source) Fetch(world, slot, cold, source);
            else End(table, slot);
            return;
        }
        float skill = Engineering(cold);
        float amount = Lerp(0.3f, 2f, Math.Clamp(skill, 0, 100) / 100) * dt;
        int k = site.BuilderCounter;
        if (k > CounterRestart) k = site.BuilderCounter = 0;
        site.BuilderCounter++;
        float scaled = amount * Settings.BuildingSpeed * constants.BuildSpeed * Falloff[Math.Min(k, CounterCap)];
        site.Progress = site.Rate > 0 ? site.Progress + site.Rate * scaled : site.Target;
        if (site.Progress > 0) SetBroken(world, site, false);
        world.Buildings.Touch();
        cold.UseSkill(StatsEnumerated.Engineering, world.Tick);
        if (cold.Stats is { } stats && cold.Race is { } race) xp.Engineering(stats, race, dt);
        // A repair (a complete building never stalls, so it takes no materials) stops at the target (the clamp is the engine's; Task_Build ends at progress / target >= 1).
        if (site.Complete) site.Progress = MathF.Min(site.Progress, site.Target);
        if (site.Progress >= site.Target && !site.Complete) Finish(world, site);
        else SyncChildren(world, site);
    }

    void Finish(World world, BuildSite site)
    {
        site.Progress = site.Target;
        site.Complete = true;
        site.Destroyed = false;
        site.Upgrading = false;
        site.Ruined = false;
        world.Buildings.Changed();
        SyncChildren(world, site);
        SetBroken(world, site, false);
        if (world.IsPlayerFaction(site.Owner)) world.PostFaction(site.Owner, Loc.Tr("Building '{1}' construction complete.", site.Name));
    }

    /// <summary>The first tick on a destroyed building (<b>Verified</b>, <c>FUN_1405595a0</c>): no longer complete, progress 0.4 x target, each delivered amount floor(delivered x U[0.3, 0.5) + 0.5).</summary>
    static void Ruin(World world, BuildSite site)
    {
        site.Complete = false;
        site.Ruined = true;
        site.Progress = 0.4f * site.Target;
        if (site.Progress > 0) SetBroken(world, site, false);
        ulong key = Rng.StableHash(site.Id);
        for (int i = 0; i < site.Materials.Length; i++)
        {
            float u = 0.3f + 0.2f * Rng.Float(Rng.Hash(world.Seed, key, RngPurpose.Build, 1000 + (ulong)i));
            site.Materials[i].Delivered = MathF.Floor(site.Materials[i].Delivered * u + 0.5f);
        }
        world.Buildings.Changed();
    }

    // ------------------------------------------------------------------ materials

    /// <summary>One unit of <paramref name="record"/> in the inventory, then in its backpack (<b>Verified</b>, the order of <c>Task_AddMaterialsToBuilding</c>).</summary>
    static (ItemInstance Item, Inventory Owner)? Find(Inventory? inventory, string record)
    {
        if (inventory is null) return null;
        foreach (var item in inventory.Items)
            if (item.Record == record && item.Quantity > 0 && item.Contents is null) return (item, inventory);
        if (inventory.Backpack?.Contents is { } bag)
            foreach (var item in bag.Items)
                if (item.Record == record && item.Quantity > 0) return (item, bag);
        return null;
    }

    /// <summary>The character carries a unit of something the site still needs.</summary>
    static bool Carries(CharacterCold cold, BuildSite site)
    {
        foreach (var m in site.Materials)
            if (m.Required > m.Delivered && Find(cold.Inventory, m.Item) is not null) return true;
        return false;
    }

    /// <summary>Where a builder fetches a material from: a building (<paramref name="Site"/>) or a ground item (<paramref name="Ground"/>), and the record it takes.</summary>
    readonly record struct MaterialSource(string? Site, int? Ground, string Record);

    /// <summary>
    /// The material finder (<b>Verified</b>, the 115 "Finding building mats" target finder <c>FUN_1405a2a60</c> -> <c>FUN_14059d000</c>; docs/game/buildings-production.md "Material delivery"):
    /// for each material still owed, in the site's order, the first that has a source wins: a loose item of it within <see cref="GroundRadius"/> of the builder, else the nearest player storage
    /// holding it within <see cref="SourceRangeSquared"/>, else the nearest player machine whose output holds it. Engine choices: the nearest loose item (the original takes the first its grid
    /// search meets), no town filter, no per-material memory of the last source, the exact record (the original also lets any record of the same type stand in for a non-ITEM material);
    /// a loose item another faction owns (a town interior's, <see cref="Items.ItemSlots"/>) is left, as the original leaves items lying in another faction's building.
    /// </summary>
    MaterialSource? Source(World world, int slot, CharacterCold cold, BuildSite site)
    {
        var at = world.Characters.Next[slot].Position;
        foreach (var material in site.Materials)
        {
            if (material.Required <= material.Delivered) continue;
            string want = material.Item;
            int? loose = null;
            float best = GroundRadius * GroundRadius;
            foreach (var g in world.GroundItems.All)
            {
                if (g.Item.Record != want || g.Item.Quantity <= 0 || g.Item.Contents is not null || g.OwnedBy >= 0 && g.OwnedBy != site.Owner) continue;
                float d = Vector3.DistanceSquared(g.Position, at);
                if (d <= best && (loose is null || d < best)) (loose, best) = (g.Id, d);
            }
            if (loose is not null) return new MaterialSource(null, loose, want);
            string? store = null, machine = null;
            float storeGap = float.MaxValue, machineGap = float.MaxValue;
            foreach (var m in world.Machines.All)
            {
                if (world.Buildings.Find(m.Site) is not { Complete: true } building || !MayFetch(world, cold, site, building)) continue;
                float d = Vector3.DistanceSquared(building.Position, at);
                if (d > SourceRangeSquared) continue;
                if (m.Data.IsStorage)
                {
                    if (d < storeGap && m.Storage is { } inv && Stack(inv, want) is not null) (store, storeGap) = (m.Site, d);
                }
                else if (d < machineGap && m.Output is { Count: > 0 } output && output.Recipe.Item == want) (machine, machineGap) = (m.Site, d);
            }
            if ((store ?? machine) is { } from) return new MaterialSource(from, null, want);
        }
        return null;
    }

    /// <summary>A loose stack of <paramref name="record"/> in a storage's grid.</summary>
    static ItemInstance? Stack(Inventory storage, string record) =>
        storage.Items.Find(i => i.Record == record && i.Section == "main" && i.Quantity > 0 && i.Contents is null);

    /// <summary>Sends a builder for a material (serial phases); <c>Schedule</c> walks it there.</summary>
    static void Fetch(World world, int slot, CharacterCold cold, MaterialSource source)
    {
        var table = world.Characters;
        MovementSystem.Stop(table, slot);
        ref var n = ref table.Next[slot];
        n.Task = (byte)CharacterTask.FetchMaterials;
        n.Mode = SpeedMode.Free;
        n.TaskTime = 0;
        cold.BuildBar = 0;
        cold.FetchSite = source.Site;
        cold.FetchItem = source.Ground;
        cold.FetchRecord = source.Record;
    }

    /// <summary>A fetcher goes back to building its site (it walks there next <c>Schedule</c>).</summary>
    static void BackToSite(ref CharacterHot n, CharacterCold cold)
    {
        ClearFetch(cold);
        n.Task = (byte)CharacterTask.Build;
        n.Flags = (ushort)(n.Flags & ~(ushort)MoveFlags.AnyPath);
        n.TaskTime = 0;
    }

    /// <summary>
    /// A fetcher at its source: a loose item goes into the inventory whole (what does not fit stays on the ground, <c>Task_Pickup</c>); a building gives up to <see cref="PickupUnits"/> units
    /// while the character has room (<b>Verified</b>, <c>Task_EmptyMachine</c> update <c>FUN_14033cd10</c>; the original takes the first record the building lists, Meitou the material). With
    /// something taken it goes back to the site, else the work ends.
    /// </summary>
    void FetchStep(World world, int slot, CharacterCold cold)
    {
        var table = world.Characters;
        ref var n = ref table.Next[slot];
        if (Root(world.Buildings, world.Buildings.Find(cold.BuildSite ?? "")) is not { } site || !MayBuild(world, cold, site)) { End(table, slot); return; }
        int taken = 0;
        if (cold.Inventory is { } inv && cold.FetchRecord is { } record)
        {
            if (cold.FetchItem is { } id && world.GroundItems.Find(id) is { } loose && loose.Item.Record == record
                && (loose.OwnedBy < 0 || loose.OwnedBy == site.Owner))
            {
                int before = loose.Item.Quantity;
                if (inv.Add(loose.Item))
                {
                    world.GroundItems.Take(id);
                    taken = before;
                }
                else taken = before - loose.Item.Quantity;
                if (taken > 0) world.EmitSound(SoundKind.PickedUp, slot, n.Position);
            }
            else if (cold.FetchSite is { } from && world.Buildings.Find(from) is { Complete: true } source && MayFetch(world, cold, site, source) && world.Machines.Find(from) is { } m)
            {
                for (; taken < PickupUnits; taken++)
                {
                    if (m.Storage is { } storage && m.Data.IsStorage)
                    {
                        if (Stack(storage, record) is not { } stack || !inv.Add(stack.WithQuantity(1))) break;
                        storage.RemoveOne(stack);
                    }
                    else if (m.Output is { Count: > 0 } output && output.Recipe.Item == record && items.Create(record, 1) is { } unit)
                    {
                        if (!inv.Add(unit)) break;
                        output.Count--;
                    }
                    else break;
                }
                if (taken > 0) world.Machines.Touch();
            }
        }
        if (taken > 0) BackToSite(ref n, cold);
        else End(table, slot);
    }

    void DeliverStep(World world, int slot, CharacterCold cold, BuildSite site, float dt)
    {
        var table = world.Characters;
        cold.BuildBar += dt * BarRate;
        if (cold.BuildBar < 1) return;
        cold.BuildBar = 0;
        int given = 0;
        foreach (var m in site.Materials)
        {
            if (m.Required <= m.Delivered || Find(cold.Inventory, m.Item) is not { } found) continue;
            found.Owner.RemoveOne(found.Item);
            m.Delivered += 1;
            given++;
        }
        if (given > 0) world.Buildings.Touch();
        // On with building; with nothing handed over the planner's next step is the building task's own (fetch or end when stalled).
        table.Next[slot].Task = (byte)CharacterTask.Build;
    }

    /// <summary>
    /// A machine's broken flag (UseableStuff <c>setBroken</c>, <c>+0x3b6</c>; plain buildings have none, docs/game/buildings-production.md "Broken machines"): every dismantle step sets it
    /// (<c>Building::addDismantleProgress</c> <c>FUN_1402a2860</c>); a build step leaving progress above 0 (<c>FUN_1405595a0</c>) and completion (<c>FUN_140562340</c>) clear it. A cage's
    /// or safe's own lock follows it (<see cref="Locks.LockState.OwnerBroken"/>).
    /// </summary>
    static void SetBroken(World world, BuildSite site, bool broken)
    {
        if (world.Machines.Find(site.Id) is { } m && m.Broken != broken) m.Broken = broken;
        if (world.Fixtures.Find(site.Id)?.Lock is { } own) own.OwnerBroken = broken;
    }

    // ------------------------------------------------------------------ dismantling

    void DismantleStep(World world, int slot, CharacterCold cold, BuildSite site, float dt)
    {
        if (site.Complete)
        {
            // A complete building drops to target - 1 first and stops being complete.
            site.Complete = false;
            site.Progress = MathF.Min(site.Progress, MathF.Max(site.Target - 1, 0));
            world.Buildings.Changed();
        }
        float amount = dt * Lerp(0.05f, 4f, Math.Clamp(Engineering(cold), 0, 100) / 100) * 4;
        site.Progress = site.Rate > 0 ? site.Progress - amount * site.Rate : 0;
        site.Dismantling = true;
        SetBroken(world, site, true);
        world.Buildings.Touch();
        SyncChildren(world, site);
        cold.UseSkill(StatsEnumerated.Engineering, world.Tick);
        if (cold.Stats is { } stats && cold.Race is { } race) xp.Engineering(stats, race, dt);
        if (site.Progress > 0) return;
        Refund(world, site);
        foreach (var c in world.Buildings.All)
            if (c.WallParent == site.Id && c.Owner == site.Owner) Refund(world, c);   // nothing unless a save from before the link gave it materials
        world.EmitSound(SoundKind.DismantleDone, slot, world.Characters.Next[slot].Position);
        Remove(world, site);
    }

    /// <summary>The selection panel's refusal while other buildings are mounted on the site (<b>Verified</b> string, slot 84 <c>FUN_14054db30</c>).</summary>
    public const string MountedRefusal = "You cannot delete this building while it has mounted buildings.";

    /// <summary>
    /// The panel's Dismantle after its confirmation (<c>Building::confirmDismantle</c> <c>FUN_14054fea0</c>, <b>Observed</b>): refused with <see cref="MountedRefusal"/> while a building
    /// other than furniture stands in it (the panel's own test, engine reading: Meitou's mounted buildings are the non-furniture children); otherwise its furniture is destroyed (no
    /// refund) and the dismantle step runs with an endless amount (slot 73 with <c>FLT_MAX</c>), so the site goes at once with the usual refund.
    /// </summary>
    void DismantleAtOnce(World world, BuildSite site, PlayerId? actor = null)
    {
        var reg = world.Buildings;
        var children = reg.All.Where(o => o.Parent == site.Id).ToList();
        if (children.Any(o => o.Owner != site.Owner || Data(o.Record) is not ({ IsInteriorFurniture: true } or { IsNode: true })))
        {
            world.Post(actor ?? PlayerId.Local, MountedRefusal);
            return;
        }
        foreach (var furniture in children) Remove(world, furniture);
        site.Complete = false;
        site.Progress = 0;
        site.Dismantling = true;
        SetBroken(world, site, true);
        Refund(world, site);
        foreach (var c in reg.All)
            if (c.WallParent == site.Id && c.Owner == site.Owner) Refund(world, c);
        Remove(world, site);
        world.Buildings.Touch();
    }

    /// <summary>
    /// The refund (<b>Verified</b>, <c>FUN_14029dc10</c>): each entry with something delivered gives <c>max(1, trunc(U[m, 1) x delivered))</c> units, m = CONSTANTS <c>min dismantle materials percentage</c> / 100,
    /// each a ground item of its own at a random point of the building's bounds. Engine choice: the bounds are the square of the footprint radius (at least <see cref="RefundSpread"/>)
    /// about the building's position, at its height (the host lays dropped items on the ground).
    /// </summary>
    const float RefundSpread = 20;

    void Refund(World world, BuildSite site)
    {
        float m = constants.MinDismantleMaterialsFraction;
        float r = MathF.Max(Settings.Footprint?.Invoke(site.Record) ?? 0, RefundSpread);
        ulong key = Rng.StableHash(site.Id);
        for (int i = 0; i < site.Materials.Length; i++)
        {
            var material = site.Materials[i];
            if (material.Delivered <= 0) continue;
            float u = m + (1 - m) * Rng.Float(Rng.Hash(world.Seed, key, RngPurpose.Build, (ulong)i));
            int count = Math.Max(1, (int)(u * material.Delivered));
            for (int k = 0; k < count; k++)
            {
                if (items.Create(material.Item, 1) is not { } item) break;
                ulong stream = 2000 + ((ulong)i << 16) + 2 * (ulong)k;
                float x = (2 * Rng.Float(Rng.Hash(world.Seed, key, RngPurpose.Build, stream)) - 1) * r;
                float z = (2 * Rng.Float(Rng.Hash(world.Seed, key, RngPurpose.Build, stream + 1)) - 1) * r;
                world.GroundItems.Drop(item, site.Position + new Vector3(x, 0, z), 0);
            }
        }
    }

    // ------------------------------------------------------------------ the engineer job

    /// <summary>
    /// The site the engineer job (125) works next (<b>Verified</b>, <c>BuildingFinderForEngineers</c> <c>FUN_1409a6900</c> / <c>FUN_1409a6c10</c>): the character's faction's unfinished or
    /// damaged, unpaused, not destroyed, not dismantled sites; score <c>max(2 (1 - d^2/9000^2), 0.0001) / (n + 1)</c>, d the 3D distance, n the others working it; the highest wins (the first on a tie). A stalled site the
    /// character carries nothing for counts only when <see cref="Source"/> finds a material. Null: none.
    /// </summary>
    public BuildSite? EngineerSite(World world, int slot)
    {
        var reg = world.Buildings;
        if (!reg.AnyNeedsWork) return null;
        var table = world.Characters;
        var cold = table.Cold(slot)!;
        var at = table.Next[slot].Position;
        BuildSite? best = null;
        float bestScore = 0;
        foreach (var site in reg.All)
        {
            if (!site.NeedsWork || site.Paused || site.Destroyed || site.Dismantling || site.WallParent is not null || site.Owner != cold.Faction || !MayBuild(world, cold, site)) continue;
            if (site.Stalled && !Carries(cold, site) && Source(world, slot, cold, site) is null) continue;
            float d2 = Vector3.DistanceSquared(at, site.Position);
            float score = MathF.Max(2 * (1 - d2 / (9000f * 9000f)), 0.0001f) / (Workers(table, site.Id, slot) + 1);
            if (best is null || score > bestScore)
            {
                best = site;
                bestScore = score;
            }
        }
        return best;
    }

    /// <summary>The characters other than <paramref name="except"/> working a site.</summary>
    static int Workers(CharacterTable table, string site, int except)
    {
        int n = 0;
        for (int i = 0; i < table.HighWater; i++)
            if (i != except && table.Next[i].Alive && IsWork(table.Next[i].Task) && table.Cold(i)!.BuildSite == site) n++;
        return n;
    }
}
