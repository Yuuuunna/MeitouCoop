using System.Numerics;
using System.Text;
using System.Text.Json.Nodes;
using Meitou.Data;
using Meitou.Data.Characters;
using Meitou.Data.Fcs;
using Meitou.Data.Gameplay.Ai;
using Meitou.Networking;
using Meitou.Simulation;
using Meitou.Simulation.Factions;
using Meitou.Simulation.Movement;
using Meitou.Simulation.Locks;
using Meitou.Simulation.Production;
using Meitou.Tests.Simulation;

namespace Meitou.Tests.Engine;

public class WorldViewCodecTests
{
    [Fact]
    public void ReusedValidationSetsAllowSiblingSchemasButRejectEscapedDuplicateFields()
    {
        var db = Database(); var codec = new WorldViewCodec(db); var view = View(db);
        view = view with { Snapshot = view.Snapshot with { Characters = Enumerable.Range(1, 100)
            .Select(i => view.Snapshot.Characters[0] with { Id = new(i, 2) }).ToArray() } };
        var bytes = codec.Encode(view);
        Assert.Equal(100, codec.Decode(bytes).Snapshot.Characters.Count);
        string json = Encoding.UTF8.GetString(bytes);
        string duplicate = json.Replace("\"World\":", $"\"\\u0057orld\":\"{view.World}\",\"World\":", StringComparison.Ordinal);
        Assert.NotEqual(json, duplicate);
        Assert.Throws<InvalidDataException>(() => codec.Decode(Encoding.UTF8.GetBytes(duplicate)));
    }

    internal static GameDatabase Database() => SyntheticTown.Database(extra:
        [SyntheticTown.Rec("wire-race", FcsRecordType.Race, "Race"), SyntheticTown.Rec("wire-clothes", FcsRecordType.Armour, "Clothes")]);
    internal static PlayerWorldView View(GameDatabase db)
    {
        var look = new CharacterAppearance { Race = db.Find("wire-race")!, Shape = new CharacterShapeInput
            { Sliders = new Dictionary<string, float> { ["Height"] = 125 }, Missing = new HashSet<Limb> { Limb.LeftArm } } };
        look.Parts.Add(new CharacterPart { Record = db.Find("wire-clothes")!, Mesh = "clothes.mesh", Field = "mesh", Mode = AttachMode.SharedSkeleton, Slot = AttachSlot.Body });
        look.Notes.Add("private diagnostic path"); look.PoseWeights["face"] = .25f;
        look.AttachmentPoints["hands"] = new("hands", "hand", new(1, 2, 3), Quaternion.Identity);
        var pose = new BonePose(2) { Weight = .75f }; pose.Set[1] = true; pose.Rotation[1] = Quaternion.Identity; pose.Position[1] = new(4, 5, 6);
        var character = new CharacterSnapshot(new(1, 2), look, new(10, 20, 30), .5f, [new("idle", .3f, 1)])
        {
            Jobs = [PermaJob.Untargeted(TaskType.JobMedic)], Ragdoll = pose,
            ItemGrid = new(10, 10, 0, 0, [new(0, "private-item", "Item", "main", 0, 0, 1, 1, 5, 1, .2f)]) { Revision = 9 },
        };
        var research = new ResearchSnapshot(2, 3, [("science", "Science", .5f)], 1, []) { Known = new HashSet<string> { "known" } };
        return new(Guid.NewGuid(), new(2), 1, 1, 12.5, new(4, [character])
        {
            StateRevision = 7, Research = research, UnlockedBuildings = new HashSet<string> { "building" },
            Doors = new Dictionary<(string, int), DoorView> { [("house", 2)] = new(.5f, default, false, false) },
        }, [new(new(123), character.Id, 7, 1, true, false)], [new(7, "Faction", FactionPermission.All, 1, 999, research, [])
            { Formation = (int)SquadFormation.Caravan, UnlockedBuildings = new HashSet<string> { "building" }, Standings = [new(7, 100, true, false)],
                Contracts = [FactionInfoView.From(new(FactionInfoKind.Name, "Contract")), FactionInfoView.From(new(FactionInfoKind.Detail, "Expires", Hours: 5))] }])
            { ActiveFaction = 7, TimeScale = 2, FactionCatalogue = [new(7, "faction-seven", "Faction", false, true)] };
    }

    [Fact]
    public void CompleteViewPreservesRenderingDetailsPrivateGrantsAndTupleCollections()
    {
        var db = Database(); var codec = new WorldViewCodec(db); var original = View(db);
        byte[] bytes = codec.Encode(original); string text = Encoding.UTF8.GetString(bytes);
        Assert.DoesNotContain("private diagnostic path", text); Assert.DoesNotContain("\"Loadout\"", text);
        Assert.DoesNotContain("\"RecordType\"", text); Assert.Contains("\"Race\":\"wire-race\"", text);
        var copy = codec.Decode(bytes); var character = Assert.Single(copy.Snapshot.Characters);
        Assert.Equal(original.World, copy.World); Assert.Equal(999, Assert.Single(copy.Factions).Money);
        Assert.Equal(7, copy.ActiveFaction); Assert.Equal(2, copy.TimeScale);
        Assert.Equal((int)SquadFormation.Caravan, Assert.Single(copy.Factions).Formation);
        Assert.Contains("building", Assert.Single(copy.Factions).UnlockedBuildings);
        Assert.Equal("faction-seven", Assert.Single(copy.FactionCatalogue).RecordId);
        Assert.True(Assert.Single(Assert.Single(copy.Factions).Standings).Ally);
        Assert.True(double.IsNaN(copy.Factions[0].Contracts[0].ToLine().Hours)); Assert.Equal(5, copy.Factions[0].Contracts[1].ToLine().Hours);
        Assert.True(float.IsNaN(character.DrawYaw)); Assert.Equal(new Vector3(10, 20, 30), character.Position);
        Assert.Equal(5, Assert.Single(character.ItemGrid!.Items).Quantity); Assert.Equal(9ul, character.ItemGrid.Revision);
        Assert.Equal(TaskType.JobMedic, Assert.Single(character.Jobs).Type);
        Assert.Equal(.75f, character.Ragdoll!.Weight); Assert.True(character.Ragdoll.Set[1]); Assert.Equal(new Vector3(4, 5, 6), character.Ragdoll.Position[1]);
        Assert.Same(db.Find("wire-race"), character.Appearance!.Race); Assert.Same(db.Find("wire-clothes"), Assert.Single(character.Appearance.Parts).Record);
        Assert.Equal(125, character.Appearance.Shape!.Sliders["Height"]); Assert.Contains(Limb.LeftArm, character.Appearance.Shape.Missing);
        Assert.Equal(.25f, character.Appearance.PoseWeights["face"]); Assert.Equal(new Vector3(1, 2, 3), character.Appearance.AttachmentPoints["hands"].Position);
        Assert.Equal(("science", "Science", .5f), Assert.Single(copy.Snapshot.Research.Queue));
        Assert.Contains("known", copy.Snapshot.Research.Known); Assert.Equal(.5f, copy.Snapshot.Doors[("house", 2)].Open);
    }

    [Fact]
    public void MissingContentAndMalformedViewsNeverProducePartialReplicas()
    {
        var db = Database(); var codec = new WorldViewCodec(db); var bytes = codec.Encode(View(db));
        foreach (var edit in new Action<JsonObject>[]
        {
            n => n["Snapshot"]!["Characters"]![0]!["Appearance"]!["Race"] = "missing-race",
            n => n["Snapshot"]!["Characters"]!.AsArray().Add(n["Snapshot"]!["Characters"]![0]!.DeepClone()),
            n => n["Snapshot"]!["Characters"]![0]!["Ragdoll"]!["Set"]!.AsArray().Add(false),
            n => n["ConnectionGeneration"] = 0,
            n => n["TimeScale"] = 3,
            n => n["ActiveFaction"] = 999,
            n => n["FactionCatalogue"]!.AsArray().Add(n["FactionCatalogue"]![0]!.DeepClone()),
            n => n["Factions"]![0]!["Standings"]![0]!["Faction"] = 999,
            n => n["Snapshot"]!["Characters"]![0]!["Position"]!["X"] = 1e100,
            n => n["Snapshot"]!["Characters"]![0]!["ItemGrid"]!["Items"]![0]!["Quality"] = 1e100,
            n => n["Snapshot"]!["Characters"]![0]!["Jobs"]![0] = null,
            n => n["Snapshot"]!["Characters"]![0]!["Appearance"]!["AttachmentPoints"]!["hands"] = null,
        })
        {
            var node = JsonNode.Parse(bytes)!.AsObject(); edit(node);
            Assert.Throws<InvalidDataException>(() => codec.Decode(Encoding.UTF8.GetBytes(node.ToJsonString())));
        }
        Assert.Throws<InvalidDataException>(() => codec.Decode(new byte[WorldViewCodec.MaximumBytes + 1]));
    }
}
