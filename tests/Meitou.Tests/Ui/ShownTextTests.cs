using Meitou.Localisation;
using Meitou.Simulation;
using Meitou.Tests.Simulation;
using SimWorld = Meitou.Simulation.World;

namespace Meitou.Tests.Ui;

/// <summary>
/// Text sources are process-wide, so registration and refresh tests must not overlap other UI or simulation tests.
/// </summary>
[CollectionDefinition("Shown text", DisableParallelization = true)]
public sealed class ShownTextCollection;

/// <summary>
/// Character identity and display location distinguish a text source's answers without changing authoritative names.
/// </summary>
[Collection("Shown text")]
public sealed class ShownTextTests
{
    /// <summary>
    /// Characters with the same source name must not share a title, and a label's title must not replace the same character's panel name.
    /// </summary>
    [Theory]
    [InlineData("Guard", ShownTextKind.Record)]
    [InlineData("Beep", ShownTextKind.Name)]
    public void Answers_are_cached_by_character_and_place_and_refreshed_together(string name, ShownTextKind kind)
    {
        var database = SyntheticTown.Database();
        var host = new object();
        var asked = new List<ShownTextContext>();
        string title = "Captain";
        const ulong first = 0xfedcba9876543210, second = first + 1;
        string Expected(ulong id, ShownPlace place) => $"{title}:{id:X16}:{place}";
        ShownText.Add(host, "character-names", context =>
        {
            asked.Add(context);
            return Expected(context.Character, context.Place);
        });
        try
        {
            foreach (ulong id in new[] { first, second })
                foreach (var place in new[] { ShownPlace.Label, ShownPlace.Panel })
                {
                    Assert.Equal(Expected(id, place), database.ShownName("character", "2-t", name, id, place));
                    Assert.Equal(Expected(id, place), database.ShownName("character", "2-t", name, id, place));
                }
            Assert.Equal(4, asked.Count);
            Assert.All(asked, context => Assert.Equal(kind, context.Kind));
            Assert.All(asked, context => Assert.Equal(kind == ShownTextKind.Record ? "name" : "character", context.Field));

            Assert.Equal(Expected(0, ShownPlace.Unknown), database.ShownName("character", "2-t", name));
            Assert.Equal(5, asked.Count);
            title = "Commander";
            ShownText.Refresh();
            Assert.Equal(Expected(first, ShownPlace.Label), database.ShownName("character", "2-t", name, first, ShownPlace.Label));
            Assert.Equal(6, asked.Count);
            Assert.Equal("Guard", database.Find("2-t")!.Name);
        }
        finally { ShownText.Remove(host); }
    }

    /// <summary>
    /// Server-side notices, dialogue and owner tooltips receive the lasting ID and place, while saves and state hashes keep the original name.
    /// </summary>
    [Fact]
    public void World_names_pass_display_context_without_changing_authoritative_state()
    {
        using var world = new SimWorld(new WorldSettings { PublishSnapshots = false }, new OpenGroundWalkability((_, _) => 0));
        var actor = world.Characters.Spawn(new CharacterHot { Health = 100 }, new CharacterCold { Name = "Beep", RecordId = "2-t" }, 0);
        var lasting = world.LastingIds.GetOrGive(actor);
        var cold = world.Characters.Cold(actor.Slot)!;
        ulong before = world.StateHash();
        var host = new object();
        var asked = new List<ShownTextContext>();
        ShownText.Add(host, "character-names", context =>
        {
            asked.Add(context);
            return context.Place + ": " + context.Text;
        });
        try
        {
            Assert.Equal("Notice: Beep", world.ShownName(cold, ShownPlace.Notice));
            Assert.Equal("Dialogue: Beep", world.ShownName(actor, ShownPlace.Dialogue));
            Assert.Equal("Tooltip: Beep", world.ShownOwnerNameOf(actor, ShownPlace.Tooltip));
            Assert.Null(world.ShownOwnerNameOf(CharacterId.None, ShownPlace.Tooltip));
            Assert.Equal(3, asked.Count);
            Assert.All(asked, context => Assert.Equal(lasting.Value, context.Character));
            Assert.Equal("Beep", cold.Name);
            Assert.Equal(before, world.StateHash());
        }
        finally { ShownText.Remove(host); }
    }
}
