using System.Globalization;
using Meitou.Simulation;

namespace Meitou.Networking.VNext;

/// <summary>
/// One independently revisioned definition or reading. A shared definition version binds the two parts before a visual becomes usable.
/// </summary>
internal sealed record ModVisualPart(ModEffectView? Effect, ModGlowView? Glow, bool Definition, ulong DefinitionVersion)
{
    /// <summary>
    /// The authority's logical identity, independent of this part's key.
    /// </summary>
    internal long Id => Effect?.Id ?? Glow?.Id ?? throw new InvalidDataException("Missing shared visual family.");

    /// <summary>
    /// Lowercase keys describe static definitions; uppercase keys describe their dynamic readings.
    /// </summary>
    internal string Key => (Effect is not null ? Definition ? "e:" : "E:" : Definition ? "g:" : "G:")
        + Id.ToString(CultureInfo.InvariantCulture);
}

/// <summary>
/// Separates immutable anchors and names from changing colour and lifetime without depending on a prior dynamic reading.
/// All state is supplied by the recipient-owned publisher or replica; the schema instance itself holds no connection state.
/// </summary>
internal sealed class ModVisualCollection : IGameCollection
{
    /// <summary>
    /// Two records describe each of the existing 65536 permitted logical visuals.
    /// </summary>
    internal const int MaximumKeys = 2 * 65536;

    /// <summary>
    /// The public snapshot field's fixed collection identity.
    /// </summary>
    public ushort Id => 1068;

    /// <summary>
    /// A definition changes version only when its exact static bytes change. Renewals therefore repeat neither anchors nor names.
    /// </summary>
    public (CollectionManifest Manifest, IReadOnlyList<(string Key, byte[] Value)> Items) Capture(WorldViewCodec codec,
        PlayerWorldView view, IReadOnlyDictionary<string, byte[]>? previous)
    {
        var visuals = view.Snapshot.ModVisuals;
        int count = checked(visuals.Effects.Length + visuals.Glows.Length);
        if (count > 65536) throw new InvalidDataException("Shared visual collection exceeds its logical identity bound.");
        var result = new List<(string Key, byte[] Value)>(2 * count);
        foreach (var effect in visuals.Effects) Add(effect, null, previous, result);
        foreach (var glow in visuals.Glows) Add(null, glow, previous, result);
        var keys = result.Select(pair => pair.Key).ToArray();
        _ = ValidateManifest(keys);
        return (new(true, true, keys), result);
    }

    /// <summary>
    /// Emits adjacent definition/reading candidates. The comparison cache may omit the definition while its version is unchanged.
    /// </summary>
    static void Add(ModEffectView? effect, ModGlowView? glow, IReadOnlyDictionary<string, byte[]>? previous,
        List<(string Key, byte[] Value)> result)
    {
        string key = new ModVisualPart(effect, glow, true, 0).Key;
        byte[] definition = ModVisualCodec.Encode(effect, glow, true, 0);
        byte[]? old = previous?.GetValueOrDefault(key);
        ulong version;
        if (old is not null && ModVisualCodec.SameDefinition(definition, old))
        {
            version = ModVisualCodec.DefinitionVersion(old);
            definition = old;
        }
        else
        {
            version = old is null ? 1 : checked(ModVisualCodec.DefinitionVersion(old) + 1);
            ModVisualCodec.SetDefinitionVersion(definition, version);
        }
        result.Add((key, definition));
        result.Add((Partner(key), ModVisualCodec.Encode(effect, glow, false, version)));
    }

    /// <summary>
    /// A part's family, logical identity and role must match the collection envelope.
    /// </summary>
    public object Decode(WorldViewCodec codec, string key, byte[] value)
    {
        var part = ModVisualCodec.Decode(value);
        if (part.Key != key) throw new InvalidDataException("Shared visual part does not match its key.");
        return part;
    }

    /// <summary>
    /// Joins only matching definitions/readings. A definition change is packed with its reading in one publication, so a mismatch is invalid.
    /// </summary>
    public PlayerWorldView Build(PlayerWorldView view, GameCollectionState state)
    {
        var effects = new List<ModEffectView>();
        var glows = new List<ModGlowView>();
        foreach (string key in state.Manifest.Keys)
        {
            if (!IsDefinition(key) || state.Items.GetValueOrDefault(key).Value is not ModVisualPart definition
                || state.Items.GetValueOrDefault(Partner(key)).Value is not ModVisualPart reading) continue;
            if (!definition.Definition || reading.Definition || definition.DefinitionVersion != reading.DefinitionVersion)
                throw new InvalidDataException("Shared visual definition and reading do not match.");
            if (definition.Effect is { } effect && reading.Effect is { } effectState)
                effects.Add(effect with { Tint = effectState.Tint, StartedAt = effectState.StartedAt, ExpiresAt = effectState.ExpiresAt });
            else if (definition.Glow is { } glow && reading.Glow is { } glowState)
                glows.Add(glow with { Colour = glowState.Colour, Strength = glowState.Strength,
                    StartedAt = glowState.StartedAt, ExpiresAt = glowState.ExpiresAt });
            else throw new InvalidDataException("Shared visual part families do not match.");
        }
        return view with { Snapshot = view.Snapshot with { ModVisuals = new(effects.ToArray(), glows.ToArray()) } };
    }

    /// <summary>
    /// Only complete logical identities enter active membership; one missing part cannot silently halve the public count bound.
    /// </summary>
    internal static IReadOnlySet<string> ValidateManifest(IReadOnlyList<string> keys)
    {
        if (keys.Count > MaximumKeys || (keys.Count & 1) != 0) throw new InvalidDataException("Invalid shared visual membership count.");
        var members = keys.ToHashSet(StringComparer.Ordinal);
        if (members.Count != keys.Count) throw new InvalidDataException("Duplicate shared visual membership.");
        foreach (string key in keys)
            if (!members.Contains(Partner(key))) throw new InvalidDataException("Incomplete shared visual membership pair.");
        return members;
    }

    /// <summary>
    /// Checks canonical positive int64 identity text and names the other part without admitting arbitrary pending keys.
    /// </summary>
    internal static string Partner(string key)
    {
        if (key.Length < 3 || key[1] != ':' || key[0] is not ('e' or 'E' or 'g' or 'G')
            || !long.TryParse(key.AsSpan(2), NumberStyles.None, CultureInfo.InvariantCulture, out long id) || id <= 0
            || id.ToString(CultureInfo.InvariantCulture) != key[2..])
            throw new InvalidDataException("Invalid shared visual part key.");
        return (char)(key[0] ^ 32) + key[1..];
    }

    /// <summary>
    /// The two lowercase prefixes identify definitions, never dynamic readings.
    /// </summary>
    internal static bool IsDefinition(string key) => key.Length > 0 && key[0] is 'e' or 'g';
}
