using Meitou.Data.Gameplay;
using Meitou.Simulation.Movement;

namespace Meitou.Simulation;

/// <summary>
/// Detached raw animation envelopes; no network identity or normalized snapshot weights.
/// </summary>
public sealed record AnimationPlaybackCapture(IReadOnlyList<AnimationPlaybackCaptureLayer> Layers);
public readonly record struct AnimationPlaybackCaptureLayer(string Name, int Definition, float Length,
    float Time, float Rate, bool Loop, float End, float Weight, float Target, float BlendSeconds,
    AnimationArea Area, bool Wanted, bool ConsumesLower, bool Predictable);

public static class AnimationPlaybackExport
{
    /// <summary>Until a character has driver state, WorldPublish exposes its literal hot-state pose.
    /// Those final weights remain frozen until its first driver update; overlays avoid normalizing twice.</summary>
    public static IReadOnlyDictionary<CharacterId, AnimationPlaybackCapture> Capture(World world, WorldSnapshot authorizedSnapshot)
    {
        var system = world.Systems.OfType<AnimationSystem>().FirstOrDefault();
        return authorizedSnapshot.Characters.ToDictionary(c => c.Id, c =>
            system is not null && world.Characters.IsAlive(c.Id) && world.Characters.Cold(c.Id.Slot)?.Animation is { } animation
            ? Capture(system, animation) : Frozen(c.Animations));
    }

    static AnimationPlaybackCapture Frozen(IReadOnlyList<AnimationLayer> layers) => new(
            layers.Select(l => new AnimationPlaybackCaptureLayer(l.Name, l.Definition,
                MathF.Max(.000001f, MathF.Max(l.Length, l.Time)), l.Time, 0, false, l.Time,
                l.Weight, l.Weight, 0, AnimationArea.Overlay, true, false, true)).ToArray());

    /// <summary>
    /// Owner-thread capture after a committed tick. The caller supplies only authorized IDs.
    /// </summary>
    public static IReadOnlyDictionary<CharacterId, AnimationPlaybackCapture> Capture(World world, IEnumerable<CharacterId> authorized)
    {
        var system = world.Systems.OfType<AnimationSystem>().FirstOrDefault();
        var result = new Dictionary<CharacterId, AnimationPlaybackCapture>();
        if (system is null) return result;
        foreach (var id in authorized)
        {
            if (!world.Characters.IsAlive(id) || world.Characters.Cold(id.Slot)?.Animation is not { } animation) continue;
            result.Add(id, Capture(system, animation));
        }
        return result;
    }

    public static AnimationPlaybackCapture Capture(AnimationSystem system, CharacterAnimation animation)
    {
        var layers = new AnimationPlaybackCaptureLayer[animation.Count];
        for (int i = 0; i < layers.Length; i++)
        {
            var layer = animation.Layers[i];
            var definition = system.Library.Definitions[layer.Definition];
            float target = layer.BlendSpeed > 0 ? layer.Target : layer.Weight;
            float length = layer.Length > 0 ? layer.Length : system.Lengths.Of(definition.Clip);
            if (!(length > 0)) throw new InvalidDataException("Animation clip length is unavailable.");
            layers[i] = new(definition.Name, layer.Definition, length, layer.PlaybackLoops ? layer.Time - MathF.Floor(layer.Time / length) * length : layer.Time,
                layer.PlaybackPredictable ? layer.PlaybackRate : 0, layer.PlaybackLoops, layer.PlaybackEnd,
                layer.Weight, target, layer.BlendSpeed > 0 ? MathF.Abs(target - layer.Weight) / layer.BlendSpeed : 0,
                definition.Area, layer.Target > 0, definition.Area == AnimationArea.All || definition.IsAction && !definition.Technique,
                layer.PlaybackPredictable);
        }
        return new(layers);
    }
}