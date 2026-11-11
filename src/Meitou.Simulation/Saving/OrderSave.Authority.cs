using System.Numerics;
using Meitou.Simulation.Construction;
using Meitou.Simulation.Locks;
using Meitou.Simulation.Production;

namespace Meitou.Simulation.Saving;

public static partial class OrderSave
{
    // Stable checkpoint evidence; transient UI/world revision numbers are intentionally absent.
    sealed record OrderAsset(string Kind, string Key, string Record, int Owner, Vector3 Position,
        Quaternion Rotation, string? Parent, string? WallParent, int Floor, FixtureKind? FixtureKind = null);

    static string? ConstructionKey(ConstructionSiteCommand source, string? site) => source switch
    {
        BuildOrder build => build.Site,
        DismantleOrder dismantle => dismantle.Site,
        _ => site,
    };

    static string? FixtureKey(FixtureCommand source, World world, string? site) => source switch
    {
        UseBedOrder bed => bed.Bed,
        PutInBedOrder bed => bed.Bed,
        Crime.PutInCageOrder cage => cage.Cage,
        GetInCageOrder cage => cage.Cage,
        PickLockOrder pick => pick.Cage ?? (world.Characters.TryResolveNext(pick.Prisoner, out int slot) ? world.Characters.Cold(slot)?.InWhat : null),
        DoorOrder door => door.Door,
        DoorLockOrder door => door.Door,
        ForceLockOrder force => force.Fixture,
        _ => source.ExpectedFixtureRevision == 0 ? null : site,
    };

    static OrderAsset BuildingAsset(string kind, BuildSite site) => new(kind, site.Id, site.Record, site.Owner,
        site.Position, site.Rotation, site.Parent, site.WallParent, site.Floor);

    static OrderAsset FixtureAsset(Fixture fixture) => new("fixture", fixture.Id, fixture.RecordId, fixture.Owner,
        fixture.Position, Quaternion.Identity, fixture.Building, fixture.HostRecordId, fixture.DoorIndex, fixture.Kind);

    static OrderAsset[]? DescribeAssets(SimCommand source, World world, string? site)
    {
        if (source is ConstructionSiteCommand construction)
        {
            if (ConstructionKey(construction, site) is not { } key) return null;
            if (world.Buildings.Find(key) is { } building && ConstructionSystem.Root(world.Buildings, building) is { } root)
                return root == building ? [BuildingAsset("site", building)] : [BuildingAsset("site", building), BuildingAsset("root", root)];
            return source is BuildOrder { Repair: true } && world.Fixtures.Find(key) is { } fixture ? [FixtureAsset(fixture)] : null;
        }
        if (source is SiteWorkCommand)
        {
            if (site is null || world.SystemOf<ProductionSystem>()?.SaveWorkSite(world, site) is not { } work) return null;
            return [BuildingAsset("work", work)];
        }
        if (source is FixtureCommand fixtureCommand)
        {
            var key = FixtureKey(fixtureCommand, world, site);
            if (key is null) return fixtureCommand.ExpectedFixtureRevision == 0 ? [] : null;
            return world.Fixtures.Find(key) is { } fixture ? [FixtureAsset(fixture)] : null;
        }
        if (source is Items.OpenContainerOrder container)
            return world.Fixtures.Find(container.Fixture) is { } fixture ? [FixtureAsset(fixture)] : null;
        // Inventory transfers and commands carrying an existing opaque UI session are never queue orders.
        return source is Items.InventoryCommand ? null : [];
    }

    static SimCommand RebindAssets(SimCommand source, World world, string? site)
    {
        if (source is ConstructionSiteCommand construction && ConstructionKey(construction, site) is { } key)
        {
            if (world.Buildings.Find(key) is { } building && ConstructionSystem.Root(world.Buildings, building) is { } root)
                return construction with { SiteRevision = world.BuildingRevision(building), RootRevision = world.BuildingRevision(root) };
            if (world.Fixtures.Find(key) is { } fixture)
                return construction with { SiteRevision = world.Fixtures.RevisionOf(fixture), RootRevision = 0 };
        }
        if (source is SiteWorkCommand work && site is not null && world.SystemOf<ProductionSystem>() is { } production)
            return work with { SiteRevision = production.WorkSiteRevision(world, site) };
        if (source is FixtureCommand fixtureCommand && FixtureKey(fixtureCommand, world, site) is { } fixtureKey && world.Fixtures.Find(fixtureKey) is { } target)
            return fixtureCommand with { ExpectedFixtureRevision = world.Fixtures.RevisionOf(target) };
        return source;
    }
}