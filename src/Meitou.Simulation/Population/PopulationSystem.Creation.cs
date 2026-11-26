using System.Numerics;
using Meitou.Data.Fcs;
using Meitou.Data.Gameplay;
using Meitou.Data.Gameplay.Construction;
using Meitou.Data.World;
using Meitou.Simulation.Factions;

namespace Meitou.Simulation.Population;

public sealed partial class PopulationSystem
{
    /// <summary>
    /// A future squad's template and contiguous slice of the flat creation roster.
    /// </summary>
    internal sealed record PreparedGroup(SquadTemplate Template, int Offset, int Count);

    /// <summary>
    /// Opaque server-generated recipe. It is not an authorization, a created role or a durable result.
    /// </summary>
    public sealed class PreparedPlayerSquad
    {
        internal PreparedPlayerSquad(PopulationSystem producer, World world, BuiltSquad built, IReadOnlyList<PreparedGroup> groups,
            TownSite? town, ulong seed, string factionIdentity, int characterLimit)
            => (Producer, World, Built, Groups, Town, Seed, FactionIdentity, CharacterLimit) =
                (producer, world, built, groups, town, seed, factionIdentity, characterLimit);
        internal PopulationSystem Producer { get; }
        internal World World { get; }
        internal BuiltSquad Built { get; }
        /// <summary>
        /// Squad templates and slices; customization changes members without changing their grouping or seed identities.
        /// </summary>
        internal IReadOnlyList<PreparedGroup> Groups { get; }
        /// <summary>
        /// The total headcount permitted by the approval used to prepare this recipe.
        /// </summary>
        internal int CharacterLimit { get; }
        internal TownSite? Town { get; }
        internal ulong Seed { get; }
        internal string FactionIdentity { get; }
        internal bool Used;
        internal bool NewFaction;
        public int CharacterCount => Built.Members.Count;
        public int Faction => Built.Faction;
        public Vector2 Centre => Built.Position;
    }

    /// <summary>
    /// Trusted creation primitive: prepare content/appearance without publishing characters or changing faction resources.
    /// </summary>
    public PreparedPlayerSquad PreparePlayerSquad(World world, NewGameStart start, int faction, ulong creationSeed, int characterLimit, bool newFaction = false)
    {
        CreationBoundary(world);
        ArgumentNullException.ThrowIfNull(start);
        if (faction < 0 || faction >= _data.Factions.Count) throw new ArgumentOutOfRangeException(nameof(faction));
        ArgumentOutOfRangeException.ThrowIfLessThan(characterLimit, 1);
        RefreshStates(world);
        var (centre, town, missing) = StartPlace(_data, creationSeed, start, faction);
        if (missing) throw new InvalidOperationException("The selected start's towns are unavailable.");
        ValidPoint(centre);
        ulong key = Rng.Mix(SeedSalts.PlayerStart ^ Rng.StableHash(start.Id));
        Vector2 at = centre;
        if (!world.Walkability.IsWalkable(at.X, at.Y))
            for (int attempt = 0; attempt < 20; attempt++)
            {
                var candidate = Rng.PointInDisc(Rng.Hash(creationSeed, key, RngPurpose.Spawn, (ulong)attempt), centre, 50 * (1 + attempt));
                if (WorldLayout.ZoneOf(candidate.X, candidate.Y).IsInsideGrid && world.Walkability.IsWalkable(candidate.X, candidate.Y)) { at = candidate; break; }
            }
        if (!world.Walkability.IsWalkable(at.X, at.Y)) throw new InvalidOperationException("The arrival region is not ready or has no walkable spawn point.");
        var (built, groups) = BuildPlayerStart(start, faction, creationSeed, at, characterLimit);
        if (built.Members.Count == 0) throw new InvalidOperationException("The selected start creates no characters.");
        foreach (var member in built.Members)
            if (_data.Db.Find(member.Plan.RecordId) is not { Type: FcsRecordType.Character or FcsRecordType.AnimalCharacter } || _assembly.RaceOf(member.Plan.RecordId, member.Appearance) is null)
                throw new InvalidDataException("A creation member has no valid character/race definition.");
        var prepared = new PreparedPlayerSquad(this, world, built, groups, town, creationSeed, _data.Factions[faction].Id, characterLimit)
        { NewFaction = newFaction };
        ValidatePrepared(world, prepared);
        return prepared;
    }

    /// <summary>
    /// Materializes every squad of the prepared roster and returns its first squad for callers that need a leading squad.
    /// </summary>
    public Squad MaterializePlayerSquad(World world, PreparedPlayerSquad prepared) => MaterializePlayerSquads(world, prepared)[0];

    /// <summary>
    /// Materializes a server-prepared roster once, rolling back every new squad and character if assembly fails. No start resources, control
    /// grants or receipt are issued; the transaction revalidates qualifications, grants every role and persists before acknowledging success.
    /// </summary>
    public IReadOnlyList<Squad> MaterializePlayerSquads(World world, PreparedPlayerSquad prepared)
    {
        CreationBoundary(world);
        ArgumentNullException.ThrowIfNull(prepared);
        if (!ReferenceEquals(prepared.Producer, this) || !ReferenceEquals(prepared.World, world) || prepared.Used || prepared.NewFaction)
            throw new InvalidOperationException("Creation recipe belongs to another world/producer or was already attempted.");
        ValidatePrepared(world, prepared);
        prepared.Used = true;
        var spawned = new List<CharacterId>(prepared.CharacterCount);
        var squads = new List<Squad>(prepared.Groups.Count);
        try
        {
            var existing = SquadManagement.PlayerSquads(world, prepared.Faction);
            int index = existing.Count == 0 ? 0 : checked(existing.Max(s => s.Index) + 1);
            string name = world.Factions.Find(prepared.Faction)?.Name ?? _data.Factions[prepared.Faction].Name;
            float radius = prepared.Town is null ? 100 : prepared.Town.Town.SizeRadius * prepared.Town.Town.TownRadiusMult;
            foreach (var group in prepared.Groups)
            {
                var built = PreparedGroupOf(prepared, group);
                var squad = SpawnSquad(world, built, null, radius, player: true, creationSeed: prepared.Seed,
                    spawned: spawned.Add, bindUniques: false);
                squads.Add(squad);
                squad.Index = checked(index + squads.Count - 1);
                squad.Name = $"{name}_{squad.Index}";
                SetStartSlaves(world, squad, prepared.Town);
            }
        }
        catch
        {
            foreach (var id in spawned) world.Characters.Remove(id);
            foreach (var squad in squads) world.Squads.Remove(squad.Id);
            throw;
        }

        // Online preparation rejects unavailable uniques; bind only after every squad's assembly has succeeded.
        foreach (var id in spawned)
            if (world.Characters.Cold(id.Slot) is { } cold && UniqueCharacters.IsUnique(_data.Db.Find(cold.RecordId)))
                world.Uniques.Bind(cold.RecordId, id);
        return squads;
    }

    /// <summary>
    /// Takes a squad's customized members without renumbering their start-wide seed identities.
    /// </summary>
    static BuiltSquad PreparedGroupOf(PreparedPlayerSquad prepared, PreparedGroup group)
    {
        var members = prepared.Built.Members.GetRange(group.Offset, group.Count);
        var plan = new SquadPlan(group.Template, members.Select(m => m.Plan).ToArray(), [], []);
        var templateMembers = prepared.Built.TemplateMembers;
        return prepared.Built with
        {
            Plan = plan,
            Members = members,
            TemplateMembers = templateMembers is null ? null : members.Where(m => templateMembers.Contains(m.Plan.Index)).Select(m => m.Plan.Index).ToHashSet()
        };
    }

    /// <summary>
    /// Initialize only a newly registered runtime faction, before memberships or rosters use it.
    /// </summary>
    public void InitializePlayerFaction(World world, int faction, NewGameStart start)
    {
        CreationBoundary(world);
        ArgumentNullException.ThrowIfNull(start);
        if (faction < _data.Relations.ContentCount || faction >= _data.Factions.Count || world.Factions.Find(faction) is not null ||
            world.Squads.All.Any(s => s.Faction == faction) || world.Players.All.Any(p => p.IsMember(faction)))
            throw new InvalidOperationException("Start resources require an unused runtime faction.");
        if (_data.Relations.PlayerFaction != -1) throw new InvalidOperationException("Multiplayer factions require directed diplomacy.");
        var research = ResearchGate.StartResearch(_data.Db, start).ToArray();
        if (research.Any(id => _data.Db.Find(id) is not { Type: FcsRecordType.Research })) throw new InvalidDataException("Start research content is missing.");
        var relations = start.FactionRelations.Select(link => (Other: _data.FactionIndex(link.Id), link.V0)).ToArray();
        if (relations.Any(r => r.Other < 0)) throw new InvalidDataException("Start diplomacy content is missing.");
        var resources = world.Factions.GetOrCreate(faction);
        resources.Money = start.Money;
        resources.Research.Set(_data.Db, research);
        foreach (var (other, value) in relations)
        {
            _data.Relations.Add(other, faction, value - _data.Relations.Get(other, faction));
            _data.Relations.Add(faction, other, value - _data.Relations.Get(faction, other));
        }
    }

    internal PreparedPlayerSquad RetargetNewFaction(World world, PreparedPlayerSquad prepared, int faction)
    {
        CreationBoundary(world);
        if (!ReferenceEquals(prepared.World, world) || !ReferenceEquals(prepared.Producer, this) || !prepared.NewFaction || prepared.Used ||
            faction < _data.Relations.ContentCount || faction >= _data.Factions.Count || _data.FactionTemplate(faction) != _data.FactionTemplate(prepared.Faction))
            throw new InvalidOperationException("Invalid new-faction recipe target.");
        var result = new PreparedPlayerSquad(this, world, prepared.Built with { Faction = faction }, prepared.Groups,
            prepared.Town, prepared.Seed, _data.Factions[faction].Id, prepared.CharacterLimit);
        ValidatePrepared(world, result); prepared.Used = true;
        return result;
    }

    /// <summary>
    /// Owner-boundary fence before changing the faction catalogue. Queued results remain staged for the next normal input boundary.
    /// </summary>
    internal void FenceCreationBuilds(World world)
    {
        CreationBoundary(world);
        if (_work is null) return;
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _work.Add(() => done.TrySetResult());
        if (!done.Task.Wait(TimeSpan.FromSeconds(30))) throw new TimeoutException("Population preparation has not reached the creation boundary.");
    }

    void ValidatePrepared(World world, PreparedPlayerSquad prepared)
    {
        int faction = prepared.Faction;
        if (faction < 0 || faction >= _data.Factions.Count || _data.Factions[faction].Id != prepared.FactionIdentity)
            throw new InvalidOperationException("Creation faction changed.");
        ValidatePreparedCapacity(world, prepared);
        var uniques = new HashSet<string>(StringComparer.Ordinal);
        foreach (var member in prepared.Built.Members)
        {
            if (_data.Db.Find(member.Plan.RecordId) is not { Type: FcsRecordType.Character or FcsRecordType.AnimalCharacter } || _assembly.RaceOf(member.Plan.RecordId, member.Appearance) is null)
                throw new InvalidDataException("Creation character content changed.");
            if (UniqueCharacters.IsUnique(_data.Db.Find(member.Plan.RecordId)) &&
                (!uniques.Add(member.Plan.RecordId) || world.Uniques.IsClaimed(member.Plan.RecordId) || world.Uniques.StateOf(member.Plan.RecordId) != UniqueState.Alive ||
                    Enumerable.Range(0, world.Characters.HighWater).Any(i => world.Characters.Current[i].Alive && world.Characters.Cold(i)?.RecordId == member.Plan.RecordId)))
                throw new InvalidOperationException("A unique creation character is already present or unavailable.");
            var point = prepared.Centre + member.Plan.Offset;
            ValidPoint(point);
            if (!world.Walkability.IsWalkable(point.X, point.Y)) point = prepared.Centre;
            if (!world.Walkability.IsWalkable(point.X, point.Y) || !float.IsFinite(world.Walkability.GroundHeight(point.X, point.Y)))
                throw new InvalidOperationException("The arrival region is no longer ready.");
        }
    }

    /// <summary>
    /// Applies the grant's total limit, each squad's limit and the faction's remaining headcount and squad slots before any member is made.
    /// </summary>
    void ValidatePreparedCapacity(World world, PreparedPlayerSquad prepared)
    {
        var constants = _data.Bodies;
        int made = 0;
        foreach (var group in prepared.Groups)
        {
            if (group.Offset != made || group.Count < 1 || group.Count > constants.MaxSquadSize)
                throw new InvalidOperationException("A creation squad exceeds its capacity or has an invalid roster slice.");
            made = checked(made + group.Count);
        }

        int heads = prepared.NewFaction ? 0 : SquadManagement.CappedHeads(world, prepared.Faction);
        int squads = prepared.NewFaction ? 0 : SquadManagement.LedSquads(world, prepared.Faction).Count;
        if (made != prepared.CharacterCount || made < 1 || made > prepared.CharacterLimit ||
            (long)heads + made > constants.MaxFactionSize || (long)squads + prepared.Groups.Count > constants.MaxSquads)
            throw new InvalidOperationException("The faction no longer has capacity for this roster.");
    }

    static void ValidPoint(Vector2 point)
    {
        if (!float.IsFinite(point.X) || !float.IsFinite(point.Y) || !WorldLayout.ZoneOf(point.X, point.Y).IsInsideGrid)
            throw new InvalidDataException("Invalid creation spawn position.");
    }
    static void CreationBoundary(World world)
    {
        if (!world.AtTickBoundary || !world.Players.RequiresExplicitControl)
            throw new InvalidOperationException("Multiplayer creation requires an explicit-authority world boundary.");
    }
}