using System.Runtime.CompilerServices;
using System.Security.Cryptography;

namespace Meitou.Simulation.Items;

/// <summary>
/// Bounded identity for a code-mod item tag on the network. The authoritative item and save retain the complete original tag.
/// </summary>
public static class ItemTagIdentity
{
    /// <summary>
    /// The first 128 bits of SHA-256 identify a tag alongside the inventory revision and actor grant; the full tag stays on the authority.
    /// </summary>
    const int DigestBytes = 16;

    /// <summary>
    /// The fixed hexadecimal wire length, independent of a mod's tag length.
    /// </summary>
    public const int DigestCharacters = DigestBytes * 2;

    /// <summary>
    /// A computed digest, cached by the immutable string object without retaining tags after their items and snapshots disappear.
    /// </summary>
    sealed record DigestValue(string Value);

    /// <summary>
    /// Computing an unchanged long tag again for every recipient is unnecessary.
    /// </summary>
    static readonly ConditionalWeakTable<string, DigestValue> Digests = new();

    /// <summary>
    /// Empty for an untagged item; otherwise exactly 32 hexadecimal characters, independent of the mod's tag length.
    /// </summary>
    public static string Digest(string tag) => tag.Length == 0 ? "" : Digests.GetValue(tag, Compute).Value;

    /// <summary>
    /// Hashes UTF-16 code units without a replacement fallback, preserving distinct strings even if a mod supplies an unpaired surrogate.
    /// </summary>
    static DigestValue Compute(string tag)
    {
        var bytes = new byte[checked(tag.Length * 2)];
        for (int i = 0; i < tag.Length; i++)
        {
            bytes[2 * i] = (byte)tag[i];
            bytes[2 * i + 1] = (byte)(tag[i] >> 8);
        }
        return new(Convert.ToHexString(SHA256.HashData(bytes).AsSpan(0, DigestBytes)));
    }
}
