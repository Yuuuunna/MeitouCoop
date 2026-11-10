using System.Numerics;
using Meitou.Networking;
using Meitou.Networking.VNext;
using Meitou.Simulation;
using Meitou.Tests.Engine;
namespace Meitou.Tests.NetworkingNext;

public sealed class PendingPresentationEventsTests
{
    [Fact]
    public void SourceRingExpiryAndRepeatedCapturesCannotLoseOrReinsertUnadmittedEvents()
    {
        var pending = new PendingPresentationEvents(new WorldViewCodec(WorldViewCodecTests.Database()));
        for (ulong id = 1; id <= 600; id++) pending.Observe(new(id, []) { Sparks = [new(id, new(0, 1))] });
        var retained = pending.Observe(new(600, []));
        Assert.Equal(600, retained.Sparks.Count);
        pending.Admit(300);
        retained = pending.Observe(new(601, []) { Sparks = [new(1, new(0, 1)), new(601, new(0, 1))] });
        Assert.Equal(302, retained.Sparks.Count); Assert.Equal(1ul, retained.Sparks[0].Id);
        pending.Admit(601); Assert.Single(pending.Observe(new(601, []) { Sparks = [new(601, new(0, 1))] }).Sparks);
        Assert.Empty(pending.Observe(new(601, [])).Sparks);
        pending.Reset(); Assert.Single(pending.Observe(new(1, []) { Sparks = [new(1, new(0, 1))] }).Sparks);
    }
    [Fact]
    public void ByteBoundRejectsAnUnadmittedEventWithoutSilentlyDiscardingEarlierEvents()
    {
        var pending = new PendingPresentationEvents(new WorldViewCodec(WorldViewCodecTests.Database()));
        pending.Observe(new(1, []) { Sparks = [new(1, new(0, 1))] });
        Assert.Throws<IOException>(() => pending.Observe(new(2, [new(2, Floater.Text(new(0, 1), Vector3.Zero, new string('x', 256 * 1024)))])));
    }
}
