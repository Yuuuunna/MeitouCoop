using System.Numerics;
using Meitou.Networking;
using Meitou.Networking.VNext;
using Meitou.Simulation;
using Meitou.Tests.Engine;

namespace Meitou.Tests.NetworkingNext;

/// <summary>
/// Producer and retention contracts must survive both codec validation and reliable critical publication.
/// </summary>
public sealed class PresentationEventValidationTests
{
    /// <summary>
    /// A door hit has a fixed position and no character target, including after source journal projection.
    /// </summary>
    [Fact]
    public void BuildingDamageSurvivesJournalProjectionBaselineAndCriticalPublication()
    {
        var database = WorldViewCodecTests.Database();
        var codec = new WorldViewCodec(database);
        var source = WorldViewCodecTests.View(database);
        var journal = new PresentationEventJournal();
        var first = Floater.BuildingHit(new(10, 20, 30), 42);
        journal.Add(first, null, null);
        var batch = journal.Capture(source.Player, new HashSet<CharacterId>(), _ => true);
        var view = source with { Events = batch };

        Assert.Equal(first, Assert.Single(codec.Decode(codec.Encode(view)).Events.Floaters).Value);
        var peer = new GameReplicationFixture(codec);
        peer.Apply(peer.Capture(view));
        Assert.Equal(first, Assert.Single(peer.Client.View!.Events.Floaters).Value);

        var second = Floater.BuildingHit(new(30, 20, 10), 24);
        journal.Add(second, null, null);
        batch = journal.Capture(source.Player, new HashSet<CharacterId>(), _ => true);
        foreach (var part in peer.Server.CaptureCritical(source with { Publication = 2, Events = batch })) peer.Apply(part);

        Assert.Equal(new[] { first, second }, peer.Client.View!.Events.Floaters.Select(entry => entry.Value));
    }

    /// <summary>
    /// Source-sized captures may accumulate beyond 512 entries without making the next reliable publication invalid.
    /// </summary>
    [Theory]
    [InlineData(513)]
    [InlineData(4096)]
    public void RetainedEventsRoundTripThroughCodecAndCriticalPublicationAtTheSharedBound(int count)
    {
        var database = WorldViewCodecTests.Database();
        var codec = new WorldViewCodec(database);
        var source = WorldViewCodecTests.View(database);
        var pending = new PendingPresentationEvents(codec);
        for (int start = 1; start <= count; start += 512)
        {
            var entries = Sparks(start, Math.Min(512, count - start + 1));
            pending.Observe(new(entries[^1].Id, []) { Sparks = entries });
        }

        var retained = pending.Observe(new((ulong)count, []));
        Assert.Equal(count, codec.Decode(codec.Encode(source with { Events = retained })).Events.Sparks.Count);

        var peer = new GameReplicationFixture(codec);
        peer.Apply(peer.Capture(source));
        foreach (var part in peer.Server.CaptureCritical(source with { Publication = 2, Events = retained })) peer.Apply(part);

        Assert.Equal(retained.Sparks, peer.Client.View!.Events.Sparks);
        pending.Admit(retained.HighWater);
        Assert.Empty(pending.Observe(new((ulong)count, [])).Sparks);
    }

    /// <summary>
    /// The limit applies to the sum of all families, while diagnostics distinguish wire validation from retention overflow.
    /// </summary>
    [Fact]
    public void CombinedCountOverflowReportsBothFamilyCountsAndTheSharedLimit()
    {
        var database = WorldViewCodecTests.Database();
        var codec = new WorldViewCodec(database);
        var source = WorldViewCodecTests.View(database);
        var batch = new PresentationEventBatch(4097, [new(4097, Floater.Label(Vector3.Zero, "private-display-text"))])
        {
            Sparks = Sparks(1, 4096)
        };

        var error = Assert.Throws<InvalidDataException>(() => codec.Encode(source with { Events = batch }));
        Assert.Contains("reason=count-limit count=4097 maximum=4096", error.Message);
        Assert.Contains("floaters=1 sounds=0 speech=0 blood=0 sparks=4096", error.Message);
        Assert.DoesNotContain("private-display-text", error.ToString());

        var pending = new PendingPresentationEvents(codec);
        pending.Observe(new(4096, []) { Sparks = batch.Sparks });
        var overflow = Assert.Throws<IOException>(() => pending.Observe(new(4097, batch.Floaters)));
        Assert.Contains("retention key limit exceeded: count=4097 maximum=4096", overflow.Message);
        Assert.Contains("floaters=1 sounds=0 speech=0 blood=0 sparks=4096", overflow.Message);
    }

    /// <summary>
    /// The projection bound includes still-visible admitted source entries as well as older unadmitted entries.
    /// </summary>
    [Fact]
    public void ProjectionOverflowReportsCurrentAndRetainedEventsWithoutSilentlyDroppingEither()
    {
        var codec = new WorldViewCodec(WorldViewCodecTests.Database());
        var pending = new PendingPresentationEvents(codec);
        pending.Admit(1);
        pending.Observe(new(4097, []) { Sparks = Sparks(2, 4096) });

        var error = Assert.Throws<IOException>(() => pending.Observe(new(4097, []) { Sparks = Sparks(1, 1) }));

        Assert.Contains("projection key limit exceeded: count=4097 maximum=4096", error.Message);
        Assert.Contains("admitted=1 highWater=4097", error.Message);
        Assert.Equal(4096, pending.Observe(new(4097, [])).Sparks.Count);
    }

    /// <summary>
    /// Invalid payloads and identities name their own category, event and field instead of blaming a different event family.
    /// </summary>
    [Theory]
    [InlineData("floater-target", "floater", "missing-target")]
    [InlineData("sound-kind", "sound", "unknown-kind")]
    [InlineData("speech-text", "speech", "text-length")]
    [InlineData("blood-amount", "blood", "invalid-amount")]
    [InlineData("spark-subject", "spark", "missing-subject")]
    [InlineData("cross-family-duplicate", "sound", "duplicate-id")]
    [InlineData("zero-id", "spark", "zero-id")]
    [InlineData("above-high-water", "spark", "id-above-high-water")]
    [InlineData("null-entry", "spark", "null-entry")]
    [InlineData("missing-collection", "spark", "missing-collection")]
    public void InvalidEventsReportCategoryReasonAndPublicationWithoutPayloads(string scenario, string category, string reason)
    {
        var database = WorldViewCodecTests.Database();
        var codec = new WorldViewCodec(database);
        var source = WorldViewCodecTests.View(database);
        var batch = InvalidBatch(scenario, source.Snapshot.Characters[0].Id);

        var error = Assert.Throws<InvalidDataException>(() => codec.Encode(source with { Events = batch }));

        Assert.Contains($"reason={reason}", error.Message);
        Assert.Contains($"category={category}", error.Message);
        Assert.Contains("highWater=1", error.Message);
        Assert.Contains($"world={source.World} generation=1 publication=1 tick=4", error.Message);
        Assert.DoesNotContain("private", error.ToString());
        if (scenario is not ("null-entry" or "missing-collection"))
        {
            ulong id = scenario switch
            {
                "zero-id" => 0,
                "above-high-water" => 2,
                _ => 1
            };
            Assert.Contains($"index=0 id={id}", error.Message);
        }
    }

    /// <summary>
    /// Builds malformed typed server payloads; display strings must never leak into the exception chain.
    /// </summary>
    static PresentationEventBatch InvalidBatch(string scenario, CharacterId who) => scenario switch
    {
        "floater-target" => new(1, [new(1, Floater.Text(CharacterId.None, Vector3.Zero, "private-floater"))]),
        "sound-kind" => new(1, []) { Sounds = [new(1, new((SoundKind)255, who, CharacterId.None, Vector3.Zero, Item: "private-item"))] },
        "speech-text" => new(1, []) { Speech = [new(1, who, new("private-speaker", new string('x', 4097), false))] },
        "blood-amount" => new(1, [])
        {
            Blood = [new(1, new(new(BloodMarkKind.Splash, who, Vector3.Zero, Vector2.One, -1, 0, "private-race"), 1, 1f / 30, 1))]
        },
        "spark-subject" => new(1, []) { Sparks = [new(1, CharacterId.None)] },
        "cross-family-duplicate" => new(1, [new(1, Floater.Label(Vector3.Zero, "private-label"))])
        {
            Sounds = [new(1, new(SoundKind.Hit, who, CharacterId.None, Vector3.Zero))]
        },
        "zero-id" => new(1, []) { Sparks = [new(0, who)] },
        "above-high-water" => new(1, []) { Sparks = [new(2, who)] },
        "null-entry" => new(1, []) { Sparks = [null!] },
        "missing-collection" => new(1, []) { Sparks = null! },
        _ => throw new ArgumentOutOfRangeException(nameof(scenario))
    };

    /// <summary>
    /// Small spark records permit exercising the count boundary independently of the byte budget.
    /// </summary>
    static BlockSparkEvent[] Sparks(int start, int count) => Enumerable.Range(start, count)
        .Select(id => new BlockSparkEvent((ulong)id, new(0, 1)))
        .ToArray();
}
