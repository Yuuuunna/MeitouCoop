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
    /// Opaque server-generated recipe. It is not an authorization, a created role or a durable result.
    /// </summary>
    public sealed class PreparedPlayerSquad
    {
        internal PreparedPlayerSquad(PopulationSystem producer, World world, BuiltSquad built, TownSite? town, ulong seed, string factionIdentity)
            => (Producer, World, Built, Town, Seed, FactionIdentity) = (producer, world, built, town, seed, factionIdentity);
        internal PopulationSystem Producer { get; }
        internal World World { get; }
        internal BuiltSquad Built { get; }
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
        var built = BuildPlayerStart(world, start, faction, creationSeed, at, strict: true,
            limit: Math.Min(characterLimit, _data.Bodies.Constants.MaxSquadSize));
        if (built.Members.Count == 0) throw new InvalidOperationException("The selected start creates no characters.");
        foreach (var member in built.Members)
            if (_data.Db.Find(member.Plan.RecordId) is not { Type: FcsRecordType.Character or FcsRecordType.AnimalCharacter } || _assembly.RaceOf(member.Plan.RecordId, member.Appearance) is null)
                throw new InvalidDataException("A creation member has no valid character/race definition.");
        var prepared = new PreparedPlayerSquad(this, world, built, town, creationSeed, _data.Factions[faction].Id) { NewFaction = newFaction };
        ValidatePrepared(world, prepared);
        return prepared;
    }

    /// <summary>
    /// Materializes one server-prepared roster once. No start money, research, diplomacy reset, control grants or success receipt.
    /// The creation transaction must revalidate qualifications, assign roles and persist its result before acknowledging success.
    /// </summary>
    public Squad MaterializePlayerSquad(World world, PreparedPlayerSquad prepared)
    {
        CreationBoundary(world);
        ArgumentNullException.ThrowIfNull(prepared);
        if (!ReferenceEquals(prepared.Producer, this) || !ReferenceEquals(prepared.World, world) || prepared.Used || prepared.NewFaction)
            throw new InvalidOperationException("Creation recipe belongs to another world/producer or was already attempted.");
        ValidatePrepared(world, prepared);
        prepared.Used = true;
        var spawned = new List<CharacterId>(prepared.CharacterCount);
        Squad? squad = null;
        try
        {
            squad = SpawnSquad(world, prepared.Built, null, prepared.Town is null ? 100 : prepared.Town.Town.SizeRadius * prepared.Town.Town.TownRadiusMult,
                player: true, creationSeed: prepared.Seed, spawned: spawned.Add, bindUniques: false);
            var existing = SquadManagement.PlayerSquads(world, prepared.Faction).Where(s => s != squad).ToArray();
            squad.Index = existing.Length == 0 ? 0 : checked(existing.Max(s => s.Index) + 1);
            squad.Name = $"{world.Factions.Find(prepared.Faction)?.Name ?? _data.Factions[prepared.Faction].Name}_{squad.Index}";
            int owner = prepared.Town is { } town && town.Index < _data.SiteFactions.Count ? _data.SiteFactions[town.Index] : -1;
            foreach (var id in spawned)
                if (world.Characters.Cold(id.Slot) is { } cold && _data.Db.Find(cold.RecordId)?.GetInt("slave") is (1 or 2) and var state && owner >= 0)
                    (cold.SlaveState, cold.SlaveOwner, cold.SlaveSince) = ((byte)state, owner, world.GameHours);
        }
        catch
        {
            foreach (var id in spawned) world.Characters.Remove(id);
            if (squad is not null) world.Squads.Remove(squad.Id);
            throw;
        }
        // Defer unique binding until all member _assembly has succeeded.
        foreach (var id in spawned)
            if (world.Characters.Cold(id.Slot) is { } cold && UniqueCharacters.IsUnique(_data.Db.Find(cold.RecordId))) world.Uniques.Bind(cold.RecordId, id);
        return squad;
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
        var result = new PreparedPlayerSquad(this, world, prepared.Built with { Faction = faction }, prepared.Town, prepared.Seed, _data.Factions[faction].Id);
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
        if (prepared.CharacterCount > _data.Bodies.Constants.MaxSquadSize ||
            (long)(prepared.NewFaction ? 0 : SquadManagement.FactionHeads(world, faction)) + prepared.CharacterCount > _data.Bodies.Constants.MaxFactionSize ||
            !prepared.NewFaction && SquadManagement.PlayerSquads(world, faction).Count >= _data.Bodies.Constants.MaxSquads)
            throw new InvalidOperationException("The faction no longer has capacity for this roster.");
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