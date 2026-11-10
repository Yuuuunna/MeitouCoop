using Meitou.Content;
using Meitou.Data.World;
using Meitou.Runtime;
using Meitou.Server;
using Meitou.Simulation;
using Meitou.Simulation.Locks;
namespace Meitou.Tests.Engine;

public sealed class InstalledHeldPlacementTests(ITestOutputHelper output)
{
    [Fact, Slow]
    public async Task PreCutoverCheckpointPrisonersResumeWithoutChangingTaskDuringPlacement()
    {
        var install = GameInstall.Locate(); Assert.SkipWhen(install is null, "No Kenshi install configured.");
        var source = Environment.GetEnvironmentVariable("MEITOU_QUIC_ACCEPTANCE_SERVER_DATA");
        Assert.SkipWhen(source is null, "Set MEITOU_QUIC_ACCEPTANCE_SERVER_DATA to an original pre-cutover server-data directory.");
        string target = Path.Combine(Path.GetTempPath(), "Meitou.HeldPlacement-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(target);
        try
        {
            string original = Path.Combine(source!, "world");
            foreach (var file in Directory.EnumerateFiles(original, "*", SearchOption.AllDirectories))
            {
                string copy = Path.Combine(target, Path.GetRelativePath(original, file)); Directory.CreateDirectory(Path.GetDirectoryName(copy)!); File.Copy(file, copy);
            }
            using var host = HeadlessWorld.Open(install!, new WorldSettings { Threads = 1, PublishSnapshots = false }, new StandardSystemOptions { SynchronousPaths = true });
            var store = new CheckpointStore(target); var saved = store.LoadCurrent(); Assert.NotNull(saved);
            var restored = host.RestoreCheckpoint(host.CreateCheckpointCoordinator(store), saved!); Assert.Null(restored.CleanupFailure);
            host.Runtime.Speed = 0; host.Population.Relations.PlayerFaction = -1;
            var player = host.World.Players.All.First(p => host.World.Players.KnownRoles(p.Id).Any());
            var role = host.World.Players.KnownRoles(player.Id).First();
            var location = host.World.Players.LocateRole(player.Id, role.Entity); Assert.NotNull(location); Assert.NotNull(location.Position);
            var at = location.Position.Value; var centre = WorldLayout.ZoneOf(at.X, at.Z);
            var zones = (from z in Enumerable.Range(-1,3) from x in Enumerable.Range(-1,3) let zone = new ZoneCoordinate(centre.X+x,centre.Y+z) where zone.IsInsideGrid select zone).ToArray();
            using var lease = host.World.Regions.Retain(zones, simulation: true);
            host.Runtime.Synchronize(); await Task.WhenAll(zones.Select(host.Content.Navigation!.PrepareZone));
            for (int i = 0; i < 100; i++) { host.Runtime.Synchronize(); if (host.World.PreparePopulation(at)) break; await Task.Delay(10, TestContext.Current.CancellationToken); }
            var cages = host.World.Fixtures.All.Where(f => f.Kind == FixtureKind.Cage && host.World.Characters.TryResolveNext(f.Occupant,out _)).ToArray();
            var corrections = cages.Where(f => host.World.Characters.Current[f.Occupant.Slot].Position != f.Position || host.World.Characters.Current[f.Occupant.Slot].Yaw != f.Yaw).ToArray();
            output.WriteLine($"Loaded={host.World.Characters.HighWater}; occupied cages={cages.Length}; placement corrections={corrections.Length}");
            Assert.NotEmpty(corrections);
            Assert.Contains(corrections, f => host.World.Characters.Current[f.Occupant.Slot].Task == (byte)CharacterTask.Follow);
            var placements = corrections.ToDictionary(f => f.Occupant, f => host.World.Characters.Current[f.Occupant.Slot]);
            var tasks = corrections.ToDictionary(f => f.Occupant, f => host.World.Characters.Current[f.Occupant.Slot].Task);
            host.World.Fixtures.HoldCaged(host.World);
            Assert.All(tasks, p => Assert.Equal(p.Value, host.World.Characters.Current[p.Key.Slot].Task));
            // Put the original saved placements back so the first real tick exercises the same correction path.
            foreach (var p in placements) host.World.Characters.Reposition(p.Key.Slot, p.Value.Position, p.Value.Yaw);
            host.Runtime.Step(publish:false);
            Assert.Equal(1,host.World.Tick);
        }
        finally { Directory.Delete(target, recursive:true); }
    }
}
