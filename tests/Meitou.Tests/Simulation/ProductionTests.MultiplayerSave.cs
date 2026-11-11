using System.Numerics;
using Meitou.Data;
using Meitou.Data.Fcs;
using Meitou.Data.Gameplay;
using Meitou.Data.Gameplay.Construction;
using Meitou.Data.Save;
using Meitou.Data.World;
using Meitou.Runtime;
using Meitou.Simulation;
using Meitou.Simulation.Player;
using Meitou.Simulation.Population;
using Meitou.Simulation.Construction;
using Meitou.Simulation.Saving;
using Meitou.Tests.Save;

namespace Meitou.Tests.Simulation;

public partial class ProductionTests
{
    [Theory]
    [InlineData("allocation")]
    [InlineData("owner")]
    [InlineData("content")]
    [InlineData("town")]
    public void MultiplayerAssetRestoreRejectsMissingIdentityOwnershipAndContent(string defect)
    {
        var db = Database(); var data = PersistenceData(db);
        using var rig = new Rig(db, Constants, [Tank]);
        rig.World.Players.SetMembership(PlayerId.Local, PlayerFaction, FactionPermission.All);
        var save = SaveCapture.Capture(rig.World, data, new SaveClock(1, 12, 0));
        var site = rig.World.Buildings.Find(rig.Sites[0])!;
        var state = HeldSave.BuildingStates(save)[BuildingKey.Of(site.Zone, site.Id)];
        switch (defect)
        {
            case "allocation": save.Camera.Record.Strings.Remove(BuildingSave.AllocationKey); break;
            case "owner": state.Strings["owner faction ID"] = "missing-faction"; break;
            case "content": save.Zones[site.Zone].BuildingList!.Instances[0].Target = "missing-building"; break;
            case "town": new Hand((int)FcsRecordType.Town, 999, 1, 0, 0).Write(state, "town"); break;
        }
        using var back = new Rig(db, Constants, []);
        Assert.Throws<InvalidDataException>(() => BuildingSave.ReadMultiplayer(save, back.World, data));
        Assert.Empty(back.World.Buildings.All);
    }

    static PopulationData PersistenceData(GameDatabase db)
    {
        var relations = FactionRelations.Build(Enumerable.Range(0, 4).Select(i => new FactionData { Id = "faction-" + i, Name = "Faction " + i }).ToArray());
        return new PopulationData { Db = db, Sites = [], Factions = relations.Factions, Relations = relations, PlayerFaction = PlayerFaction };
    }

    [Fact]
    public void MultiplayerCheckpointRestoresOutpostsBuildingsAndMachineContentsForEveryOwner()
    {
        var db = Database();
        var well = Rec(Well, FcsRecordType.Building, "Well");
        well.References["construction"] = [new FcsReference(Water, 20, 0, 0)];
        var patch = new FcsFile(); patch.Records.Add(well); db.Apply(patch, "construction.mod");
        using var rig = new Rig(db, Constants, [Tank, Tank, Well, Well, Workshop, Generator, Battery]);
        var world = rig.World; var data = PersistenceData(db);
        world.Players.SetMembership(PlayerId.Local, PlayerFaction, FactionPermission.All);
        var remote = world.Players.Register(new PlayerId(2));
        world.Players.SetMembership(remote.Id, 3, FactionPermission.All);
        var a = world.Buildings.AddTown(At, PlayerFaction, "A base");
        var b = world.Buildings.AddTown(At + new Vector3(300, 0, 0), 3, "B base");
        world.Buildings.AddTown(At + new Vector3(10000, 0, 0), 3, "Empty base");
        a.Public = true;
        for (int i = 0; i < rig.Sites.Count; i++)
        {
            var site = world.Buildings.Find(rig.Sites[i])!;
            var definition = BuildingBuildData.From(db.Find(site.Record)!);
            site.Owner = i is 0 or 2 ? PlayerFaction : 3;
            site.Town = site.Owner == PlayerFaction ? a.Number : b.Number;
            site.Target = definition.Target; site.Progress = definition.Target;
            site.Materials = definition.Materials.Select(m => new MaterialSlot(m.Item, m.Required) { Delivered = m.Required }).ToArray();
        }
        var damaged = world.Buildings.Find(rig.Sites[2])!;
        damaged.Progress = damaged.Target / 2; damaged.Public = false;
        var child = world.Buildings.Find(rig.Sites[1])!;
        child.Parent = rig.Sites[3];
        world.RunTick();
        Assert.True(rig.Machine(0).Storage!.Add(rig.Items.Create(Water, 5)!));
        Assert.True(rig.Machine(1).Storage!.Add(rig.Items.Create(Water, 8)!));
        rig.Machine(2).Output!.Count = 2; rig.Machine(2).Output!.Progress = 0.25f;
        rig.Machine(3).Output!.Count = 7; rig.Machine(3).Output!.Progress = 0.75f;
        rig.Production.RestoreCraft(rig.Machine(4), Gadget, null, 0.4f, 2);
        rig.Machine(4).Craft!.Repeat = true;
        rig.Machine(5).PowerOn = false; rig.Machine(5).Inputs[0].Count = 3;
        rig.Machine(6).Charge = 19;
        var upgrading = world.Buildings.Find(rig.Sites[4])!;
        upgrading.Complete = false; upgrading.Paused = true; upgrading.Upgrading = true; upgrading.Ruined = true;
        var zone = damaged.Zone;
        world.Buildings.Reserve(BuildingRegistry.IdOf(1000, zone));
        string consumed = world.Buildings.NewId(zone);
        var save = SaveCapture.Capture(world, data, new SaveClock(1, 12, 0));
        byte[] metadata = PlayerRegistrySave.Capture(world, data);
        // A procedurally generated NPC building of B's faction must not be reclassified as a player construction site.
        string npcId = BuildingRegistry.IdOf(900, zone);
        var file = save.Zones[zone].File;
        var npcState = new FcsRecord { RecordType = FcsRecordType.GamestateBuilding, StringId = npcId + "-S23", Id = SaveBuilder.NextNumber(file) };
        npcState.Strings["owner faction ID"] = data.Factions[3].Id;
        npcState.Strings["sentinel"] = "NPC state";
        new Hand((int)FcsRecordType.Building, save.Zones[zone].ContainerId, 11111, 900, 1).Write(npcState, "handle");
        file.Records.Add(npcState);
        var npcInstance = new FcsInstance { Id = npcId, Target = Well, Position = At, Rotation = Quaternion.Identity };
        npcInstance.States.Add(npcState.StringId);
        save.Zones[zone].BuildingList!.Instances.Add(npcInstance);
        file.SlotLists[0] = [.. file.SlotLists[0], 900];
        save.TownInstanceList!.Instances.Reverse(); // Explicit town numbers, not file order, determine identity.
        string root = SaveSamples.Temp("multiplayer-assets");
        try
        {
            var store = new CheckpointStore(root); store.Commit(save, metadata);
            var checkpoint = store.LoadCurrent()!;
            using var back = new Rig(db, Constants, []);
            var backData = PersistenceData(db);
            PlayerRegistrySave.PrepareCatalogue(backData, checkpoint.Metadata);
            var loaded = SaveLoader.Load(back.World, checkpoint.World, backData, new SaveLoadOptions { MultiplayerMetadata = checkpoint.Metadata });
            PlayerRegistrySave.Restore(back.World, backData, checkpoint.Metadata);
            Assert.Equal(7, back.World.Buildings.Count);
            Assert.Null(back.World.Buildings.Find(npcId));
            Assert.Equal(new[] { (1, PlayerFaction, "A base"), (2, 3, "B base"), (3, 3, "Empty base") },
                back.World.Buildings.Towns.Select(t => (t.Number, t.Owner, t.Name)));
            Assert.True(back.World.Buildings.FindTown(1)!.Public);
            foreach (var original in world.Buildings.All)
            {
                var actual = back.World.Buildings.Find(original.Id)!;
                Assert.Equal((original.Owner, original.Town, original.Parent, original.Progress, original.Complete, original.Paused, original.Upgrading, original.Ruined),
                    (actual.Owner, actual.Town, actual.Parent, actual.Progress, actual.Complete, actual.Paused, actual.Upgrading, actual.Ruined));
                Assert.Equal(original.Materials.Select(m => m.Delivered), actual.Materials.Select(m => m.Delivered));
            }
            var machines = rig.Sites.Select(id => back.World.Machines.Find(id)!).ToArray();
            Assert.Equal(5, machines[0].Storage!.Items.Sum(i => i.Quantity));
            Assert.Equal(8, machines[1].Storage!.Items.Sum(i => i.Quantity));
            Assert.Equal((2, 0.25f), (machines[2].Output!.Count, machines[2].Output!.Progress));
            Assert.Equal((7, 0.75f), (machines[3].Output!.Count, machines[3].Output!.Progress));
            Assert.Equal(0.4f, Assert.Single(machines[4].Craft!.Queue).Progress); Assert.True(machines[4].Craft!.Repeat);
            Assert.False(machines[5].PowerOn); Assert.Equal(3, machines[5].Inputs[0].Count);
            Assert.Equal(19, machines[6].Charge);
            Assert.NotEqual(consumed, back.World.Buildings.NewId(zone));
            back.World.Buildings.Remove(back.World.Buildings.Find(rig.Sites[0])!);
            var again = SaveCapture.Capture(back.World, backData, new SaveClock(1, 12, 0), loaded);
            Assert.DoesNotContain(again.Zones[zone].BuildingList!.Instances, i => i.Id == rig.Sites[0]);
            Assert.Contains(again.Zones[zone].BuildingList!.Instances, i => i.Id == rig.Sites[1]);
            Assert.Contains(again.Zones[zone].BuildingList!.Instances, i => i.Id == npcId);
            Assert.Equal("NPC state", HeldSave.BuildingStates(again)[BuildingKey.Of(zone, npcId)].Strings["sentinel"]);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
