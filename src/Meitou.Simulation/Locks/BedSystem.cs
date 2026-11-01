using System.Numerics;
using Meitou.Data.Gameplay;
using Meitou.Data.Gameplay.Ai;

namespace Meitou.Simulation.Locks;

/// <summary>
/// The original's order 258 <c>USE_BED_ORDER</c> (verb "Sleep"; a quick right click on a bed, docs/game/ui-input.md "Building hover and click"): one of the characters (the selection when
/// the list is empty), the nearest that is awake and free, walks to <see cref="Bed"/> (a bed fixture id) and lies in it until another order.
/// </summary>
public sealed record UseBedOrder(IReadOnlyList<CharacterId> Characters, string Bed) : FixtureCommand;

/// <summary>
/// The original's order 99 <c>PUT_SOMEONE_IN_BED</c> (verb "Put in Bed"): each of the carriers (the selected ones carrying someone when the list is empty) carries its body to
/// <see cref="Bed"/>, or with none to the bed the bed search finds for it, and lays it in.
/// </summary>
public sealed record PutInBedOrder(IReadOnlyList<CharacterId> Carriers, string? Bed = null) : FixtureCommand;

/// <summary>A walk to a bed (<see cref="BedSystem"/>): <see cref="Character"/> to lie in <see cref="Bed"/> itself, or to lay <see cref="Body"/> it carries in it.</summary>
public sealed class BedTrip
{
    public required CharacterId Character { get; init; }
    internal SimCommand? Request { get; init; }
    internal CharacterControl? Control { get; init; }
    internal ulong Membership { get; init; }
    internal Fixture? Target { get; set; }
    internal int Owner { get; set; }
    internal ulong FixtureRevision { get; set; }
    internal CharacterCold? BodyCold { get; init; }
    internal int BodyFaction { get; init; }
    public required string Bed { get; set; }
    /// <summary>The carried character to lay in the bed; <see cref="CharacterId.None"/>: the character lies in it itself.</summary>
    public CharacterId Body { get; init; } = CharacterId.None;
    /// <summary>The bed was given (an order on that bed): no other is looked for when it is taken.</summary>
    public bool Fixed { get; init; }
    /// <summary>Walks that ended short of the bed.</summary>
    public int Tries { get; set; }

    internal void Hash(ref StateHasher h)
    {
        h.Add(Character); h.Add(Rng.StableHash(Bed)); h.Add(Body); h.Add(Fixed); h.Add(Tries);
        if (Control is { } grant) { h.Add(grant.Player.Value); h.Add(grant.Entity.Value); h.Add(grant.Revision); h.Add(Membership); h.Add(Owner); h.Add(BodyFaction); }
    }
}

/// <summary>The numbers of <see cref="BedSystem"/>; engine choices unless marked.</summary>
public sealed class BedSettings
{
    /// <summary>A walker gets in (or lays its body in) within the bed's <c>use range</c> (15, <b>Verified</b> data) plus this, on the bed's floor.</summary>
    public float Reach { get; init; } = 15;
    /// <summary>A walk that ended (blocked by the bed's furniture, the path's end snapped beside it) still counts within this.</summary>
    public float Slack { get; init; } = 60;
    /// <summary>A bed counts as on the walker's floor within this height.</summary>
    public float FloorSlack { get; init; } = 20;
    /// <summary>Walks that end short before the bed is given up (and marked unreachable for the walker's faction).</summary>
    public int Walks { get; init; } = 3;
    /// <summary>Ticks between the looks of the "Find and put in bed" job (106) for a carried friend to lay in a bed.</summary>
    public int JobEveryTicks { get; init; } = 15;
    /// <summary>A rented bed paid for this many game hours ago is free again for the payer's faction (<b>Observed</b>, <c>FUN_1403576c0</c>: 24).</summary>
    public double RentHours { get; init; } = 24;
}

/// <summary>
/// Beds in play (docs/simulation.md "Cages, beds and locks"; the original's rules in docs/game/locks.md "Beds"). Serial phases only.
/// <list type="bullet">
/// <item><b>Sleep</b> (<see cref="UseBedOrder"/>, and the "Sleep when injured" option through <see cref="StartSleep"/>): the walker's task is <see cref="CharacterTask.Sleep"/>; in reach it pays
/// the bed's rent (<see cref="Pay"/>) and lies in it (<see cref="FixtureRegistry.EnterBed"/>). It lies there until its task changes (any order), then gets up
/// (<see cref="FixtureRegistry.LeaveBed"/>), as the original's 258 never completes and an order replaces it.</item>
/// <item><b>Put in bed</b> (<see cref="PutInBedOrder"/>, and the job 106 "Find and put in bed" for a carried friend): the carrier walks there, pays (the cheaper of its own and the body's
/// rent, <c>FUN_140358170</c>), lets go and the body lies in the bed (<c>FUN_140358670</c>).</item>
/// <item><b>Rent</b> (<see cref="Cost"/>): only the player's characters pay; refused for a bed that is not theirs to use.</item>
/// </list>
/// The player's own beds become bed fixtures when their buildings register (<see cref="FixtureRegistry.RegisterPlayerBuilding"/>, by the lock system).
/// </summary>
public sealed class BedSystem(PopulationData data, BedSettings? settings = null) : ITickSystem, IStateHashed
{
    public BedSettings Settings { get; } = settings ?? new BedSettings();
    /// <summary>Lets a carrier put its body down (null: no carrying, so no laying in).</summary>
    public Carrying.CarrySystem? Carry { get; set; }

    readonly List<BedTrip> trips = [];
    // Bed id -> the game hour the player's faction last paid its rent (the original's global map is keyed by a name string of the argument and is never saved, Verified by references;
    // Meitou saves it as `meitou rent` keys on the CAMERA, engine choice).
    readonly SortedDictionary<string, double> paid = new(StringComparer.Ordinal);

    public IReadOnlyList<BedTrip> Trips => trips;

    /// <summary>The beds whose rent was paid, with the game hour of the payment, in id order (a save keeps them: <see cref="Saving.BedSave"/>).</summary>
    public IReadOnlyCollection<KeyValuePair<string, double>> RentPaid => paid;

    /// <summary>Sets the game hour a bed's rent was last paid (a loaded save does this).</summary>
    public void SetRentPaid(string bed, double hours) => paid[bed] = hours;
    public static string RentKey(int faction, string bed) => $"faction:{faction}:{bed}";

    static IReadOnlyList<CharacterId> Who(World world, SimCommand command, IReadOnlyList<CharacterId> ids) => world.CommandCharacters(command, ids).Where(id =>
        command.Issuer is not { } issuer || world.Characters.TryResolveNext(id, out int slot)
        && command.FactionGrantRevision != 0 && world.Players.Find(issuer)?.MembershipRevision(world.Characters.Cold(slot)!.Faction) == command.FactionGrantRevision).ToArray();

    public void Inputs(World world, IReadOnlyList<SimCommand> commands)
    {
        var table = world.Characters;
        foreach (var command in commands)
        {
            switch (command)
            {
                case UseBedOrder use:
                {
                    if (world.Fixtures.Find(use.Bed) is not { Kind: FixtureKind.Bed } bed || !world.Fixtures.Matches(use, bed)) break;
                    int best = -1;
                    float bestD = float.MaxValue;
                    foreach (var id in Who(world, use, use.Characters))
                    {
                        if (!table.TryResolveNext(id, out int s) || table.Cold(s) is not { } c || !CanWalk(c) || !c.Carrying.IsNone) continue;
                        if (c.InSomething == HeldState.Bed && c.InWhat == bed.Id) { best = -1; break; }   // one of them lies in it already
                        float d = Vector3.DistanceSquared(table.Next[s].Position, bed.Position);
                        if (d < bestD) (best, bestD) = (s, d);
                    }
                    if (best >= 0) StartSleep(world, best, bed, fixedBed: true, request: use);
                    break;
                }
                case PutInBedOrder put:
                    foreach (var id in Who(world, put, put.Carriers))
                    {
                        if (!table.TryResolveNext(id, out int s) || table.Cold(s) is not { Carrying.IsNone: false } c || !CanWalk(c)) continue;
                        var bed = put.Bed is { } given ? world.Fixtures.Find(given) : world.Fixtures.FindBed(world, s, data.Relations, f => Reserved(f, id));
                        if (bed is not { Kind: FixtureKind.Bed }) { if (c.IsPlayer) world.Post($"{c.Name}: no free bed nearby"); continue; }
                        if (put.Bed is not null && !world.Fixtures.Matches(put, bed)) continue;
                        StartPutIn(world, s, bed, fixedBed: put.Bed is not null, request: put);
                    }
                    break;
            }
        }
    }

    /// <summary>A character that may walk to a bed: awake, not carried, not caged, not fleeing.</summary>
    static bool CanWalk(CharacterCold c) => c is { Medical.Incapacitated: false, IsImprisoned: false, Fleeing: false } && c.CarriedBy.IsNone;

    /// <summary>The bed search for the character in <paramref name="slot"/> (<see cref="FixtureRegistry.FindBed"/>), leaving out the beds others are on their way to.</summary>
    public Fixture? FindFor(World world, int slot)
    {
        var id = world.Characters.IdOf(slot);
        return world.Fixtures.FindBed(world, slot, data.Relations, f => Reserved(f, id));
    }

    /// <summary>Whether another character than <paramref name="except"/> is on its way to <paramref name="bed"/>.</summary>
    public bool Reserved(Fixture bed, CharacterId except)
    {
        foreach (var t in trips) if (t.Bed == bed.Id && t.Character != except) return true;
        return false;
    }

    /// <summary>
    /// Sends a character to lie in <paramref name="bed"/> (task <see cref="CharacterTask.Sleep"/>): out of another bed first; in reach it gets in at once. <paramref name="fixedBed"/>: the
    /// player named the bed, so no other is looked for. The "Sleep when injured" job starts it too.
    /// </summary>
    public void StartSleep(World world, int slot, Fixture bed, bool fixedBed = false, SimCommand? request = null)
    {
        var table = world.Characters;
        var id = table.IdOf(slot);
        var c = table.Cold(slot)!;
        trips.RemoveAll(t => t.Character == id);
        if (world.Players.RequiresExplicitControl && c.IsPlayer && world.Players.ControlOf(id) is null) return;
        if (c.InSomething == HeldState.Bed && c.InWhat != bed.Id) world.Fixtures.LeaveBed(world, id);
        if (c.InSomething == HeldState.Bed) { table.Next[slot].Task = (byte)CharacterTask.Sleep; return; }
        var trip = Trip(world, id, bed, CharacterId.None, fixedBed, request);
        trips.Add(trip);
        WalkTo(world, slot, bed);
        table.Next[slot].Task = (byte)CharacterTask.Sleep;
        if (!Step(world, trip)) trips.Remove(trip);
    }

    /// <summary>Sends a carrier to lay its body in <paramref name="bed"/>.</summary>
    public void StartPutIn(World world, int slot, Fixture bed, bool fixedBed = false, SimCommand? request = null)
    {
        var table = world.Characters;
        var id = table.IdOf(slot);
        var c = table.Cold(slot)!;
        trips.RemoveAll(t => t.Character == id);
        if (world.Players.RequiresExplicitControl && c.IsPlayer && world.Players.ControlOf(id) is null) return;
        var trip = Trip(world, id, bed, c.Carrying, fixedBed, request);
        trips.Add(trip);
        WalkTo(world, slot, bed);
        if (!Step(world, trip)) trips.Remove(trip);
    }

    /// <summary>A walk onto the bed's own floor (furniture cuts a hole in the floor's navmesh; a goal without a height resolves to the storey above or the ground below).</summary>
    void WalkTo(World world, int slot, Fixture bed)
    {
        var table = world.Characters;
        var c = table.Cold(slot)!;
        var at = world.Walkability.FloorNear(bed.Position.X, bed.Position.Z, bed.Position.Y, Settings.FloorSlack) is { } onFloor ? onFloor : bed.Position;
        var flat = new Vector2(at.X, at.Z);
        MovementSystem.GoTo(table, slot, flat, c.RunSpeed);
        (c.GoalHeight, c.GoalHeightAt) = (at.Y, flat);
    }

    public void SlowWorld(World world)
    {
        GetUp(world);
        for (int k = 0; k < trips.Count;)
        {
            if (Step(world, trips[k])) k++;
            else trips.RemoveAt(k);
        }
        if (Carry is not null && world.Tick % Math.Max(1, Settings.JobEveryTicks) == 0) PutInJobs(world);
    }

    /// <summary>A character lying in a bed whose task is no longer <see cref="CharacterTask.Sleep"/> (an order replaced it) gets up.</summary>
    void GetUp(World world)
    {
        var table = world.Characters;
        for (int i = 0; i < table.HighWater; i++)
            if (table.Next[i].Alive && table.Next[i].Task != (byte)CharacterTask.Sleep && table.Cold(i) is { InSomething: HeldState.Bed })
                world.Fixtures.LeaveBed(world, table.IdOf(i));
    }

    /// <summary>One tick of a trip; false when it is over (in bed, laid in, refused, given up, or another order).</summary>
    BedTrip Trip(World world, CharacterId actor, Fixture bed, CharacterId body, bool fixedBed, SimCommand? request)
    {
        var grant = world.Players.ControlOf(actor);
        return new BedTrip { Character = actor, Bed = bed.Id, Body = body, Fixed = fixedBed, Request = request,
            Control = grant, Membership = grant is { } g ? world.Players.Find(g.Player)?.MembershipRevision(g.Faction) ?? 0 : 0,
            Target = bed, Owner = bed.Owner, FixtureRevision = world.Fixtures.RevisionOf(bed),
            BodyCold = world.Characters.TryResolveNext(body, out int bodySlot) ? world.Characters.Cold(bodySlot) : null,
            BodyFaction = world.Characters.TryResolveNext(body, out int factionSlot) ? world.Characters.Cold(factionSlot)!.Faction : -1 };
    }

    bool Step(World world, BedTrip t)
    {
        var table = world.Characters;
        if (!table.TryResolveNext(t.Character, out int s) || table.Cold(s) is not { } c || !CanWalk(c)) return false;
        if (t.Request is { } request && !world.MayOrder(request, t.Character)) return false;
        if (world.Players.RequiresExplicitControl && c.IsPlayer && (t.Control is not { } control || !world.Players.Validate(control)
            || t.Membership == 0 || world.Players.Find(control.Player)?.MembershipRevision(c.Faction) != t.Membership)) return false;
        if (t.Target is { } target && (!ReferenceEquals(world.Fixtures.Find(t.Bed), target) || target.Owner != t.Owner || world.Fixtures.RevisionOf(target) != t.FixtureRevision)) return false;
        if (t.BodyCold is { } bodyCold && (!table.TryResolveNext(t.Body, out int bodySlot) || !ReferenceEquals(table.Cold(bodySlot), bodyCold) || bodyCold.Faction != t.BodyFaction)) return false;
        bool carrying = !t.Body.IsNone;
        if (carrying ? c.Carrying != t.Body : c.InSomething != HeldState.None) return false;
        ref var n = ref table.Next[s];
        var bed = world.Fixtures.Find(t.Bed);
        if (bed is null || !FixtureRegistry.IsFree(table, bed))
        {
            if (t.Fixed || world.Fixtures.FindBed(world, s, data.Relations, f => Reserved(f, t.Character)) is not { } other)
            {
                if (c.IsPlayer) world.Post(carrying ? $"{c.Name}: no free bed nearby" : $"{c.Name}: the bed is taken");
                if (!carrying) MovementSystem.Stop(table, s);
                return false;
            }
            t.Bed = other.Id; t.Target = other; t.Owner = other.Owner; t.FixtureRevision = world.Fixtures.RevisionOf(other);
            WalkTo(world, s, other);
            if (!carrying) n.Task = (byte)CharacterTask.Sleep;
            return true;
        }
        var goal = new Vector2(bed.Position.X, bed.Position.Z);
        float d = Vector2.Distance(new Vector2(n.Position.X, n.Position.Z), goal);
        bool walking = ((MoveFlags)n.Flags & MoveFlags.AnyPath) != 0;
        bool onTask = carrying ? n.Task == (byte)CharacterTask.GoTo || !walking && n.Task == (byte)CharacterTask.Idle : n.Task == (byte)CharacterTask.Sleep;
        if (!onTask || walking && Vector2.Distance(n.Goal, goal) > Settings.Slack) return false;   // another order
        bool floor = MathF.Abs(n.Position.Y - bed.Position.Y) <= Settings.FloorSlack;
        if (floor && (d <= bed.UseRange + Settings.Reach || !walking && d <= Settings.Slack))
        {
            if (walking) MovementSystem.Stop(table, s);
            return carrying ? LayIn(world, s, c, t.Body, bed) : LieIn(world, s, c, bed);
        }
        if (!walking)
        {
            if (++t.Tries > Settings.Walks)
            {
                world.Fixtures.MarkUnreachable(world, bed, c.Faction);
                if (c.IsPlayer) world.Post($"{c.Name} cannot get to the bed");
                if (!carrying) MovementSystem.Stop(table, s);
                return false;
            }
            WalkTo(world, s, bed);
            if (!carrying) n.Task = (byte)CharacterTask.Sleep;
        }
        return true;
    }

    /// <summary>The walker at its bed: the rent, then in (the original's <c>Task_UseBed</c> start <c>FUN_140357d00</c>). False: the trip is over either way.</summary>
    bool LieIn(World world, int s, CharacterCold c, Fixture bed)
    {
        var table = world.Characters;
        if (!Pay(world, s, bed, Cost(world, bed, c.Faction)) || !world.Fixtures.EnterBed(world, table.IdOf(s), bed)) MovementSystem.Stop(table, s);
        return false;
    }

    /// <summary>
    /// The carrier at the bed: the rent (the cheaper of the carrier's and the body's, <c>FUN_140358170</c>), then the body is let go and lies in the bed (<c>FUN_140358670</c>). False: over.
    /// </summary>
    bool LayIn(World world, int s, CharacterCold c, CharacterId body, Fixture bed)
    {
        var table = world.Characters;
        if (!table.TryResolveNext(body, out int b) || table.Cold(b) is not { } bc) return false;
        int cost = Cost(world, bed, c.Faction);
        if (cost != 0 && Cost(world, bed, bc.Faction) is var own and >= 0 && own < cost) cost = own;
        if (!Pay(world, s, bed, cost)) return false;
        Carry!.LetGo(world, s);
        if (!world.Fixtures.EnterBed(world, body, bed) && c.IsPlayer) world.Post($"{c.Name} could not put {bc.Name} in the bed");
        return false;
    }

    // The editor's BuildingDesignation BD_BAR. The original tests the house's own designation, which the first squad to take it as home sets from its template.
    const int BarDesignation = 3;

    /// <summary>
    /// What a character of <paramref name="faction"/> pays to use a bed (the original's <c>FUN_140330ab0</c>, <b>Observed</b>): 0 for a bed of no faction, of a faction that counts it as an
    /// ally (its own included) or of a <c>fundamental type</c> 8 (bandit) faction, and for a bed standing on its own; else the bed's house must be public for beds (its resident squad's
    /// <c>public beds</c>, or a bar: its <c>building designation</c> is BD_BAR), or -1 (not to be used); then the squad's <c>bed usage cost</c>, x4 for a skeleton bed. The town-type and
    /// two further tests of the original are not modelled.
    /// </summary>
    public int Cost(World world, Fixture bed, int faction)
    {
        int owner = bed.Owner;
        if (owner < 0 || owner == faction || !bed.Indoors) return 0;
        if (data.Relations.Contains(owner) && data.Relations.Contains(faction) && data.Relations.IsAlly(owner, faction)) return 0;
        if (owner < data.Factions.Count && data.Factions[owner].FundamentalType == FundamentalType.Bandit) return 0;
        Squad? residents = null;
        foreach (var squad in world.Squads.All)
            if ((squad.Ai?.OwnHome ?? squad.HomeBuilding)?.Key == bed.Building) { residents = squad; break; }
        if (residents is null || data.Db.Find(residents.TemplateId) is not { } template || !template.GetBool("public beds") && template.GetInt("building designation") != BarDesignation) return -1;
        int cost = template.GetInt("bed usage cost");
        if (bed.Bed.Function == Meitou.Data.World.TownBuildings.SkeletonBedFunction) cost *= 4;
        return Math.Max(cost, 0);
    }

    internal BuildingDetail? DetailOf(World world, string? instance, int? faction = null)
    {
        // Town beds only: a bed the player built is a machine site (Site -1) and keeps the machine's panel.
        if (instance is null || world.Fixtures.Find(instance) is not { Kind: FixtureKind.Bed, Site: >= 0 } bed || data.Db.Find(bed.RecordId) is not { } record) return null;
        string? town = bed.Site >= 0 && bed.Site < data.Sites.Count ? data.Sites[bed.Site].Town.Name : null;
        float healing = Meitou.Data.Gameplay.Construction.MachineData.From(data.Db, record)?.OutputValue ?? 1;
        int viewerFaction = faction ?? world.Player.Faction;
        return new BuildingDetail(instance, [], bed.Owner == viewerFaction)
        {
            Bed = new(record.Name, town, Cost(world, bed, viewerFaction), healing),
        };
    }

    /// <summary>
    /// The rent paid by the character in <paramref name="slot"/> (the original's <c>FUN_1403576c0</c>, <b>Observed</b>); only the player's characters pay. -1: "This isn't mine", refused.
    /// Above 0: free when the player's faction paid for this bed less than <see cref="BedSettings.RentHours"/> ago; else "Can't afford c.{1} for bed rental." and refused, or the money
    /// is paid with "Paid c.{1} for bed rental.". True when the bed may be used.
    /// </summary>
    public bool Pay(World world, int slot, Fixture bed, int cost)
    {
        var table = world.Characters;
        if (table.Cold(slot) is not { IsPlayer: true } || cost == 0) return true;
        if (cost < 0)
        {
            world.Bark(table.IdOf(slot), Loc.Tr("This isn't mine"));
            return false;
        }
        string key = world.Players.RequiresExplicitControl ? RentKey(table.Cold(slot)!.Faction, bed.Id) : bed.Id;
        if (paid.TryGetValue(key, out double when) && world.GameHours - when < Settings.RentHours) return true;
        var cold = table.Cold(slot)!;
        if (world.Players.RequiresExplicitControl && (world.Players.ControlOf(table.IdOf(slot)) is not { } grant || world.Players.Find(grant.Player)?.HasPermission(cold.Faction, FactionPermission.SpendFunds) != true)) return false;
        if (world.Factions.Find(cold.Faction) is not { } funds || !funds.TryDebit(cost))
        {
            world.Post(Loc.Tr("Can't afford c.{1} for bed rental.", cost));
            return false;
        }
        paid[key] = world.GameHours;
        world.Post(Loc.Tr("Paid c.{1} for bed rental.", cost));
        return true;
    }

    /// <summary>
    /// The job 106 "Find and put in bed" (<c>FUN_1405a0d50</c>, docs/game/carry-loot.md 3.2): a player character with it in its list and the jobs switch on, idle, carrying a body whose
    /// faction is not hostile to its own and that is no animal, lays it in the bed the bed search finds (<b>engine choice</b>: the original's 106 has no target finder of its own).
    /// </summary>
    void PutInJobs(World world)
    {
        var table = world.Characters;
        for (int i = 0; i < table.HighWater; i++)
        {
            if (!table.Next[i].Alive || table.Next[i].Task is not ((byte)CharacterTask.Idle or (byte)CharacterTask.Wander or (byte)CharacterTask.Follow)) continue;
            if (table.Cold(i) is not { IsPlayer: true, Carrying.IsNone: false } c || !CanWalk(c) || (c.Orders & CharacterOrders.Jobs) == 0) continue;
            if (!c.Jobs.Exists(j => j.Type == TaskType.FIND_BED_AND_PUT_IN) || trips.Exists(t => t.Character == table.IdOf(i))) continue;
            if (!table.TryResolveNext(c.Carrying, out int b) || table.Cold(b) is not { } bc || bc.Fighter is { IsAnimal: true }) continue;
            if (data.Relations.Contains(bc.Faction) && data.Relations.Contains(c.Faction) && data.Relations.IsHostile(bc.Faction, c.Faction)) continue;
            if (world.Fixtures.FindBed(world, i, data.Relations, f => Reserved(f, table.IdOf(i))) is { } bed) StartPutIn(world, i, bed);
        }
    }

    public void Hash(ref StateHasher hasher)
    {
        if (trips.Count == 0 && paid.Count == 0) return;
        hasher.Add(0x42656473);
        hasher.Add(trips.Count);
        foreach (var t in trips) t.Hash(ref hasher);
        hasher.Add(paid.Count);
        foreach (var (bed, when) in paid) { hasher.Add(Rng.StableHash(bed)); hasher.Add(when); }
    }
}
