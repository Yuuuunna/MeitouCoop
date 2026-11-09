using System.Numerics;
using Meitou.Engine;

namespace Meitou.Tests.Engine;

public partial class SecureServerTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RemoteMovementToleratesLateAndSameTickPublications(bool sameTick)
    {
        await using var scene = new Scene();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var token = timeout.Token; var actor = scene.Actors[0];
        await using var connection = await ConnectRemote(scene, actor.Credential, token);
        await using var session = new RemotePlayerSession(connection, (_, _) => 300);
        connection.RequestEntry(actor.Grant.Entity);
        await PumpPlayer(session, () => session.CanControl, token);
        float Shown() => Vector3.Lerp(session.PreviousSnapshot.Characters.Single(c => c.Id == actor.Role).Position,
            session.CurrentSnapshot.Characters.Single(c => c.Id == actor.Role).Position, session.SimulationAlpha).X;
        async Task Publish(float x)
        {
            long tick = await scene.Loop.InvokeAsync(() =>
            {
                var table = scene.Runtime.World.Characters;
                table.BeginTick(); table.Next[actor.Role.Slot].Position = new(x, 300, 0); table.EndTick();
                scene.Runtime.World.RunTicks(3);
                return scene.Runtime.World.Tick;
            }, token);
            await WaitRemote(connection, state => state.View?.Snapshot.Tick >= tick, token);
            session.Advance(0);
        }
        for (int i = 1; i <= 8; i++) { await Publish(i * 10); session.Advance(.1); }
        await Publish(90);
        if (sameTick)
        {
            session.Advance(.02);
            float before = Shown();
            ulong publication = connection.State.View!.Publication;
            await WaitRemote(connection, state => state.View!.Publication > publication, token);
            session.Advance(0);
            Assert.Equal(before, Shown(), 4);
        }
        else
        {
            // The next packet is 50 ms later than the usual 100 ms. Every rendered
            // frame must keep moving while the existing authoritative segment lasts.
            float before = Shown();
            for (int i = 0; i < 15; i++)
            {
                session.Advance(.01);
                float after = Shown();
                Assert.True(after > before, $"Movement stopped on frame {i}: {before} -> {after}");
                Assert.InRange(after - before, 0, 1.1f);
                before = after;
            }
        }
    }
}
