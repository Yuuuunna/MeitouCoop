using System.Text.Json;
using Meitou.Content;
using Meitou.Data.World;
using Meitou.Networking.VNext;
using Meitou.Runtime;
using Meitou.Server;
using Meitou.Simulation;
using Meitou.Simulation.Movement;

namespace Meitou.Tests.Engine;

public sealed class InstalledAnimationExportTests(ITestOutputHelper output)
{
    [Fact, Slow]
    public async Task RestoredSceneReportsExactPlaybackChangeReasonsWithoutNetworkQueues()
    {
        var install = GameInstall.Locate(); Assert.SkipWhen(install is null, "No Kenshi install configured.");
        var source = Environment.GetEnvironmentVariable("MEITOU_QUIC_ACCEPTANCE_SERVER_DATA");
        Assert.SkipWhen(source is null, "Set MEITOU_QUIC_ACCEPTANCE_SERVER_DATA to an original pre-cutover server-data directory.");
        string target = Path.Combine(Path.GetTempPath(), "Meitou.AnimationExport-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(target);
        try
        {
            string original = Path.Combine(source!, "world");
            foreach (var file in Directory.EnumerateFiles(original, "*", SearchOption.AllDirectories))
            {
                string copy = Path.Combine(target, Path.GetRelativePath(original, file)); Directory.CreateDirectory(Path.GetDirectoryName(copy)!); File.Copy(file, copy);
            }
            using var host = HeadlessWorld.Open(install!, new WorldSettings { Threads = 1, PublishSnapshots = false }, new StandardSystemOptions { SynchronousPaths = true });
            var store = new CheckpointStore(target); var saved = store.LoadCurrent(); Assert.NotNull(saved);
            var restored = host.RestoreCheckpoint(host.CreateCheckpointCoordinator(store), saved!, deferPlayerRoles: true); Assert.Null(restored.CleanupFailure);
            host.Runtime.Speed = 0; host.Population.Relations.PlayerFaction = -1;
            var player = host.World.Players.All.First(p => host.World.Players.KnownRoles(p.Id).Any());
            var role = host.World.Players.KnownRoles(player.Id).First();
            var location = host.World.Players.LocateRole(player.Id, role.Entity); Assert.NotNull(location); Assert.NotNull(location.Position);
            var at = location.Position.Value; var centre = WorldLayout.ZoneOf(at.X, at.Z);
            var zones = (from z in Enumerable.Range(-1,3) from x in Enumerable.Range(-1,3) let zone = new ZoneCoordinate(centre.X+x,centre.Y+z) where zone.IsInsideGrid select zone).ToArray();
            using var lease = host.World.Regions.Retain(zones, simulation: true);
            host.Runtime.Synchronize(); await Task.WhenAll(zones.Select(host.Content.Navigation!.PrepareZone));
            for (int i = 0; i < 100; i++)
            {
                host.Runtime.Synchronize(); bool ready = host.World.PreparePopulation(at);
                host.World.StoredPlayerRoles?.Restore(role.Entity, p => host.Content.Navigation!.IsReady(WorldLayout.ZoneOf(p.X, p.Z)));
                if (ready && host.World.Entities.TryResolve(role.Entity, out _)) break;
                await Task.Delay(10, TestContext.Current.CancellationToken);
            }
            var system = Assert.Single(host.World.Systems.OfType<AnimationSystem>());
            var exporter = new AuthorityAnimationExporter(diagnostics: true);
            var timelines = new Dictionary<ulong, AnimationTimeline>();
            var loadingTimelines = new Dictionary<ulong, AnimationTimeline>();
            var descriptors = new AnimationDescriptorCatalog(); var receivedDescriptors = new AnimationDescriptorCatalog(); var announced = new HashSet<ushort>();
            long compactBytes = 0, descriptorBytes = 0;
            long bytes = 0, emissions = 0, samples = 0; float maximumPhaseError = 0, maximumWeightError = 0;
            host.Runtime.Speed = 1;
            for (int step = 0; step < 300; step++)
            {
                host.Runtime.Step(publish:false);
                if (step % 3 != 0) continue;
                var ids = Enumerable.Range(0, host.World.Characters.HighWater).Select(host.World.Characters.IdOf).Where(host.World.Characters.IsAlive).ToArray();
                foreach (var pair in AnimationPlaybackExport.Capture(host.World, ids))
                {
                    ulong entity = host.World.Entities.Track(pair.Key).Value;
                    var fence = new AnimationTimelineFence(1, 1, entity, 1);
                    if (!timelines.TryGetValue(entity, out var timeline)) timelines[entity] = timeline = new(fence);
                    if (exporter.Capture(entity, pair.Value, (ulong)host.World.Tick, host.World.TickSeconds) is { } state)
                    {
                        byte[] compact = CompactAnimationPlaybackCodec.Encode(state, descriptors, out var used); compactBytes += compact.Length;
                        foreach (ushort descriptor in used.Distinct()) if (announced.Add(descriptor))
                        { byte[] definition = descriptors.EncodeDefinition(descriptor); descriptorBytes += definition.Length + 19; receivedDescriptors.ApplyDefinition(definition); }
                        var decoded = CompactAnimationPlaybackCodec.Decode(compact, receivedDescriptors);
                        Assert.Equal(AnimationPlaybackCodec.Encode(state), AnimationPlaybackCodec.Encode(decoded));
                        if (!loadingTimelines.TryGetValue(entity, out var loadingTimeline)) loadingTimelines[entity] = loadingTimeline = new(fence);
                        loadingTimeline.AdvanceTo(Math.Max(0, host.World.Tick - 3));
                        Assert.Equal(AnimationApplyResult.Applied, loadingTimeline.Apply(fence, decoded));
                        timeline.Apply(fence, decoded); bytes += AnimationPlaybackCodec.Encode(state).Length; emissions++;
                    }
                    var expected = system.Publish(host.World.Characters.Cold(pair.Key.Slot)!.Animation!, []);
                    var actual = timeline.Sample(host.World.Tick); Assert.Equal(expected.Count, actual.Count);
                    for (int i = 0; i < expected.Count; i++)
                    {
                        Assert.Equal(expected[i].Name, actual[i].Name);
                        float error = MathF.Abs(expected[i].Time - actual[i].Time);
                        if (expected[i].Length > 0) error = MathF.Abs(MathF.IEEERemainder(expected[i].Time - actual[i].Time, expected[i].Length));
                        maximumPhaseError = MathF.Max(maximumPhaseError, error);
                        maximumWeightError = MathF.Max(maximumWeightError, MathF.Abs(expected[i].Weight - actual[i].Weight));
                        if (error > .00011f) output.WriteLine(JsonSerializer.Serialize(new { Tick = host.World.Tick, Layer = i, Definition = expected[i].Definition, Length = expected[i].Length, ExpectedTime = expected[i].Time, ActualTime = actual[i].Time, Raw = pair.Value.Layers[i] with { Name = "" } }));
                        Assert.InRange(error, 0, .00011f); Assert.InRange(MathF.Abs(expected[i].Weight - actual[i].Weight), 0, .00011f);
                    }
                    samples++;
                }
            }
            foreach (var pair in loadingTimelines) Assert.Equal(timelines[pair.Key].Sample(host.World.Tick), pair.Value.Sample(host.World.Tick));
            output.WriteLine(JsonSerializer.Serialize(new { Entities = timelines.Count, Samples = samples, Emissions = emissions, Bytes = bytes, CompactBytes = compactBytes, DescriptorBytes = descriptorBytes, Descriptors = descriptors.Count, MaximumPhaseError = maximumPhaseError, MaximumWeightError = maximumWeightError, Diagnostics = exporter.Diagnostics }));
            Assert.True(timelines.Count >= 100);
        }
        finally { Directory.Delete(target, recursive: true); }
    }
}
