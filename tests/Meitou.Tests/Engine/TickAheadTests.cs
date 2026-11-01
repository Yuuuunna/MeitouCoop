using System.Numerics;
using Meitou.Engine;

namespace Meitou.Tests.Engine;

/// <summary>The tick run ahead during the world draw (docs/simulation.md "Tick ahead").</summary>
public class TickAheadTests
{
    [Fact]
    public void CommonClientLoopPreservesLocalModalPauseAndPresentationTime()
    {
        using var local = new WorldSession(Vector3.Zero, (-100, -100, 100, 100), (_, _) => 0);
        IClientSession client = local;
        client.Advance(1.0 / 30);
        Assert.Equal(1, local.World.Tick);
        Assert.Equal(local.ShownTick + local.SimulationAlpha, client.PresentationTick);
        Assert.Equal(local.Clock.GameSeconds, client.PresentationSeconds);
        client.SetModalPause(true); Assert.True(client.IsPaused); Assert.Equal(1, client.TimeScale);
        client.Advance(.1); Assert.Equal(1, local.World.Tick);
        client.SetModalPause(false); Assert.False(client.IsPaused);
        client.Advance(1.0 / 30); Assert.Equal(2, local.World.Tick);
    }

    [Fact]
    public void A_tick_run_ahead_shows_when_it_falls_due_and_a_pause_takes_it()
    {
        using var session = new WorldSession(Vector3.Zero, (-100, -100, 100, 100), (_, _) => 0);
        double tick = session.Simulation.TickSeconds;
        Assert.Equal(1, session.AdvanceSimulation(tick));
        var (shown, before, seconds) = (session.CurrentSnapshot, session.PreviousSnapshot, session.Clock.GameSeconds);

        // Ahead: the world runs tick 2; the shown pair, the shown tick and the game clock stay.
        session.BeginTickAhead();
        session.EndTickAhead();
        Assert.Equal(2, session.World.Tick);
        Assert.Equal(1, session.ShownTick);
        Assert.NotSame(shown, session.World.Snapshot);
        Assert.Same(shown, session.CurrentSnapshot);
        Assert.Same(before, session.PreviousSnapshot);
        Assert.Equal(seconds, session.Clock.GameSeconds);

        // A frame that owes no tick leaves it waiting.
        Assert.Equal(0, session.AdvanceSimulation(tick * 0.25));
        Assert.Equal(1, session.ShownTick);
        Assert.Same(shown, session.CurrentSnapshot);

        // Due: it is the tick owed (no other runs), the game clock moves once and the world's snapshots show.
        Assert.Equal(1, session.AdvanceSimulation(tick * 0.8));
        Assert.Equal(2, session.World.Tick);
        Assert.Equal(2, session.ShownTick);
        Assert.Same(session.World.Snapshot, session.CurrentSnapshot);
        Assert.Equal(seconds + tick, session.Clock.GameSeconds, 9);

        // Paused with a tick ahead: it is taken at once and the clock moves by it; no other runs, and none starts while paused.
        session.BeginTickAhead();
        session.EndTickAhead();
        session.Simulation.TogglePause();
        Assert.Equal(0, session.AdvanceSimulation(tick));
        Assert.Equal(3, session.World.Tick);
        Assert.Equal(3, session.ShownTick);
        Assert.Same(session.World.Snapshot, session.CurrentSnapshot);
        Assert.Equal(seconds + 2 * tick, session.Clock.GameSeconds, 9);
        session.BeginTickAhead();
        session.EndTickAhead();
        Assert.Equal(3, session.World.Tick);
    }
}
