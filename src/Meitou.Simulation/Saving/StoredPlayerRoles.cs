using System.Numerics;
using Meitou.Data.Save;
using Meitou.Simulation.Factions;
using Meitou.Simulation.Population;

namespace Meitou.Simulation.Saving;

/// <summary>
/// Checkpoint-backed player records. Consumed source records are never replayed after a live role leaves the table.
/// </summary>
public sealed class StoredPlayerRoles
{
    sealed class Group(Squad squad, Func<SaveCharacter, SavedParts?> read, Func<SavedParts, CharacterId> spawn, Action<SaveCharacter, CharacterId> undo)
    {
        public Squad Squad = squad;
        public Func<SaveCharacter, SavedParts?> Read = read;
        public Func<SavedParts, CharacterId> Spawn = spawn;
        public Action<SaveCharacter, CharacterId> Undo = undo;
        public List<Entry> Entries = [];
        public bool Failed;
    }
    sealed class Entry(Group group, SaveCharacter source, EntityId entity, int faction, bool player)
    {
        public Group Group = group;
        public SaveCharacter Source = source;
        public EntityId Entity = entity;
        public int Faction = faction;
        public bool Player = player;
        public bool Dead = group.Squad.Dead || source.GameState?.Bools.GetValueOrDefault(PlayerSquadSave.DeadKey) == true || source.Medical?.Bools.GetValueOrDefault("dead") == true;
        public bool Consumed;
    }
    readonly World _world;
    readonly List<Group> _groups = [];
    readonly Dictionary<EntityId, Entry> _entities = [];
    readonly Dictionary<SaveCharacter, Entry> _sources = [];
    internal StoredPlayerRoles(World world) => this._world = world;
    internal void Register(Squad squad, SavePlatoon platoon, PopulationData data, Func<SaveCharacter, SavedParts?> read,
        Func<SavedParts, CharacterId> spawn, Action<SaveCharacter, CharacterId> undo)
    {
        var group = new Group(squad, read, spawn, undo); _groups.Add(group);
        foreach (var character in platoon.Characters)
        {
            var entity = EntitySave.Read(character);
            int faction = data.FactionIndex(character.OwnerFactionId);
            var entry = new Entry(group, character, entity, faction < 0 ? squad.Faction : faction, character.GameState?.Bools.GetValueOrDefault(PlayerSquadSave.RoleKey) == true);
            group.Entries.Add(entry); _sources.Add(character, entry);
            if (!entity.IsNone && !_entities.TryAdd(entity, entry)) throw new InvalidDataException("Duplicate stored player identity.");
        }
    }
    internal void MarkLoaded(SaveCharacter source) { if (_sources.TryGetValue(source, out var entry)) entry.Consumed = true; }
    internal void MarkDead(SaveCharacter source) { if (_sources.TryGetValue(source, out var entry)) entry.Dead = true; }
    internal (PlayerRoleAvailability State, Vector3? Position, int Faction)? Locate(EntityId entity)
    {
        if (!_entities.TryGetValue(entity, out var entry)) return null;
        return (entry.Dead ? PlayerRoleAvailability.Dead : entry.Consumed ? PlayerRoleAvailability.Unavailable : PlayerRoleAvailability.Stored,
            entry.Dead || entry.Consumed ? null : Position(entry), entry.Faction);
    }
    public IEnumerable<Vector3> PendingPositions => _groups.SelectMany(g => g.Entries).Where(e => e.Player && !e.Consumed && !e.Dead).Select(Position);
    public IEnumerable<(Vector3 Position, bool Up)> PendingFactionPositions(int faction) => _groups.SelectMany(g => g.Entries)
        .Where(e => e.Faction == faction && !e.Consumed && !e.Dead)
        .Select(e => (Position(e), e.Source.Medical?.Bools.GetValueOrDefault("unconcious") != true));
    public int PendingCount(int squad) => _groups.Where(g => g.Squad.Id == squad).SelectMany(g => g.Entries).Count(e => !e.Consumed && !e.Dead);
    public int PendingFactionHeads(int faction) => _groups.SelectMany(g => g.Entries).Count(e => e.Player && e.Faction == faction && !e.Consumed && !e.Dead);
    internal int PendingMembers => _groups.SelectMany(g => g.Entries).Count(e => !e.Consumed && !e.Dead);
    public bool Failed(EntityId entity) => _entities.TryGetValue(entity, out var entry) && entry.Group.Failed;
    internal Vector3 CarryPosition(CharacterCold cold, Vector3 fallback)
    {
        if (cold.Save is not { CarryPartner.IsNull: false, CarryIsCarrier: false } link || _world.Saved is not { } saved) return fallback;
        return PartnerPosition(saved, link.CarryPartner, fallback);
    }
    Vector3 Position(Entry entry)
    {
        var state = entry.Source.GameState;
        var partner = state is not null && !state.Bools.GetValueOrDefault("carrying") ? Hand.Read(state, "carrying") : Hand.Null;
        return partner.IsNull || _world.Saved is not { } saved ? entry.Source.Position : PartnerPosition(saved, partner, entry.Source.Position);
    }
    Vector3 PartnerPosition(SavedPopulation saved, Hand handle, Vector3 fallback)
    {
        if (saved.CharacterOf(handle) is { } live && _world.Characters.TryResolveNext(live, out int slot)) return _world.Characters.Current[slot].Position;
        if (saved.Handles.Character(handle) is not { } partner) return fallback;
        if (saved.ByName.TryGetValue(partner.Platoon.Name, out var platoon) && _world.Platoons.Find(platoon.PlatoonId) is { } standIn)
        {
            var member = platoon.Members.FirstOrDefault(m => ReferenceEquals(m.Source, partner));
            var at = member?.Position ?? partner.Position; var delta = standIn.Position - platoon.Anchor;
            return at + new Vector3(delta.X, 0, delta.Y);
        }
        return partner.Position;
    }

    /// <summary>
    /// Restore complete saved squads only after their regions are ready. Failure leaves every unconsumed record retryable.
    /// </summary>
    public bool Restore(EntityId entity, Func<Vector3, bool> ready)
        => Restore(entity, ready, retry: true);
    bool Restore(EntityId entity, Func<Vector3, bool> ready, bool retry)
    {
        if (!_world.AtTickBoundary || _world.HasPendingDurableMutation) throw new InvalidOperationException("Stored roles require an available world boundary.");
        if (_world.Entities.TryResolve(entity, out _)) return true;
        if (!_entities.TryGetValue(entity, out var selected) || selected.Dead || selected.Consumed) return false;
        var needed = new HashSet<Group>(); var pending = new Queue<Group>(); pending.Enqueue(selected.Group);
        while (pending.TryDequeue(out var group))
        {
            if (!needed.Add(group)) continue;
            if (needed.Sum(g => g.Entries.Count) > 4096) throw new InvalidDataException("Stored carry dependency group is too large.");
            foreach (var entry in group.Entries.Where(e => !e.Consumed && !e.Dead))
            {
                var partner = entry.Source.GameState is { } state ? Hand.Read(state, "carrying") : Hand.Null;
                if (!partner.IsNull && _world.Saved?.Handles.Character(partner) is { } source && _sources.TryGetValue(source, out var other) && !other.Dead && !other.Consumed)
                    pending.Enqueue(other.Group);
            }
        }
        var entries = needed.SelectMany(g => g.Entries).Where(e => !e.Consumed && !e.Dead).ToArray();
        if (!retry && needed.Any(g => g.Failed)) return false;
        if (entries.Any(e => !ReferenceEquals(_world.Squads.Find(e.Group.Squad.Id), e.Group.Squad) || !ready(Position(e)))) return false;
        foreach (var entry in entries)
        {
            var state = entry.Source.GameState;
            var hand = state is null ? Hand.Null : Hand.Read(state, "carrying");
            if (hand.IsNull) continue;
            var partner = _world.Saved?.Handles.Character(hand);
            bool carrier = state!.Bools.GetValueOrDefault("carrying");
            if (partner is not null && (!carrier && partner.Medical?.Bools.GetValueOrDefault("dead") == true ||
                carrier && _sources.TryGetValue(partner, out var archived) && archived.Dead)) continue;
            if (partner is not null && _sources.TryGetValue(partner, out var dependency) && needed.Contains(dependency.Group) && !dependency.Dead && !dependency.Consumed) continue;
            if (_world.Saved?.CharacterOf(hand) is not { } live || !_world.Characters.TryResolveNext(live, out _)) return false;
        }
        var built = new List<(Entry Entry, SavedParts Parts)>();
        foreach (var entry in entries)
        {
            if (entry.Group.Read(entry.Source) is not { } parts) { foreach (var g in needed) g.Failed = true; return false; }
            if (parts.Medical?.Dead == true) { entry.Dead = true; continue; }
            if (parts.Race is null || parts.Medical is null || parts.Stats is null) { foreach (var g in needed) g.Failed = true; return false; }
            built.Add((entry, parts));
        }
        var spawned = new List<(Entry Entry, CharacterId Id)>();
        var leaders = needed.ToDictionary(g => g, g => g.Squad.Leader);
        _world.Characters.BeginTick(); _world.Characters.Phase = TablePhase.SlowWorld;
        try
        {
            foreach (var (entry, parts) in built)
            {
                var id = entry.Group.Spawn(parts); spawned.Add((entry, id));
            }
            foreach (var (entry, _) in spawned) entry.Consumed = true;
            foreach (var group in needed) group.Failed = false;
            foreach (var group in needed)
                if (group.Squad.Leader.IsNone && group.Squad.Members.Count != 0) group.Squad.Leader = group.Squad.Members[0];
            _world.Fixtures.Bind(_world);
            foreach (var carrying in _world.Systems.OfType<Carrying.CarrySystem>()) carrying.RestoreLinks(_world);
        }
        catch
        {
            foreach (var (entry, id) in spawned.AsEnumerable().Reverse())
            {
                entry.Group.Undo(entry.Source, id); entry.Consumed = false;
                _world.Characters.Remove(id, unloaded: true);
            }
            foreach (var (group, leader) in leaders) group.Squad.Leader = leader;
            foreach (var group in needed) group.Failed = true;
            throw;
        }
        finally { _world.Characters.Phase = TablePhase.Outside; _world.Characters.EndTick(); }
        _world.NoteStoredRolesRestored();
        return _world.Entities.TryResolve(entity, out _);
    }
    public void RestoreReady(Func<Vector3, bool> ready)
    {
        if (!_world.AtTickBoundary || _world.HasPendingDurableMutation) return;
        foreach (var entry in _entities.Values.Where(e => !e.Consumed && !e.Dead && !e.Group.Failed).ToArray())
            if (!entry.Consumed && ready(Position(entry))) Restore(entry.Entity, ready, retry: false);
    }
}