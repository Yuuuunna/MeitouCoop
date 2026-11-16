using Meitou.Data.Characters;
using Meitou.Simulation;

namespace Meitou.Networking.VNext;

/// <summary>
/// Recipient-owned ragdoll copies. Frozen or otherwise unchanged poses share a detached value across captures, never the mutable source.
/// </summary>
internal sealed class RagdollPoseCapture
{
    /// <summary>
    /// Only currently observed ragdolls are retained; source mutations cannot change these private comparison values.
    /// </summary>
    readonly Dictionary<CharacterId, BonePose> _poses = [];

    /// <summary>
    /// Rag dolls present in the current projection, used to release withdrawals and departed entities immediately after capture.
    /// </summary>
    readonly HashSet<CharacterId> _current = [];

    /// <summary>
    /// Called only on the authority owner; the returned projection and all its reused poses are read-only to encoding workers.
    /// </summary>
    public PlayerWorldView Detach(WorldViewCodec codec, PlayerWorldView view)
    {
        if (view.Snapshot.Characters.Count > 4096) throw new InvalidDataException("Recipient entity subscription exceeds protocol bound.");

        _current.Clear();
        var detached = codec.Detach(view, PoseOf);
        foreach (var id in _poses.Keys.Where(id => !_current.Contains(id)).ToArray()) _poses.Remove(id);
        return detached;
    }

    /// <summary>
    /// Reuses an owned value only after exact comparison; BonePose itself permits in-place mutation and reference identity is insufficient.
    /// </summary>
    BonePose? PoseOf(CharacterSnapshot character)
    {
        if (character.Ragdoll is not { } source) return null;
        if (!_current.Add(character.Id)) throw new InvalidDataException("Duplicate character ragdoll capture.");

        _poses.TryGetValue(character.Id, out var previous);
        if (RagdollPoseCodec.Same(previous, source)) return previous;

        var detached = RagdollPoseCodec.Copy(source)!;
        _poses[character.Id] = detached;
        return detached;
    }
}
