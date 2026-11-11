using System.Numerics;
using Meitou.Data.World;
using Meitou.Simulation.Navigation;

namespace Meitou.Tests.Navigation;

public class NavPublicationTests
{
    [Fact]
    public async Task CurrentFailureIsRememberedUntilExplicitRetry()
    {
        var walk = new NavmeshWalkability((_, _) => 0); int builds = 0;
        using var service = new NavMeshService(walk, (_, _) => Interlocked.Increment(ref builds) == 1
            ? throw new IOException("missing geometry") : new(Mesh(2), NavMeshOrigin.Built));
        var failed = service.Prepare(Zone);
        await Assert.ThrowsAsync<IOException>(async () => await failed.WaitAsync(Watchdog, TestContext.Current.CancellationToken));
        Assert.Same(failed, service.Prepare(Zone)); Assert.Equal(1, builds);
        var retry = service.Rebuild(Zone);
        await service.Prepare(Zone).WaitAsync(Watchdog, TestContext.Current.CancellationToken);
        service.CommitPrepared(); await retry.WaitAsync(Watchdog, TestContext.Current.CancellationToken);
        Assert.True(service.IsReady(Zone)); Assert.Equal(2, builds);
    }

    static readonly ZoneCoordinate Zone = new(32, 32);
    static readonly TimeSpan Watchdog = TimeSpan.FromSeconds(15);
    static ZoneNavMesh Mesh(float height, bool kept = true) => new()
    {
        ZoneX = Zone.X, ZoneZ = Zone.Y,
        Vertices = [new(0, height, 0), new(100, height, 0), new(100, height, 100), new(0, height, 100)],
        Polygons = [[0, 1, 2, 3]], Neighbours = [[-1, -1, -1, -1]], Areas = [NavArea.Ground], Kept = [kept],
        BoundsMin = Vector2.Zero, BoundsMax = new(100),
    };
    static void Await(ManualResetEventSlim signal) { if (!signal.Wait(Watchdog)) throw new TimeoutException("Test build was not released."); }

    [Fact]
    public async Task PreparedResultsBecomeVisibleOnlyAtCommitAndNotifyOnTheCommitThread()
    {
        var walk = new NavmeshWalkability((_, _) => 0);
        using var service = new NavMeshService(walk, (_, _) => new(Mesh(10), NavMeshOrigin.Built));
        int notificationThread = -1;
        service.ZoneReady += _ => notificationThread = Environment.CurrentManagedThreadId;
        var published = service.Request(Zone);
        await service.Prepare(Zone).WaitAsync(Watchdog, TestContext.Current.CancellationToken);
        Assert.False(published.IsCompleted); Assert.False(walk.World.Contains(Zone)); Assert.False(service.IsReady(Zone));
        int commitThread = Environment.CurrentManagedThreadId;
        Assert.Contains(Zone, service.CommitPrepared());
        await published.WaitAsync(Watchdog, TestContext.Current.CancellationToken);
        Assert.True(service.IsReady(Zone)); Assert.Equal(commitThread, notificationThread);
        Assert.Equal(10, walk.World.Find(Zone)!.Vertices[0].Y);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SupersededBuildAndFailureCannotPublishOrCompleteTheNewGeneration(bool oldFails)
    {
        var walk = new NavmeshWalkability((_, _) => 0); var overlay = new NavRuntimeBuildings();
        overlay.Add(new(Zone, "a", "old", new(10, 0, 10), Quaternion.Identity, null, null) { Key = "a" });
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim(); int builds = 0; string? firstRecord = null;
        using var service = new NavMeshService(walk, (_, snapshot) =>
        {
            int n = Interlocked.Increment(ref builds);
            if (n == 1)
            {
                started.SetResult(); Await(release);
                firstRecord = Assert.Single(snapshot!.All()).BuildingId;
                if (oldFails) throw new IOException("old build failed");
            }
            return new(Mesh(n), NavMeshOrigin.Built);
        }, runtime: overlay);
        var published = service.Request(Zone);
        await started.Task.WaitAsync(Watchdog, TestContext.Current.CancellationToken);
        overlay.Add(new(Zone, "a", "new", new(10, 0, 10), Quaternion.Identity, null, null) { Key = "a" });
        _ = service.Rebuild(Zone);
        var prepared = service.Prepare(Zone); release.Set();
        await prepared.WaitAsync(Watchdog, TestContext.Current.CancellationToken); service.CommitPrepared(); await published.WaitAsync(Watchdog, TestContext.Current.CancellationToken);
        Assert.Equal("old", firstRecord); Assert.Equal(2, walk.World.Find(Zone)!.Vertices[0].Y);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnloadOrWorldReplacementRejectsAnEarlierIncarnation(bool replaceWorld)
    {
        var walk = new NavmeshWalkability((_, _) => 0);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim(); int builds = 0;
        using var service = new NavMeshService(walk, (_, _) =>
        {
            int n = Interlocked.Increment(ref builds);
            if (n == 1) { started.SetResult(); Await(release); }
            return new(Mesh(n), NavMeshOrigin.Built);
        });
        var old = service.Request(Zone); await started.Task.WaitAsync(Watchdog, TestContext.Current.CancellationToken);
        if (replaceWorld) service.NewIncarnation(); else service.Unload(Zone);
        var current = service.Prepare(Zone); release.Set(); await current.WaitAsync(Watchdog, TestContext.Current.CancellationToken);
        Assert.True(old.IsCanceled); Assert.False(walk.World.Contains(Zone));
        service.CommitPrepared(); Assert.Equal(2, walk.World.Find(Zone)!.Vertices[0].Y);
    }

    [Fact]
    public async Task RebuildInvalidatesPublishedGeometryAtTheBoundaryAndNeverCommitsAStagedOldResult()
    {
        var walk = new NavmeshWalkability((_, _) => 0);
        using var release = new ManualResetEventSlim(); int builds = 0;
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var service = new NavMeshService(walk, (_, _) =>
        {
            int n = Interlocked.Increment(ref builds);
            if (n == 2) { started.SetResult(); Await(release); }
            return new(Mesh(n), NavMeshOrigin.Built);
        });
        await service.Prepare(Zone).WaitAsync(Watchdog, TestContext.Current.CancellationToken); service.CommitPrepared();
        var rebuilding = service.Rebuild(Zone); await started.Task.WaitAsync(Watchdog, TestContext.Current.CancellationToken);
        Assert.False(service.IsReady(Zone)); Assert.True(walk.World.Contains(Zone));
        Assert.Contains(Zone, service.CommitPrepared()); Assert.False(walk.World.Contains(Zone));
        release.Set(); await service.Prepare(Zone).WaitAsync(Watchdog, TestContext.Current.CancellationToken);
        _ = service.Rebuild(Zone); // generation 2 is prepared but must never be committed after this change
        await service.Prepare(Zone).WaitAsync(Watchdog, TestContext.Current.CancellationToken); service.CommitPrepared();
        await rebuilding.WaitAsync(Watchdog, TestContext.Current.CancellationToken); Assert.Equal(3, walk.World.Find(Zone)!.Vertices[0].Y);
    }

    [Fact]
    public async Task KeptRegionsAreExpandedOnlyByTheCommitter()
    {
        var walk = new NavmeshWalkability((_, _) => 0);
        using var service = new NavMeshService(walk, (_, _) => new(Mesh(0, kept: false), NavMeshOrigin.Built));
        await service.Prepare(Zone).WaitAsync(Watchdog, TestContext.Current.CancellationToken); service.CommitPrepared();
        Assert.Equal(0, walk.World.Find(Zone)!.PolygonCount);
        long oldIncarnation = service.Incarnation;
        service.NewIncarnation(requeuePending: false);
        Assert.False(service.KeepAt(new(50, 0, 50), oldIncarnation));
        service.CommitPrepared(); Assert.Equal(0, walk.World.Find(Zone)!.PolygonCount);
        Assert.True(service.KeepAt(new(50, 0, 50)));
        Assert.Equal(0, walk.World.Find(Zone)!.PolygonCount);
        service.CommitPrepared(); Assert.Equal(1, walk.World.Find(Zone)!.PolygonCount);
    }
}
