using Meitou.Simulation.Dialogue;
using Meitou.Simulation.Items;
using Meitou.Simulation.Player;
using Meitou.Simulation.Population;
using Meitou.Simulation.Weather;
using System.Collections.ObjectModel;
using System.Linq.Expressions;
using System.Collections.Concurrent;
using System.Reflection;
using Meitou.Simulation;

namespace Meitou.Networking.VNext;
public enum CaptureFamily
{
    Identity,
    Clock,
    MotionExternal,
    Authority,
    Appearance,
    Equipment,
    Animation,
    Vitals,
    Details,
    Scene,
    Interaction,
    Knowledge,
    Economy,
    Weather,
    Events,
    Survey,
    Container
}

public readonly record struct CaptureField(string Name, CaptureFamily Family);
public readonly record struct RecipientComponentKey(string Domain, ulong Entity, CaptureFamily Family, string Field);
public readonly record struct RecipientCaptureIdentity(Guid World, PlayerId Player, ulong ConnectionGeneration);
public readonly record struct CaptureClock(long Tick, ulong StateRevision, ulong Publication, double GameHours, double TimeScale, float SimulationStepSeconds);
/// <summary>
/// Detached bytes; neither callers nor later mutation of a source snapshot can change captured data.
/// </summary>
public sealed class CapturedField
{
    readonly byte[] _bytes;
    internal CapturedField(byte[] value) => _bytes = (byte[])value.Clone();
    public ReadOnlySpan<byte> Bytes => _bytes;
}

/// <summary>A migration capture boundary, not the protocol 21 wire schema or an authorization filter.
/// Source must already be recipient-filtered. Motion is deliberately external; animation changes remain separate.
/// Low-frequency fields use the existing content-reference serializer; no whole view/character is serialized.</summary>
public sealed class RecipientComponentCapture
{
    readonly WorldViewCodec _codec;
    readonly Func<CharacterId, ulong> _entityOf;
    readonly ConcurrentDictionary<Type, Func<object, byte[]>> _encoders = new();
    public RecipientComponentCapture(WorldViewCodec codec, Func<CharacterId, ulong> entityOf)
    {
        this._codec = codec ?? throw new ArgumentNullException(nameof(codec));
        this._entityOf = entityOf ?? throw new ArgumentNullException(nameof(entityOf));
        ValidateCoverage<PlayerWorldView>(ViewFields);
        ValidateCoverage<WorldSnapshot>(SnapshotFields);
        ValidateCoverage<CharacterSnapshot>(CharacterFields);
    }

    public CapturedRecipientComponents Capture(PlayerWorldView view)
    {
        if (view.World == Guid.Empty || view.Player.IsNone || view.ConnectionGeneration == 0)
            throw new InvalidDataException("Missing recipient capture identity.");
        var entries = new Dictionary<RecipientComponentKey, CapturedField>();
        Add(view, ViewFields, "recipient", 0, entries);
        Add(view.Snapshot, SnapshotFields, "world", 0, entries);
        var seen = new HashSet<ulong>();
        foreach (var character in view.Snapshot.Characters)
        {
            ulong entity = _entityOf(character.Id);
            if (entity == 0 || !seen.Add(entity))
                throw new InvalidDataException("Missing or duplicate stable character entity.");
            Add(character, CharacterFields, "character", entity, entries);
        }

        return new(new(view.World, view.Player, view.ConnectionGeneration), new(view.Snapshot.Tick, view.Snapshot.StateRevision, view.Publication, view.GameHours, view.TimeScale, view.SimulationStepSeconds), entries);
    }

    void Add<T>(T source, IReadOnlyList<CaptureField> fields, string domain, ulong entity, Dictionary<RecipientComponentKey, CapturedField> entries)
    {
        foreach (var field in fields)
        {
            if (field.Family is CaptureFamily.Identity or CaptureFamily.Clock or CaptureFamily.MotionExternal or CaptureFamily.Container)
                continue;
            var property = typeof(T).GetProperty(field.Name)!;
            object? value = property.GetValue(source);
            // Null is absent, so losing access to an optional field produces an explicit withdrawal.
            if (value is not null)
                entries.Add(new(domain, entity, field.Family, field.Name), new(_encoders.GetOrAdd(property.PropertyType, Encoder)(value)));
        }
    }

    Func<object, byte[]> Encoder(Type declaredType)
    {
        // Preserve the declared interface type: content/tuple/set converters target these contracts,
        // and serializing a boxed runtime Dictionary would bypass those converters.
        var value = Expression.Parameter(typeof(object));
        var method = typeof(WorldViewCodec).GetMethod("EncodeComponent", BindingFlags.Instance | BindingFlags.NonPublic)!;
        return Expression.Lambda<Func<object, byte[]>>(Expression.Call(Expression.Constant(_codec), method.MakeGenericMethod(declaredType), Expression.Convert(value, declaredType)), value).Compile();
    }

    static void ValidateCoverage<T>(IReadOnlyList<CaptureField> fields)
    {
        var actual = typeof(T).GetProperties(BindingFlags.Instance | BindingFlags.Public).Select(p => p.Name).Order(StringComparer.Ordinal);
        var declared = fields.Select(f => f.Name).Order(StringComparer.Ordinal);
        if (!actual.SequenceEqual(declared))
            throw new InvalidOperationException($"Incomplete component field coverage for {typeof(T).Name}.");
    }

    static IReadOnlyList<CaptureField> Fields(params (CaptureFamily Family, string Names)[] groups) => Array.AsReadOnly(groups.SelectMany(g => g.Names.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(n => new CaptureField(n, g.Family))).ToArray());
    public static IReadOnlyList<CaptureField> ViewFields { get; } = Fields((CaptureFamily.Identity, "World Player ConnectionGeneration"), (CaptureFamily.Clock, "Publication GameHours TimeScale SimulationStepSeconds"), (CaptureFamily.Container, "Snapshot"), (CaptureFamily.Authority, "Roles AuthorityRules ActiveFaction"), (CaptureFamily.Economy, "Factions FactionCatalogue"), (CaptureFamily.Survey, "Prospected"), (CaptureFamily.Events, "Events Messages"), (CaptureFamily.Weather, "Weather WeatherEffects"));
    public static IReadOnlyList<CaptureField> SnapshotFields { get; } = Fields((CaptureFamily.Clock, "Tick StateRevision"), (CaptureFamily.Container, "Characters"), (CaptureFamily.Scene, "FoliageOwnedTowns Buildings BuildingsRevision NestBuildings NestMeshes NestBuildingsRevision Ruins RuinsRevision EmptiedHouses EmptiedHousesRevision GroundItems GroundItemsRevision Containers Beds Dummies Seats Cages DoorPicks Doors DoorGroupRevisions"), (CaptureFamily.Interaction, "VerbLead Verbs ArrivalSerial ArrivalForget Trade Conversation CharacterTrade DialogueRequest OpenLoot LootOpener LootSessionId ContainerOpener Held HeldSerial PlayerTrade InspectedBuilding InspectedRichness InspectedTurret"), (CaptureFamily.Knowledge, "Towns Camps KnownFactions TutorialFacts"), (CaptureFamily.Economy, "SquadAi PlayerSquads ActiveSquad Outposts UnlockedBuildings OwnedTownBuildings Machines MachinesVersion Research Crafts Smiths"), (CaptureFamily.Events, "Projectiles Speech"));
    public static IReadOnlyList<CaptureField> CharacterFields { get; } = Fields((CaptureFamily.Identity, "Id"), (CaptureFamily.MotionExternal, "Position Yaw DrawYaw"), (CaptureFamily.Authority, "Faction SquadId IsPlayer Controller"), (CaptureFamily.Appearance, "Appearance Build Name RecordId Scale DrawDistanceScale PortraitSerial"), (CaptureFamily.Equipment, "WeaponDrawn RangedDrawn BackDrawn StuckBolts Inventory LimbBits BloodAmounts HeldTool Mouth MouthTone"), (CaptureFamily.Animation, "Animations HasFlies CrossbowShots Knockdown CarriedBy Carry Carrying WorkEvents Task PickingLock WorkingShackles InBed StayingLow InCombat CombatMode Activity Turret Ragdoll"), (CaptureFamily.Vitals, "Body HealthParts Asleep Down Conscious Standing Slave Disguise Status Bounties Imprisoned CageOpen Shackled ShacklesLocked WeatherNotice WeatherNoticeProtection"), (CaptureFamily.Details, "WantsToTalk IsTrader IsDiplomat Selected Inspected Orders RunSpeed Jobs JobsRevision Path PathExact Skills AllSkills SkillXpMultipliers ClosedSlots Hud ItemGrid FightsPlayer NoLootGuilt WarParty CanBeShackled CarriesShackles Decision Stealth Light Bar StealthKoChance CanStealthKo KidnapChance LockpickSkill Animal HostileToPlayer CanOpenInventory Prison RangedLine BedRecoveryRate InPlayerBuilding Watchers"));
}

/// <summary>
/// All withdrawals are recipient-scope removals. Destruction requires separate authoritative lifecycle evidence.
/// </summary>
public sealed record CapturedComponentDifference(IReadOnlyDictionary<RecipientComponentKey, CapturedField> Changes, IReadOnlyList<RecipientComponentKey> Withdrawals);
public sealed class CapturedRecipientComponents
{
    internal CapturedRecipientComponents(RecipientCaptureIdentity identity, CaptureClock clock, Dictionary<RecipientComponentKey, CapturedField> fields) => (Identity, Clock, Fields) = (identity, clock, new ReadOnlyDictionary<RecipientComponentKey, CapturedField>(fields));
    public RecipientCaptureIdentity Identity { get; }
    public CaptureClock Clock { get; }
    public IReadOnlyDictionary<RecipientComponentKey, CapturedField> Fields { get; }

    /// <summary>
    /// Pure comparison, not a delivery/ACK baseline. A caller must retain reliable changes until acknowledged.
    /// </summary>
    public CapturedComponentDifference Difference(CapturedRecipientComponents? acknowledged)
    {
        if (acknowledged is not null && Identity != acknowledged.Identity)
            throw new InvalidDataException("Cannot compare captures across recipients or connection generations.");
        var changed = Fields.Where(p => acknowledged is null || !acknowledged.Fields.TryGetValue(p.Key, out var before) || !p.Value.Bytes.SequenceEqual(before.Bytes)).ToDictionary(p => p.Key, p => p.Value);
        var removed = acknowledged?.Fields.Keys.Where(k => !Fields.ContainsKey(k)).ToArray() ?? [];
        return new(new ReadOnlyDictionary<RecipientComponentKey, CapturedField>(changed), Array.AsReadOnly(removed));
    }
}