using Meitou.Data.Fcs;
using System.Numerics;
using Meitou.Data;
using Meitou.Data.Gameplay.Construction;
using Meitou.Data.World;
using Meitou.Simulation;
using Meitou.Simulation.Construction;

namespace Meitou.Runtime.Placement;
public readonly record struct PlacementSurface(float Height, GhostFloor Floor);
/// <summary>
/// Builds placement queries from live authority state, never from client snapshots or a camera.
/// </summary>
public sealed class WorldPlacement(GameDatabase db, Func<World, PlacementServices> services, Func<World, int, PlannedBuilding, PlacementSurface?> parentSurface, Func<Vector3, bool> regionReady, Func<int, string> factionRecord, Func<IReadOnlyList<PlacementTown>> towns) : IConstructionPlacement
{
    /// <summary>
    /// Matches the existing preview's bounded height adjustment; multiplayer observation requires a role or owned outpost nearby.
    /// </summary>
    public const float MaxLift = 1000, BuildReach = ConstructionSystem.JoinTownRange;
    readonly Dictionary<string, BuildingBuildData?> _buildings = new(StringComparer.Ordinal);
    readonly Dictionary<string, IReadOnlyList<PlacedMesh>> _parts = new(StringComparer.Ordinal);
    BuildingBuildData? Data(string id)
    {
        if (!_buildings.TryGetValue(id, out var result))
            _buildings[id] = result = db.Find(id)is { Type: Meitou.Data.Fcs.FcsRecordType.Building } r ? BuildingBuildData.From(r) : null;
        return result;
    }

    public PlacementFailure Check(World world, PlayerId? actor, int faction, PlannedBuilding plan)
    {
        try
        {
            return CheckCore(world, actor, faction, plan);
        }
        catch (Exception error)when (error is IOException || error is InvalidDataException)
        {
            return PlacementFailure.NotAvailable;
        }
    }

    PlacementFailure CheckCore(World world, PlayerId? actor, int faction, PlannedBuilding plan)
    {
        if (Data(plan.Record)is not { } data || !Finite(plan.Position) || !float.IsFinite(plan.Rotation.LengthSquared()) || MathF.Abs(plan.Rotation.LengthSquared() - 1) > 0.01f || plan.Floor is < 0 or > 3 || !WorldLayout.ZoneOf(plan.Position.X, plan.Position.Z).IsInsideGrid)
            return PlacementFailure.InvalidPlan;
        if (!regionReady(plan.Position))
            return PlacementFailure.RegionNotReady;
        if (actor is { } player && !Observed(world, player, faction, plan.Position))
            return PlacementFailure.InvalidPlan;
        var service = services(world);
        if (service.Footprint(data.Id)is { } footprint)
            foreach (var corner in footprint.Place(plan.Position, plan.Rotation))
                if (!WorldLayout.ZoneOf(corner.X, corner.Y).IsInsideGrid || !regionReady(new(corner.X, plan.Position.Y, corner.Y)))
                    return PlacementFailure.RegionNotReady;
        GhostFloor? floor = null;
        float surface;
        if (plan.Parent is not null)
        {
            if (parentSurface(world, faction, plan)is not { } parent || !PlacementRules.MayBuildIn(data, parent.Floor.Owned, parent.Floor.Destroyed, parent.Floor.OwnerNotReal))
                return PlacementFailure.NotOwnedBuilding;
            if (parent.Floor.Floor != plan.Floor)
                return PlacementFailure.InvalidFloor;
            floor = parent.Floor;
            if (data.CeilingPlacement && floor.Ceiling is not { })
                return PlacementFailure.InvalidFloor;
            surface = data.CeilingPlacement ? floor.Ceiling!.Value - 1 : parent.Height;
        }
        else
        {
            if (plan.Floor != 0)
                return PlacementFailure.InvalidFloor;
            surface = service.Ground(plan.Position.X, plan.Position.Z);
        }

        if (!float.IsFinite(surface) || MathF.Abs(plan.Position.Y - surface) > MaxLift)
            return PlacementFailure.InvalidGround;
        var sites = world.Buildings.All.Select(s => new BuildingSnapshot(s.Id, s.Record, s.Position, s.Rotation, s.Floor, s.Parent, s.Owner, s.Progress, s.Target, [], s.Complete, s.Paused) { Destroyed = s.Destroyed }).ToArray();
        if (sites.Any(s => s.Record == plan.Record && Vector3.DistanceSquared(s.Position, plan.Position) < 0.01f))
            return PlacementFailure.TooCloseToBuilding;
        var characters = new List<CharacterSnapshot>();
        var table = world.Characters;
        for (int i = 0; i < table.HighWater; i++)
        {
            if (!table.Current[i].Alive || table.Cold(i)is not { DiedTick: < 0 } cold || cold.Medical?.Dead == true || !cold.CarriedBy.IsNone)
                continue;
            characters.Add(new(new CharacterId(i, table.Current[i].Generation), null, table.Current[i].Position, table.Current[i].Yaw, []));
        }

        if (!_parts.TryGetValue(plan.Record, out var layout))
            _parts[plan.Record] = layout = WorldObjectLayout.Building(db, db.Find(plan.Record)!, "placement", Vector3.Zero, Quaternion.Identity, new BuildingState());
        bool Joint(BuildingSnapshot other)
        {
            if (!data.LinksTogether || other.Owner != faction || Data(other.Record)is not { LinksTogether: true } link)
                return false;
            var end = plan.Position + Vector3.Transform(new Vector3(0, 0, data.LinkLength), plan.Rotation);
            var otherEnd = other.Position + Vector3.Transform(new Vector3(0, 0, link.LinkLength), other.Rotation);
            return Vector3.DistanceSquared(plan.Position, otherEnd) <= ConstructionSystem.JoinDistanceSquared || Vector3.DistanceSquared(end, other.Position) <= ConstructionSystem.JoinDistanceSquared;
        }

        var query = new PlacementQuery(data, plan.Position, plan.Rotation, PlacementCheck.YawOf(plan.Rotation), plan.Floor, layout, [], characters, sites, towns(), factionRecord(faction), service)
        {
            On = floor,
            WallPiece = data.LinksTogether,
            IgnoreSite = Joint
        };
        // The submitted transform is already the preview's settled transform. A hypothetical corrective nudge must not authorize a floating or buried site.
        if (PlacementCheck.Margins(query)is { Down: < -0.001f } or { Up: < -0.001f })
            return PlacementFailure.NotLevel;
        return PlacementCheck.Evaluate(query);
    }

    static bool Finite(Vector3 p) => float.IsFinite(p.X) && float.IsFinite(p.Y) && float.IsFinite(p.Z);
    static bool Observed(World world, PlayerId player, int faction, Vector3 position)
    {
        foreach (var grant in world.Players.KnownRoles(player))
            if (world.Players.Validate(grant) && world.Entities.TryResolve(grant.Entity, out var role) && Vector3.DistanceSquared(world.Characters.Current[role.Slot].Position, position) <= BuildReach * BuildReach)
                return true;
        return world.Buildings.Towns.Any(t => t.Owner == faction && Vector2.DistanceSquared(new(t.Position.X, t.Position.Z), new(position.X, position.Z)) <= t.Radius * t.Radius);
    }
}