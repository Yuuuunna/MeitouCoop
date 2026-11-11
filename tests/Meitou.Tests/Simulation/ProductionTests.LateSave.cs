using System.Numerics;
using Meitou.Data.Fcs;
using Meitou.Data.Save;
using Meitou.Data.World;
using Meitou.Runtime;
using Meitou.Simulation;
using Meitou.Simulation.Player;
using Meitou.Simulation.Population;
using Meitou.Simulation.Construction;
using Meitou.Simulation.Locks;
using Meitou.Simulation.Production;
using Meitou.Simulation.Saving;
using Meitou.Tests.Save;
using SimWorld = Meitou.Simulation.World;

namespace Meitou.Tests.Simulation;

public partial class ProductionTests
{
    static (PopulationData Data, TownBuilding House) LateMachineData()
    {
        var db = Database(); var definitions = new FcsFile();
        var player = Rec("204-gamedata.base", FcsRecordType.Faction, "Nameless");
        var owner = Rec("owner", FcsRecordType.Faction, "Owner");
        var town = Rec("town", FcsRecordType.Town, "Town");
        town.Ints["type"] = 2; town.References["faction"] = [new("owner", 0, 0, 0)];
        var function = Rec("house-function", FcsRecordType.BuildingFunctionality, "House"); function.Ints["function"] = 19;
        var building = Rec("house-machine", FcsRecordType.Building, "House machine");
        building.Ints["build materials"] = 32; building.Bools["has inventory"] = true;
        building.Ints["storage size width"] = building.Ints["storage size height"] = 8;
        building.References["functionality"] = [new("house-function", 0, 0, 0)];
        definitions.Records.AddRange([player, owner, town, function, building]); db.Apply(definitions, "houses.mod");
        var house = new TownBuilding("home", building.StringId, "House machine", At, 0, false, false, [])
        {
            Zone = WorldLayout.ZoneOf(At.X, At.Z), Destroyed = true,
            Fixtures = [new TownFixture(BuildingKey.Of(WorldLayout.ZoneOf(At.X, At.Z), "home") + "/0", Tank, false, At, 0)
                { Function = TownBuildings.GeneralStorageFunction, StorageWidth = 4, StorageHeight = 4 }],
        };
        var data = PopulationData.Create(db, [new TownPlacement("town-instance", "town", At, null)],
            buildings: new Dictionary<string, List<TownBuilding>> { ["town-instance"] = [house] });
        return (data, house);
    }

    static SimWorld LateMachineWorld(PopulationData data) => new(new WorldSettings { PublishSnapshots = false },
        new OpenGroundWalkability((_, _) => 300),
        [new PlayerSystem(), new ProductionSystem(data.Db, data.Bodies.Constants, data.BodyOptions, data.Items, null)]);

    static (SaveGame Save, byte[] Metadata) LateMachineCheckpoint()
    {
        var (data, house) = LateMachineData();
        int remote = data.RegisterFaction("runtime:remote", "Remote", data.PlayerFaction);
        using var world = LateMachineWorld(data);
        var player = world.Players.Register(new PlayerId(2));
        world.Players.SetMembership(player.Id, remote, FactionPermission.All);
        var builder = SaveCapture.PrepareBase(data);
        var zone = new SaveZone(house.Zone, new SaveFile()); builder.Game.Zones.Add(house.Zone, zone);
        var list = new FcsRecord { Id = 1, StringId = WorldLevelData.BuildingListId, RecordType = FcsRecordType.InstanceCollection };
        var state = new FcsRecord { Id = 2, StringId = "arbitrary-house-state", RecordType = FcsRecordType.GamestateBuilding };
        state.Strings["owner faction ID"] = data.Factions[remote].Id;
        state.Bools[FixtureRegistry.PurchasedKey] = true;
        state.Bools["meitou town construction"] = true;
        state.Bools["is complete"] = false; state.Bools["destroyed"] = false; state.Floats["construction progress"] = 7;
        new Hand((int)FcsRecordType.Building, zone.ContainerId, 11111, 1, 1).Write(state, "handle");
        var instance = new FcsInstance { Id = house.InstanceId, Target = house.BuildingId, Position = house.Position, Rotation = Quaternion.Identity };
        instance.States.Add(state.StringId); list.Instances.Add(instance); zone.File.Records.AddRange([list, state]); zone.File.SlotLists.Add([1]);
        world.Fixtures.Saved = HeldSave.BuildingStates(builder.Game);
        world.Fixtures.RegisterSite(world, data, 0);
        var machine = world.Machines.Find(house.Key)!;
        Assert.NotNull(machine);
        Assert.True(machine.Storage!.Add(data.Items.Create(Water, 7)!));
        machine.PowerOn = false;
        var save = SaveCapture.Capture(world, data, new SaveClock(1, 12, 0), prepared: builder);
        Assert.Equal(house.InstanceId + "-inventory", save.Zones[house.Zone].BuildingList!.Instances.Single().States[0]);
        return (save, PlayerRegistrySave.Capture(world, data));
    }

    [Fact]
    public void PurchasedLevelMachineLoadsOnceWhenItsRegionRegistersAndDoesNotReplayStock()
    {
        var (save, metadata) = LateMachineCheckpoint();
        string root = SaveSamples.Temp("late-level-machine");
        try
        {
            var store = new CheckpointStore(root); store.Commit(save, metadata);
            var checkpoint = store.LoadCurrent()!;
            var (data, house) = LateMachineData(); PlayerRegistrySave.PrepareCatalogue(data, checkpoint.Metadata);
            using var world = LateMachineWorld(data);
            var loaded = SaveLoader.Load(world, checkpoint.World, data, new SaveLoadOptions { MultiplayerMetadata = checkpoint.Metadata });
            PlayerRegistrySave.Restore(world, data, checkpoint.Metadata);
            Assert.Empty(world.Buildings.All); Assert.Equal(0, world.Machines.Count);
            world.Fixtures.RegisterSite(world, data, 0);
            var site = world.Buildings.Find(house.Key)!;
            Assert.Equal(data.FactionIndex("runtime:remote"), site.Owner);
            var machine = world.Machines.Find(house.Key)!;
            Assert.Equal(7, machine.Storage!.Items.Sum(i => i.Quantity)); Assert.False(machine.PowerOn);
            var production = world.Systems.OfType<ProductionSystem>().Single();
            machine.Storage.Items.Clear();
            Assert.Same(machine, production.Ensure(world, site));
            MachineSave.Read(checkpoint.World, world, production, data.Items, allOwners: true);
            Assert.Empty(machine.Storage.Items);
            world.Machines.Remove(site.Id);
            var replacement = production.Ensure(world, site)!;
            Assert.NotSame(machine, replacement); Assert.Empty(replacement.Storage!.Items);
            Assert.True(replacement.PowerOn);
            world.Machines.Remove(site.Id);
            var after = SaveCapture.Capture(world, data, new SaveClock(1, 12, 0), loaded);
            Assert.DoesNotContain(after.Zones[house.Zone].BuildingList!.Instances.Single().States,
                id => id.EndsWith("-inventory", StringComparison.Ordinal));
            using var restarted = LateMachineWorld(data);
            SaveLoader.Load(restarted, after.Clone(), data, new SaveLoadOptions { MultiplayerMetadata = checkpoint.Metadata });
            PlayerRegistrySave.Restore(restarted, data, checkpoint.Metadata);
            restarted.Fixtures.RegisterSite(restarted, data, 0);
            Assert.Empty(restarted.Machines.Find(house.Key)!.Storage!.Items);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void TownResetBeforeMachineCreationSurvivesRecaptureAndCannotRestoreOldStockAfterRestart()
    {
        var (save, metadata) = LateMachineCheckpoint();
        var (data, house) = LateMachineData(); PlayerRegistrySave.PrepareCatalogue(data, metadata);
        var state = HeldSave.BuildingStates(save)[house.Key];
        state.Bools[FixtureRegistry.PurchasedKey] = false;
        state.Bools.Remove("meitou town construction");
        state.Strings["owner faction ID"] = "owner";
        state.Strings["meitou container 0"] = "stale contents";
        using var world = LateMachineWorld(data);
        var loaded = SaveLoader.Load(world, save, data, new SaveLoadOptions { MultiplayerMetadata = metadata });
        PlayerRegistrySave.Restore(world, data, metadata);
        Assert.Equal(0, world.Machines.Count);
        world.Fixtures.TownReplaced(world, data, 0);
        var after = SaveCapture.Capture(world, data, new SaveClock(1, 12, 0), loaded);
        var instance = after.Zones[house.Zone].BuildingList!.Instances.Single();
        Assert.DoesNotContain(instance.States, id => id.EndsWith("-inventory", StringComparison.Ordinal));
        Assert.False(HeldSave.BuildingStates(after)[house.Key].Bools.ContainsKey("power on"));
        Assert.False(HeldSave.BuildingStates(after)[house.Key].Strings.ContainsKey("meitou container 0"));
        using var restarted = LateMachineWorld(data);
        SaveLoader.Load(restarted, after.Clone(), data, new SaveLoadOptions { MultiplayerMetadata = metadata });
        PlayerRegistrySave.Restore(restarted, data, metadata);
        var placement = new BuildingPlacement(house.Zone, house.InstanceId, house.BuildingId, house.Position, Quaternion.Identity, null, house.Position.Y);
        var site = new BuildSite { Id = house.Key, LevelPlacement = placement, Record = house.BuildingId, Owner = data.FactionIndex("owner"), Position = house.Position, Materials = [], Complete = true };
        restarted.Buildings.Add(site);
        var machine = restarted.Systems.OfType<ProductionSystem>().Single().Ensure(restarted, site)!;
        Assert.Empty(machine.Storage!.Items); Assert.True(machine.PowerOn);
    }

    [Fact]
    public void TownResetClearsOldContainersButKeepsContentsAcquiredAfterTheReset()
    {
        var (save, metadata) = LateMachineCheckpoint();
        var (data, house) = LateMachineData(); PlayerRegistrySave.PrepareCatalogue(data, metadata);
        var state = HeldSave.BuildingStates(save)[house.Key];
        state.Bools[FixtureRegistry.PurchasedKey] = false; state.Bools.Remove("meitou town construction");
        state.Strings["owner faction ID"] = "owner";
        using var world = LateMachineWorld(data);
        var loaded = SaveLoader.Load(world, save, data, new SaveLoadOptions { MultiplayerMetadata = metadata });
        PlayerRegistrySave.Restore(world, data, metadata);
        world.Fixtures.RegisterSite(world, data, 0);
        Assert.True(world.Fixtures.Find(house.Key + "/0")!.Contents!.Add(data.Items.Create(Water, 5)!));
        world.Fixtures.TownReplaced(world, data, 0);
        var reset = world.Fixtures.Find(house.Key + "/0")!.Contents!;
        Assert.Empty(reset.Items);
        Assert.True(reset.Add(data.Items.Create(Water, 2)!));
        var after = SaveCapture.Capture(world, data, new SaveClock(1, 12, 0), loaded);
        using var restored = LateMachineWorld(data);
        SaveLoader.Load(restored, after.Clone(), data, new SaveLoadOptions { MultiplayerMetadata = metadata });
        PlayerRegistrySave.Restore(restored, data, metadata);
        restored.Fixtures.RegisterSite(restored, data, 0);
        Assert.Equal(2, restored.Fixtures.Find(house.Key + "/0")!.Contents!.Items.Sum(i => i.Quantity));
    }
}
