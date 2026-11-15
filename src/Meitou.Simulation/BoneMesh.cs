using System.Numerics;
using System.Text;

namespace Meitou.Simulation;

/// <summary>
/// A rigid mesh a code mod hangs on one of a character's bones (a saddle on a mount; docs/modding.md "Bone meshes"): <paramref name="Mesh"/>'s
/// origin sits at <paramref name="Bone"/> as the renderer poses the character in the drawn frame, moved by <paramref name="Offset"/> and turned by
/// <paramref name="Rotation"/> in the bone's frame; the offset and the mesh scale with the character's size. <paramref name="Key"/> names it among
/// the character's bone meshes (<see cref="CharacterCold.SetBoneMesh"/>). Display only: the simulation reads none of it.
/// </summary>
public readonly record struct BoneMesh(string Key, string Mesh, string Bone, Vector3 Offset, Quaternion Rotation)
{
    /// <summary>
    /// Per-character count and UTF-8 string limits shared by mod setters and multiplayer snapshots.
    /// </summary>
    public const int MaxPerCharacter = 64, MaxKeyBytes = 128, MaxMeshBytes = 1024, MaxBoneBytes = 256;

    static readonly UTF8Encoding TextEncoding = new(false, true);

    /// <summary>
    /// Refuses attachment state that cannot be published to multiplayer clients.
    /// </summary>
    internal void Validate()
    {
        if (string.IsNullOrEmpty(Key) || TextEncoding.GetByteCount(Key) > MaxKeyBytes
            || string.IsNullOrEmpty(Mesh) || TextEncoding.GetByteCount(Mesh) > MaxMeshBytes
            || string.IsNullOrEmpty(Bone) || TextEncoding.GetByteCount(Bone) > MaxBoneBytes)
            throw new ArgumentException("A bone mesh requires nonempty key, mesh and bone names within the shared snapshot limits.");
        if (!float.IsFinite(Offset.X) || !float.IsFinite(Offset.Y) || !float.IsFinite(Offset.Z)
            || !float.IsFinite(Rotation.X) || !float.IsFinite(Rotation.Y) || !float.IsFinite(Rotation.Z) || !float.IsFinite(Rotation.W))
            throw new ArgumentException("A bone mesh's transform must be finite.");
    }
}
