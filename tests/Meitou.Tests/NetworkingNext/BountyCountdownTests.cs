using System.Buffers.Binary;
using Meitou.Networking;
using Meitou.Networking.VNext;
using Meitou.Simulation;
using Meitou.Simulation.Crime;
using Meitou.Tests.Engine;

namespace Meitou.Tests.NetworkingNext;

/// <summary>
/// Permanent bounty deadlines must survive the publication that previously disconnected online clients.
/// </summary>
public class BountyCountdownTests
{
    /// <summary>
    /// A permanent bounty and an expiring one survive spawn, clock-only updates, and replacement by a finite bounty.
    /// </summary>
    [Fact]
    public void PermanentBountySurvivesPublicationAndClockAdvance()
    {
        var db = WorldViewCodecTests.Database();
        var codec = new WorldViewCodec(db);
        var view = WorldViewCodecTests.View(db);
        var character = view.Snapshot.Characters[0] with
        {
            Animations = [],
            Bounties = [new("Holy Nation", 10000, "Assault", CrimeTable.LifetimeHours(10000)), new("Other", 500, "Theft", 20)],
        };
        view = view with { Snapshot = view.Snapshot with { Characters = [character] } };
        var publisher = new GameStatePublisher(codec, _ => 42, 11, 22);
        var replica = new GameStateReplica(codec, 11, 22);

        Apply(replica, publisher.Capture(view));
        Assert.Equal(double.PositiveInfinity, replica.View!.Snapshot.Characters[0].Bounties[0].HoursLeft);

        character = character with { Bounties = [character.Bounties[0], character.Bounties[1] with { HoursLeft = 17 }] };
        view = view with { Publication = 2, GameHours = view.GameHours + 3, Snapshot = view.Snapshot with { Characters = [character] } };
        Apply(replica, publisher.Capture(view));
        Assert.Equal(double.PositiveInfinity, replica.View!.Snapshot.Characters[0].Bounties[0].HoursLeft);
        Assert.Equal(17, replica.View.Snapshot.Characters[0].Bounties[1].HoursLeft);

        character = character with { Bounties = [character.Bounties[0] with { HoursLeft = 10 }, character.Bounties[1]] };
        view = view with { Publication = 3, Snapshot = view.Snapshot with { Characters = [character] } };
        Apply(replica, publisher.Capture(view));
        Assert.Equal(10, replica.View!.Snapshot.Characters[0].Bounties[0].HoursLeft);
    }

    /// <summary>
    /// Supporting the permanent sentinel does not permit corrupt countdowns on either side of the wire.
    /// </summary>
    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.NegativeInfinity)]
    public void InvalidBountyCountdownsRemainRejected(double hours)
    {
        var db = WorldViewCodecTests.Database();
        var codec = new WorldViewCodec(db);
        var view = WorldViewCodecTests.View(db);
        var character = view.Snapshot.Characters[0] with
        {
            Animations = [],
            Bounties = [new("Holy Nation", 10000, "Assault", hours)],
        };
        view = view with { Snapshot = view.Snapshot with { Characters = [character] } };
        var publisher = new GameStatePublisher(codec, _ => 42, 11, 22);
        Assert.Throws<InvalidDataException>(() => publisher.Capture(view));

        const double marker = 12345.6789;
        character = character with { Bounties = [character.Bounties[0] with { HoursLeft = marker }] };
        view = view with { Snapshot = view.Snapshot with { Characters = [character] } };
        publisher = new GameStatePublisher(codec, _ => 42, 11, 22);
        var publication = publisher.Capture(view);
        var bytes = Assert.Single(publication.Frames, frame => frame.AsSpan().IndexOf(BitConverter.GetBytes(marker)) >= 0);
        int offset = bytes.AsSpan().IndexOf(BitConverter.GetBytes(marker));
        BinaryPrimitives.WriteDoubleLittleEndian(bytes.AsSpan(offset, 8), hours);
        System.Security.Cryptography.SHA256.HashData(bytes.AsSpan(76)).CopyTo(bytes, 44);

        var replica = new GameStateReplica(codec, 11, 22);
        Assert.Throws<InvalidDataException>(() => Apply(replica, publication));
    }

    /// <summary>
    /// Delivers all reliable frames of a publication to the replica.
    /// </summary>
    static void Apply(GameStateReplica replica, GameStatePublication publication)
    {
        foreach (var frame in publication.Frames) replica.Apply(frame);
    }
}
