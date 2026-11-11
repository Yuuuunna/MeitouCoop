using Meitou.Networking;
using Meitou.Networking.VNext;
using Meitou.Simulation;
using Meitou.Simulation.Factions;
namespace Meitou.Tests.Engine;

internal sealed class GameReplicationFixture(WorldViewCodec codec)
{
    internal GameStatePublisher Server { get; } = new(codec, id => ((ulong)((uint)id.Slot + 1) << 32) | (uint)id.Generation, 11, 22);
    internal GameStateReplica Client { get; } = new(codec, 11, 22);
    internal GameStatePublication Capture(PlayerWorldView view) => Server.Capture(view with { Snapshot = view.Snapshot with { Characters = view.Snapshot.Characters.Select(c => c with { Animations = [] }).ToArray() } });
    internal void Apply(GameStatePublication publication) { foreach (var frame in publication.Frames) Client.Apply(frame); }
    internal static bool Changes(GameStatePublication publication, string field) => publication.ComponentBytes.Keys.Any(id => GameStatePublisher.ComponentName(id) == field);
}
