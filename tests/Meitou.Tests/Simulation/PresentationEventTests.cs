using System.Numerics;
using Meitou.Simulation;
using SimWorld = Meitou.Simulation.World;

namespace Meitou.Tests.Simulation;

public class PresentationEventTests
{
    [Fact]
    public void BloodIsPositionalWithHiddenSubjectsRedactedAndSparksRequireAVisibleSubject()
    {
        var clock = new Clock(); var journal = new PresentationEventJournal(clock);
        var visible = new CharacterId(0, 1); var hidden = new CharacterId(1, 1);
        journal.Add(new BloodVisual(new(BloodMarkKind.Explosion, hidden, Vector3.Zero, Vector2.One, 1, 0xFF0000, "race"), 10, 1f / 30, 1000));
        journal.Add(new BloodVisual(new(BloodMarkKind.Splash, visible, new(20000, 0, 0), Vector2.One, 1, 0xFF0000), 10, 1f / 30, 1000));
        journal.AddSpark(visible); journal.AddSpark(hidden);
        var batch = journal.Capture(new(2), new HashSet<CharacterId> { visible }, p => p.X < 100);
        Assert.Equal(4ul, batch.HighWater); Assert.True(Assert.Single(batch.Blood).Value.Mark.Who.IsNone);
        Assert.Equal(visible, Assert.Single(batch.Sparks).Who);
        clock.Now = TimeSpan.FromSeconds(4).Ticks;
        var expired = journal.Capture(new(2), new HashSet<CharacterId> { visible }, _ => true);
        Assert.Empty(expired.Blood); Assert.Empty(expired.Sparks); Assert.Equal(4ul, expired.HighWater);
    }

    sealed class Clock : TimeProvider
    {
        public long Now;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Now;
    }

    [Fact]
    public void JournalBoundsAndExpiresEventsWithoutReusingTheirIdentities()
    {
        var clock = new Clock(); var journal = new PresentationEventJournal(clock);
        var player = new PlayerId(2); var role = new CharacterId(0, 1);
        for (int i = 0; i < 600; i++) journal.Add(Floater.Text(role, Vector3.Zero, "private"), player, null);
        var batch = journal.Capture(player, new HashSet<CharacterId> { role }, _ => true);
        Assert.Equal(600ul, batch.HighWater); Assert.Equal(PresentationEventJournal.Capacity, batch.Floaters.Count);
        Assert.Equal(89ul, batch.Floaters[0].Id);
        Assert.Empty(journal.Capture(new(3), new HashSet<CharacterId> { role }, _ => true).Floaters);
        clock.Now = TimeSpan.FromSeconds(4).Ticks;
        Assert.Empty(journal.Capture(player, new HashSet<CharacterId> { role }, _ => true).Floaters);
        journal.Add(Floater.Text(role, Vector3.Zero, new string('x', 5000)), player, null);
        var next = journal.Capture(player, new HashSet<CharacterId> { role }, _ => true);
        Assert.Equal(601ul, Assert.Single(next.Floaters).Id); Assert.Equal(4096, next.Floaters[0].Value.Part.Length);
    }

    [Fact]
    public void SameFactionMembersReceiveOnlyTheirPrivateTextAndObserverRelativeCombatFlags()
    {
        using var world = new SimWorld(new WorldSettings { PublishSnapshots = false }, new OpenGroundWalkability((_, _) => 0));
        var a = world.Players.Register(new(2)); var b = world.Players.Register(new(3));
        world.Players.SetMembership(a.Id, 7, FactionPermission.All); world.Players.SetMembership(b.Id, 7, FactionPermission.All);
        CharacterId Spawn(PlayerId player)
        {
            var role = world.Characters.Spawn(new(), new() { Faction = 7, IsPlayer = true }, 0);
            world.Players.AssignInitial(player, role); return role;
        }
        var ca = Spawn(a.Id); var cb = Spawn(b.Id);
        var connectionA = world.Authority.Open(a.Id); var connectionB = world.Authority.Open(b.Id);
        Assert.True(world.Authority.BeginSynchronization(connectionA)); Assert.True(world.Authority.BeginSynchronization(connectionB));
        world.EmitFloater(Floater.Text(ca, Vector3.Zero, "Ate secret A"));
        world.EmitFloater(Floater.Text(cb, Vector3.Zero, "Ate secret B"));
        world.EmitFloater(Floater.Hit(cb, Vector3.Zero, true, true, "Head", default, default), a.Id);
        world.EmitFloater(Floater.Label(new(20000, 0, 0), "hidden label"));
        var av = world.Authority.CaptureView(connectionA, new(), new(100))!.Events;
        var bv = world.Authority.CaptureView(connectionB, new(), new(100))!.Events;
        Assert.Equal(4ul, av.HighWater); Assert.Equal(2, av.Floaters.Count); Assert.Equal(2, bv.Floaters.Count);
        Assert.Contains(av.Floaters, e => e.Value.Part == "Ate secret A"); Assert.DoesNotContain(av.Floaters, e => e.Value.Part == "Ate secret B");
        Assert.Contains(bv.Floaters, e => e.Value.Part == "Ate secret B"); Assert.DoesNotContain(bv.Floaters, e => e.Value.Part == "Ate secret A");
        var ahit = Assert.Single(av.Floaters, e => e.Value.Kind == FloaterKind.Damage).Value;
        var bhit = Assert.Single(bv.Floaters, e => e.Value.Kind == FloaterKind.Damage).Value;
        Assert.False(ahit.TargetIsPlayer); Assert.True(ahit.SourceIsPlayer);
        Assert.True(bhit.TargetIsPlayer); Assert.False(bhit.SourceIsPlayer);
        Assert.True(world.Players.TryTransfer(a.Id, world.Players.ControlOf(ca)!.Value, b.Id, out _));
        Assert.DoesNotContain(world.Authority.CaptureView(connectionB, new(), new(100))!.Events.Floaters, e => e.Value.Part == "Ate secret A");
    }

    [Fact]
    public void LegacyQueueRetainsTheOriginalLocalFloaterPayload()
    {
        using var world = new SimWorld(new WorldSettings(), new OpenGroundWalkability((_, _) => 0));
        world.Floaters = new();
        var value = Floater.Hit(new(1, 1), Vector3.Zero, true, false, "Head", default, default);
        world.EmitFloater(value);
        Assert.True(world.Floaters.TryDequeue(out var received)); Assert.Equal(value, received);
    }

    [Fact]
    public void SoundsSeparatePrivateCuesFromVisibleEmittersAndRedactHiddenReferences()
    {
        var journal = new PresentationEventJournal(new Clock());
        var a = new PlayerId(2); var b = new PlayerId(3);
        var first = new CharacterId(1, 1); var second = new CharacterId(2, 1); var hidden = new CharacterId(3, 1);
        journal.Add(new SoundEvent(SoundKind.Hit, second, hidden, Vector3.Zero, Item: "must not leak"), b);
        journal.Add(new SoundEvent(SoundKind.BountyPaid, first, CharacterId.None, new(20000, 0, 0)), a);
        journal.Add(new SoundEvent(SoundKind.ShacklesOn, first, CharacterId.None, Vector3.Zero, Item: "private item"), a);
        journal.Add(new SoundEvent(SoundKind.Shot, hidden, CharacterId.None, Vector3.Zero), null);
        journal.Add(new SoundEvent(SoundKind.BoltStuck, CharacterId.None, hidden, Vector3.Zero), null);
        journal.Add(new SoundEvent(SoundKind.Shot, second, CharacterId.None, new(20000, 0, 0)), b);
        var visible = new HashSet<CharacterId> { first, second };
        var av = journal.Capture(a, visible, p => p.X < 100).Sounds;
        var bv = journal.Capture(b, visible, p => p.X < 100).Sounds;
        Assert.Equal(new[] { SoundKind.Hit, SoundKind.BountyPaid, SoundKind.ShacklesOn, SoundKind.BoltStuck }, av.Select(e => e.Value.Kind));
        Assert.Equal(new[] { SoundKind.Hit, SoundKind.BoltStuck }, bv.Select(e => e.Value.Kind));
        Assert.All(av, e => Assert.True(e.Value.Other.IsNone)); Assert.All(bv, e => Assert.Null(e.Value.Item));
        Assert.Equal("private item", av[2].Value.Item);
        var missingSubject = journal.Capture(a, new HashSet<CharacterId> { second }, _ => true);
        Assert.True(Assert.Single(missingSubject.Sounds, e => e.Value.Kind == SoundKind.BountyPaid).Value.Subject.IsNone);
    }

    [Fact]
    public void LegacySoundQueueReceivesTheExactProducerPayload()
    {
        using var world = new SimWorld(new WorldSettings(), new OpenGroundWalkability((_, _) => 0));
        world.Sounds = new();
        var value = new SoundEvent(SoundKind.BoltHit, new(1, 2), new(4, 5), new(6, 7, 8), Armoured: true);
        world.EmitSound(value);
        Assert.True(world.Sounds.TryDequeue(out var received)); Assert.Equal(value, received);
    }
}
