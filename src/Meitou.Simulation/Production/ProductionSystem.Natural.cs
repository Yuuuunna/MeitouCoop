using Meitou.Data;
using Meitou.Data.Gameplay;
using Meitou.Data.Gameplay.Ai;
using Meitou.Data.Gameplay.Bodies;
using Meitou.Data.Save;
using Meitou.Data.World;
using Meitou.Simulation.Aggro;
using Meitou.Simulation.Ai;
using Meitou.Simulation.Bodies;
using Meitou.Simulation.Carrying;
using Meitou.Simulation.Combat;
using Meitou.Simulation.Factions;
using Meitou.Simulation.Items;
using Meitou.Simulation.Locks;
using Meitou.Simulation.Movement;
using Meitou.Simulation.Perception;
using Meitou.Simulation.Player;
using Meitou.Simulation.Population;
using System.Runtime.CompilerServices;
using System.Globalization;
using System.Numerics;
using Meitou.Data.Fcs;
using Meitou.Data.Gameplay.Construction;
using Meitou.Simulation.Construction;

namespace Meitou.Simulation.Production;
/// <summary>
/// Natural sites (docs/simulation.md "Production"; the original's rocks are ProductionBuildings with "is foliage", docs/game/buildings-production.md
/// "Resources"): a rock the foliage system places is worked like a player's mine. It is no BuildSite of the registry: its site is made from its id, a
/// pure function of the rock's BUILDING and position, so orders and perma-jobs name it and a saved job finds it again. Its machine lives in
/// World.Machines like any other and is not saved (the original saves no rock either, Observed).
/// </summary>
public sealed partial class ProductionSystem
{
    /// <summary>
    /// The prefix of a rock's site id.
    /// </summary>
    const string NaturalPrefix = "natural:";

    /// <summary>
    /// The rocks' sites by id, made on first use (null: the id names no rock).
    /// </summary>
    readonly Dictionary<string, BuildSite?> _naturalSites = new(StringComparer.Ordinal);
    /// <summary>
    /// A rock site's centre and bounds radius from the host's rock source, with its mesh's shape (footprint and bounds box) and the instance's
    /// transform when the source has the mesh: an operator inside them has reached the rock.
    /// </summary>
    readonly record struct RockBounds(Vector3 Centre, float Radius, RockShape? Shape, Matrix4x4 Transform)
    {
        /// <summary>
        /// The rock's centre on the ground plane (X, Z).
        /// </summary>
        public Vector2 Flat => new(Centre.X, Centre.Z);

        /// <summary>
        /// The X/Z gap from <paramref name="from"/> to the rock: to its mesh's footprint (<see cref="RockShape.Outline"/>) where the instance places
        /// it (taken at the box's height), else to its bounds circle. The footprint, not the bounds box: a long or scattered rock's box reached far
        /// past the stone, and miners mined from 20 m off (#332).
        /// </summary>
        public float Gap(Vector3 from)
        {
            var at = new Vector2(from.X, from.Z);
            if (Shape is not { } shape || !Matrix4x4.Invert(Transform, out var inverse)) return MathF.Max(0, Vector2.Distance(at, Flat) - Radius);
            var local = Vector3.Transform(new Vector3(from.X, Centre.Y, from.Z), inverse);
            var edge = shape.NearestXz(new Vector2(local.X, local.Z));
            var nearest = Vector3.Transform(new Vector3(edge.X, local.Y, edge.Y), Transform);
            return Vector2.Distance(at, new Vector2(nearest.X, nearest.Z));
        }

        /// <summary>
        /// Whether <paramref name="from"/> is inside the footprint scaled 1.2 about the box's middle (taken at the box's height), else inside the
        /// bounds circle scaled 1.2.
        /// </summary>
        public bool Touches(Vector3 from)
        {
            if (Shape is not { } shape || !Matrix4x4.Invert(Transform, out var inverse))
                return Vector2.Distance(new Vector2(from.X, from.Z), Flat) < 1.2f * Radius;

            var local = Vector3.Transform(new Vector3(from.X, Centre.Y, from.Z), inverse);
            var mid = new Vector2((shape.Min.X + shape.Max.X) / 2, (shape.Min.Z + shape.Max.Z) / 2);
            return shape.InsideXz(mid + (new Vector2(local.X, local.Z) - mid) / 1.2f);
        }
    }

    /// <summary>
    /// The rocks' bounds by site id, from the host's rock source.
    /// </summary>
    readonly Dictionary<string, RockBounds> _naturalBounds = new(StringComparer.Ordinal);

    /// <summary>
    /// The site id of the rock of BUILDING <paramref name="record"/> at <paramref name="at"/>.
    /// </summary>
    public static string NaturalId(string record, Vector3 at) =>
        string.Create(CultureInfo.InvariantCulture, $"{NaturalPrefix}{record}@{at.X:R},{at.Y:R},{at.Z:R}");

    /// <summary>
    /// Whether a site id names a rock (<see cref="NaturalId"/>).
    /// </summary>
    public static bool IsNatural(string id) => id.StartsWith(NaturalPrefix, StringComparison.Ordinal);
    /// <summary>
    /// A player site of the registry, else the rock a natural id names, else a town seat or training dummy; null for none.
    /// </summary>
    BuildSite? Site(World world, string id) => world.Buildings.Find(id) ?? Seat(world, id) ?? Natural(world, id) ?? TownDummy(world, id);
    /// <summary>
    /// The training dummies' sites by id, made on first use.
    /// </summary>
    readonly Dictionary<string, (Locks.Fixture Source, BuildSite Site)> _dummySites = new(StringComparer.Ordinal);
    /// <summary>
    /// A town training dummy (<see cref = "Locks.FixtureKind.Training"/>, docs/simulation.md "Production"): like a rock, no BuildSite of the registry,
    /// so it is neither drawn, saved nor built again; a complete site of the town building's owner, made from the fixture once its town has
    /// registered. Its machine lives in World.Machines and is not saved.
    /// </summary>
    BuildSite? TownDummy(World world, string id)
    {
        if (world.Fixtures.Find(id)is not { Kind: Locks.FixtureKind.Training } f || _db.Find(f.RecordId)is not { Type: FcsRecordType.Building } building)
            return null;
        if (_dummySites.TryGetValue(id, out var known) && ReferenceEquals(known.Source, f) && known.Site.Owner == f.Owner)
            return known.Site;
        var site = new BuildSite
        {
            Id = id,
            Record = f.RecordId,
            Name = building.Name,
            Position = f.Position,
            Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, f.Yaw),
            Owner = f.Owner,
            Materials = [],
            Complete = true,
        };
        _dummySites[id] = (f, site);
        return site;
    }

    /// <summary>
    /// The rocks' resource yields by building instance, read from the resource map on first use.
    /// </summary>
    readonly Dictionary<string, float> _naturalYields = new(StringComparer.Ordinal);
    /// <summary>
    /// The richness line of the building <paramref name = "instance"/> names (<c>ProductionBuilding::getGUIFertility</c>, function 1 or 27 with a
    /// resource): a machine's yield, or a rock's before it has been mined (no machine yet); null for anything else.
    /// </summary>
    public RichnessView? RichnessOf(World world, string? instance)
    {
        if (instance is null)
            return null;
        if (world.Machines.Find(instance)is { } m)
            return m.Data is { Function: MachineData.MineFunction or MachineData.NaturalMineFunction, Resource: not 0 and var r } ? new(instance, r, m.Yield)
            {
                SiteRevision = WorkSiteRevision(world, instance),
                Owner = Natural(world, instance)?.Owner ?? -1,
                TownName = TownNameOf(world, Natural(world, instance))
            }

            : null;
        if (Natural(world, instance)is not { } site || Data(site.Record)is not { Resource: not 0 and var resource } data)
            return null;
        if (!_naturalYields.TryGetValue(instance, out float y))
            _naturalYields[instance] = y = _resources.NodeYield(resource, site.Position);
        return new(instance, resource, y)
        {
            SiteRevision = world.BuildingRevision(site),
            Owner = site.Owner,
            TownName = TownNameOf(world, site),
            Efficiency = new(0, data.PowerOperators, 1, 1, data.IsConsumer, data.OutputRate * (site.Owner >= 0 ? ResearchFor(world, site.Owner).Upgrade(data.Id).ProductionMult : 1), y)
        };
    }

    /// <summary>
    /// The name of the player town a worked rock joined (null: none, the original's NO_TOWN, which the panel's <c>Town:</c> line leaves out).
    /// </summary>
    static string? TownNameOf(World world, BuildSite? site) =>
        site is { Town: not 0 and var number } ? world.Buildings.Towns.FirstOrDefault(t => t.Number == number)?.Name : null;
}
