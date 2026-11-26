using System.Collections.Immutable;
using System.Text;
using System.Text.Json.Serialization;
using Meitou.Content;

namespace Meitou.Simulation;

/// <summary>
/// One code mod's presentation for a single recipient. Small text segments keep arbitrary Unicode inside the wire codec's per-string bound.
/// </summary>
public sealed record PublishedModView(string ModId, ImmutableArray<string> Segments)
{
    /// <summary>
    /// The previously joined payload, retained while the recipient reads this publication.
    /// </summary>
    string? _text;

    /// <summary>
    /// The mod's complete detached text.
    /// </summary>
    [JsonIgnore]
    public string Text => _text ??= string.Concat(Segments);
}

/// <summary>
/// Latest recipient-private UI data from trusted simulation systems. A new world starts with no publications.
/// </summary>
public sealed class PlayerModViews
{
    /// <summary>
    /// Maximum content bytes per mod and recipient.
    /// </summary>
    public const int MaximumBytes = 65536;

    /// <summary>
    /// Small enough that even fully escaped UTF-16 fits a single wire string.
    /// </summary>
    const int SegmentLength = 8192;

    /// <summary>
    /// Detached collections shared with captures until their next replacement.
    /// </summary>
    readonly Dictionary<PlayerId, ImmutableArray<PublishedModView>> _recipients = [];

    /// <summary>
    /// Replaces one recipient's copy on the simulation owner. An empty payload withdraws that mod's entry.
    /// </summary>
    public void Publish(PlayerId recipient, string modId, string payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        if (recipient.IsNone || !CodeModManifest.IsValidId(modId)) throw new ArgumentException("Invalid mod view address.");
        if (Encoding.UTF8.GetByteCount(payload) > MaximumBytes) throw new ArgumentOutOfRangeException(nameof(payload));

        var prior = _recipients.GetValueOrDefault(recipient, []);
        int index = 0;
        while (index < prior.Length && prior[index].ModId != modId) index++;
        bool found = index < prior.Length;
        if (payload.Length == 0)
        {
            if (found) _recipients[recipient] = prior.RemoveAt(index);
            return;
        }

        if (!found && prior.Length >= 16) throw new InvalidOperationException("Too many mod views for this player.");
        var parts = ImmutableArray.CreateBuilder<string>();
        for (int offset = 0; offset < payload.Length;)
        {
            int count = Math.Min(SegmentLength, payload.Length - offset);
            if (char.IsHighSurrogate(payload[offset + count - 1]) && offset + count < payload.Length) count--;
            parts.Add(payload.Substring(offset, count));
            offset += count;
        }

        var next = new PublishedModView(modId, parts.ToImmutable());
        _recipients[recipient] = found ? prior.SetItem(index, next) : prior.Add(next);
    }

    /// <summary>
    /// Captures only the requested recipient's publications.
    /// </summary>
    public IReadOnlyList<PublishedModView> Capture(PlayerId recipient) => _recipients.GetValueOrDefault(recipient, []);

    /// <summary>
    /// Checks wire bounds before accepting detached publications from the authority.
    /// </summary>
    public static bool Valid(IReadOnlyList<PublishedModView>? views)
    {
        if (views is null || views.Count > 16) return false;
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var view in views)
        {
            if (view?.ModId is null || !CodeModManifest.IsValidId(view.ModId) || !ids.Add(view.ModId)) return false;
            if (view.Segments.IsDefaultOrEmpty || view.Segments.Length > 9) return false;
            int bytes = 0;
            foreach (string segment in view.Segments)
            {
                if (segment is null || segment.Length is 0 or > SegmentLength) return false;
                bytes += Encoding.UTF8.GetByteCount(segment);
            }
            if (bytes > MaximumBytes) return false;
        }

        return true;
    }
}
