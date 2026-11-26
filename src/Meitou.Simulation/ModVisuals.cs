using System.Numerics;
using Meitou.Data.Characters;
using Meitou.Modding;

namespace Meitou.Simulation;

/// <summary>
/// A server-owned effect request. Times are simulation seconds, identity is stable across renewals, and particle pools stay on the client.
/// </summary>
public sealed record ModEffectView(long Id, string Owner, string Effect, string System, EffectAnchor Anchor, Vector3? Tint,
    double StartedAt, double ExpiresAt, bool Kept);

/// <summary>
/// One target's server-owned glow. The client derives its fade from the authority's start and expiry times.
/// </summary>
public sealed record ModGlowView(long Id, string Owner, GlowTarget Target, Vector3 Colour, float Strength,
    double StartedAt, double ExpiresAt, GlowMode Mode = GlowMode.Light, bool ThroughWalls = false);

/// <summary>
/// A server-owned ribbon request; recipients sample their current authorized anchor without historical trajectory data.
/// </summary>
public sealed record ModTrailView(long Id, string Owner, EffectAnchor Anchor, TrailStyle Style, double StartedAt, double ExpiresAt);

/// <summary>
/// One authority owner's audio controls; an identity reading preserves its claim over the same owner's local setting.
/// </summary>
public sealed record ModAudioView(long Id, string Owner, AudioModulation Modulation);

/// <summary>
/// Immutable presentation requests, filtered by each player's published characters, buildings and observed region before transmission.
/// </summary>
public sealed record ModVisualSnapshot(ModEffectView[] Effects, ModGlowView[] Glows)
{
    /// <summary>
    /// No shared visual requests.
    /// </summary>
    public static readonly ModVisualSnapshot Empty = new([], []);
    /// <summary>
    /// Ribbon requests sampled on the recipient.
    /// </summary>
    public ModTrailView[] Trails { get; init; } = [];
    /// <summary>
    /// Current authority audio controls, including explicit clears.
    /// </summary>
    public ModAudioView[] Audio { get; init; } = [];
}

/// <summary>
/// Authoritative code-mod visuals. Only requests and expiry changes are published; the renderer advances particles, bones and fades locally.
/// </summary>
public sealed class ModVisuals
{
    #region State

    /// <summary>
    /// Retains a finished effect through the existing presentation event history window so short bursts reach the reliable publisher.
    /// </summary>
    const double RetentionSeconds = 3;
    /// <summary>
    /// The upstream mod API's maximum requested duration.
    /// </summary>
    const float MaxSeconds = 30;
    /// <summary>
    /// The upstream glow's fade-in and fade-out duration.
    /// </summary>
    const float FadeSeconds = 0.25f;
    /// <summary>
    /// Guards host callbacks and publication without sharing mutable arrays.
    /// </summary>
    readonly object _gate = new();
    /// <summary>
    /// Active or retained one-shot and kept effects, keyed by stable server identity.
    /// </summary>
    readonly Dictionary<long, ModEffectView> _effects = [];
    /// <summary>
    /// The mod API's renewal key for kept effects.
    /// </summary>
    readonly Dictionary<(string Owner, string Effect, EffectAnchor Anchor), long> _kept = [];
    /// <summary>
    /// One glow per target, with the most recent owner allowed to stop it.
    /// </summary>
    readonly Dictionary<GlowTarget, ModGlowView> _glows = [];
    /// <summary>
    /// One kept ribbon per owner and scale-independent anchor.
    /// </summary>
    readonly Dictionary<(string Owner, EffectAnchor Anchor), ModTrailView> _trails = [];
    /// <summary>
    /// Stable audio identity per loaded authority mod.
    /// </summary>
    readonly Dictionary<string, ModAudioView> _audio = new(StringComparer.Ordinal);
    /// <summary>
    /// Monotonic identity across effects and glows in this world.
    /// </summary>
    long _sequence;
    /// <summary>
    /// Last observed simulation time, for clearing transient effects when a clock is restored backwards.
    /// </summary>
    double _lastTime;
    /// <summary>
    /// Earliest possible expiry; repeated mod renewals in one tick do not rescan every retained request.
    /// </summary>
    double _nextExpiry = double.PositiveInfinity;
    /// <summary>
    /// The immutable view reused until requests or expiration change it.
    /// </summary>
    ModVisualSnapshot? _published;

    #endregion

    #region Effects

    /// <summary>
    /// Queues one accepted server-mod effect or renews its kept key without changing its network identity.
    /// </summary>
    public bool Play(World world, string owner, string effect, string system, in EffectAnchor anchor, Vector3? tint, float seconds, bool kept)
    {
        if (!Name(owner, 256) || !Name(effect, 4096) || !Name(system, 4096) || !ValidAnchor(anchor)
            || !Duration(seconds) || tint is { } colour && !Colour(colour)) return false;
        if (anchor.Kind is EffectAnchorKind.Bone or EffectAnchorKind.Weapon && !world.Characters.IsAlive(anchor.Character)) return false;

        lock (_gate)
        {
            double now = Advance(world);
            var key = (owner, effect, anchor.Key);
            if (kept && _kept.TryGetValue(key, out long oldId) && _effects.TryGetValue(oldId, out var old) && old.ExpiresAt > now)
            {
                _effects[oldId] = old with { Tint = tint, Anchor = anchor, ExpiresAt = now + seconds };
            }
            else
            {
                long id = checked(++_sequence);
                _effects[id] = new(id, owner, effect, system, anchor, tint, now, now + seconds, kept);
                if (kept) _kept[key] = id;
            }

            _nextExpiry = Math.Min(_nextExpiry, now + seconds + RetentionSeconds);
            _published = null;
            return true;
        }
    }

    /// <summary>
    /// Stops the named kept effect; its final expiry remains available for reliable publication.
    /// </summary>
    public bool StopEffect(World world, string owner, string effect, in EffectAnchor anchor)
    {
        lock (_gate)
        {
            double now = Advance(world);
            if (!_kept.Remove((owner, effect, anchor.Key), out long id) || !_effects.TryGetValue(id, out var previous)) return false;

            _effects[id] = previous with { ExpiresAt = Math.Min(previous.ExpiresAt, now) };
            _nextExpiry = Math.Min(_nextExpiry, now + RetentionSeconds);
            _published = null;
            return true;
        }
    }

    #endregion

    #region Glows

    /// <summary>
    /// Sets or renews one target's glow, preserving its current fade when a new request replaces it.
    /// </summary>
    public bool Glow(World world, string owner, in GlowTarget target, Vector3 colour, float seconds, float strength,
        GlowMode mode = GlowMode.Light, bool throughWalls = false)
    {
        if (!Name(owner, 256) || !ValidTarget(target) || !world.Characters.IsAlive(target.On) || !Colour(colour)
            || !Duration(seconds) || !ValidGlow(mode, target, colour, strength, throughWalls)) return false;

        lock (_gate)
        {
            double now = Advance(world);
            bool replaces = _glows.TryGetValue(target, out var old);
            double shown = replaces ? FadeSeconds * Envelope(old!, now) : 0;
            long id = replaces ? old!.Id : checked(++_sequence);
            _glows[target] = new(id, owner, target, colour, strength, now - shown, now + seconds, mode, throughWalls);
            _nextExpiry = Math.Min(_nextExpiry, now + seconds + RetentionSeconds);
            _published = null;
            return true;
        }
    }

    /// <summary>
    /// Fades out only the requesting mod's glow, as the upstream API's StopGlow does.
    /// </summary>
    public bool StopGlow(World world, string owner, in GlowTarget target)
    {
        lock (_gate)
        {
            double now = Advance(world);
            if (!_glows.TryGetValue(target, out var previous) || previous.Owner != owner || previous.ExpiresAt <= now) return false;

            _glows[target] = previous with { ExpiresAt = Math.Min(previous.ExpiresAt, now + FadeSeconds * Envelope(previous, now)) };
            _nextExpiry = Math.Min(_nextExpiry, _glows[target].ExpiresAt + RetentionSeconds);
            _published = null;
            return true;
        }
    }

    /// <summary>
    /// The current fraction of a glow's requested intensity.
    /// </summary>
    static double Envelope(ModGlowView glow, double now) => Math.Clamp(Math.Min(now - glow.StartedAt, glow.ExpiresAt - now) / FadeSeconds, 0, 1);

    #endregion

    #region Trails and audio

    /// <summary>
    /// Renews a ribbon's scale and style while retaining its identity through its locally fading tail.
    /// </summary>
    public bool KeepTrail(World world, string owner, in EffectAnchor anchor, TrailStyle style, float seconds)
    {
        if (!Name(owner, 256) || !ValidAnchor(anchor) || !ValidTrail(style) || !Duration(seconds)) return false;
        if (anchor.Kind is EffectAnchorKind.Bone or EffectAnchorKind.Weapon && !world.Characters.IsAlive(anchor.Character)) return false;

        lock (_gate)
        {
            double now = Advance(world);
            var key = (owner, anchor.Key);
            bool renew = _trails.TryGetValue(key, out var old) && old.ExpiresAt + old.Style.Seconds > now;
            long id = renew ? old!.Id : checked(++_sequence);
            _trails[key] = new(id, owner, anchor, style, renew ? old!.StartedAt : now, now + seconds);
            _nextExpiry = Math.Min(_nextExpiry, now + seconds + Math.Max(RetentionSeconds, style.Seconds));
            _published = null;
            return true;
        }
    }

    /// <summary>
    /// Stops a ribbon taking new samples while retaining its existing tail and final reading.
    /// </summary>
    public bool StopTrail(World world, string owner, in EffectAnchor anchor)
    {
        lock (_gate)
        {
            double now = Advance(world);
            var key = (owner, anchor.Key);
            if (!_trails.TryGetValue(key, out var old) || old.ExpiresAt <= now) return false;

            _trails[key] = old with { ExpiresAt = now };
            _nextExpiry = Math.Min(_nextExpiry, now + Math.Max(RetentionSeconds, old.Style.Seconds));
            _published = null;
            return true;
        }
    }

    /// <summary>
    /// Stores bounded controls. Identity clears the contribution while retaining authority over that owner.
    /// </summary>
    public void SetAudio(World world, string owner, AudioModulation modulation)
    {
        if (!Name(owner, 256)) return;
        modulation = modulation.Clamped();
        lock (_gate)
        {
            Advance(world);
            long id = _audio.TryGetValue(owner, out var old) ? old.Id : checked(++_sequence);
            _audio[owner] = new(id, owner, modulation);
            _published = null;
        }
    }

    /// <summary>
    /// Seeds a ready world's Initialize-time default without replacing an explicit value already restored into that candidate.
    /// </summary>
    public void InitializeAudio(World world, string owner, AudioModulation modulation)
    {
        lock (_gate)
            if (!_audio.ContainsKey(owner)) SetAudio(world, owner, modulation);
    }

    #endregion

    #region Publication

    /// <summary>
    /// Captures immutable views; unchanged requests do not accumulate per-frame position, fade or lifetime updates.
    /// </summary>
    public ModVisualSnapshot Capture(World world)
    {
        lock (_gate)
        {
            Advance(world);
            return _published ??= _effects.Count == 0 && _glows.Count == 0 && _trails.Count == 0 && _audio.Count == 0
                ? ModVisualSnapshot.Empty
                : new([.. _effects.Values.OrderBy(effect => effect.Id)], [.. _glows.Values.OrderBy(glow => glow.Id)])
                {
                    Trails = [.. _trails.Values.OrderBy(trail => trail.Id)],
                    Audio = [.. _audio.Values.OrderBy(audio => audio.Id)],
                };
        }
    }

    /// <summary>
    /// Removes a failed or unloaded mod's remaining visual requests.
    /// </summary>
    public void RemoveOwner(string owner)
    {
        lock (_gate)
        {
            foreach (var effect in _effects.Values.Where(effect => effect.Owner == owner).ToArray()) Remove(effect);
            foreach (var glow in _glows.Values.Where(glow => glow.Owner == owner).ToArray()) _glows.Remove(glow.Target);
            foreach (var key in _trails.Keys.Where(key => key.Owner == owner).ToArray()) _trails.Remove(key);
            if (_audio.TryGetValue(owner, out var audio)) _audio[owner] = audio with { Modulation = AudioModulation.Identity };
            _published = null;
        }
    }

    /// <summary>
    /// Expires finished requests and handles a backwards clock without reusing identities.
    /// </summary>
    double Advance(World world)
    {
        double now = world.Tick * (double)world.TickSeconds;
        if (now < _lastTime)
        {
            _effects.Clear();
            _kept.Clear();
            _glows.Clear();
            _trails.Clear();
            foreach (string owner in _audio.Keys.ToArray()) _audio[owner] = _audio[owner] with { Modulation = AudioModulation.Identity };
            _nextExpiry = double.PositiveInfinity;
            _published = null;
        }

        _lastTime = now;
        if (now < _nextExpiry) return now;

        foreach (var effect in _effects.Values.Where(effect => now >= effect.ExpiresAt + RetentionSeconds).ToArray()) Remove(effect);
        foreach (var glow in _glows.Values.Where(glow => now >= glow.ExpiresAt + RetentionSeconds).ToArray())
        {
            _glows.Remove(glow.Target);
            _published = null;
        }

        foreach (var key in _trails.Where(pair => now >= pair.Value.ExpiresAt + Math.Max(RetentionSeconds, pair.Value.Style.Seconds))
            .Select(pair => pair.Key).ToArray())
        {
            _trails.Remove(key);
            _published = null;
        }

        _nextExpiry = double.PositiveInfinity;
        foreach (var trail in _trails.Values) _nextExpiry = Math.Min(_nextExpiry, trail.ExpiresAt + Math.Max(RetentionSeconds, trail.Style.Seconds));
        foreach (var effect in _effects.Values) _nextExpiry = Math.Min(_nextExpiry, effect.ExpiresAt + RetentionSeconds);
        foreach (var glow in _glows.Values) _nextExpiry = Math.Min(_nextExpiry, glow.ExpiresAt + RetentionSeconds);
        return now;
    }

    /// <summary>
    /// Removes an effect and only its own renewal entry; a newer effect may already own the same kept key.
    /// </summary>
    void Remove(ModEffectView effect)
    {
        _effects.Remove(effect.Id);
        var key = (effect.Owner, effect.Effect, effect.Anchor.Key);
        if (effect.Kept && _kept.GetValueOrDefault(key) == effect.Id) _kept.Remove(key);
        _published = null;
    }

    #endregion

    #region Validation

    /// <summary>
    /// The public anchor's bounded identity, finite transform and supported scale.
    /// </summary>
    public static bool ValidAnchor(in EffectAnchor anchor) => Enum.IsDefined(anchor.Kind) && Finite(anchor.Offset)
        && (anchor.Turn is not { } turn || Finite(turn) && turn.LengthSquared() > 1e-8f)
        && (anchor.Stretch is not { } stretch || stretch is > 0 and <= 100)
        && (anchor.Size is not { } size || size is > 0 and <= 100)
        && anchor.PulseAmount is >= 0 and < 1 && float.IsFinite(anchor.PulseSeconds)
        && (anchor.PulseAmount == 0 || anchor.PulseSeconds is >= 0.05f and <= 60)
        && (anchor.Kind != EffectAnchorKind.Projectile || anchor.ProjectileId >= 0)
        && (anchor.Kind is not (EffectAnchorKind.Bone or EffectAnchorKind.Weapon) || !anchor.Character.IsNone)
        && (anchor.Name is null or "" || Name(anchor.Name, 4096))
        && (anchor.Kind is not (EffectAnchorKind.Bone or EffectAnchorKind.Building) || Name(anchor.Name, 4096));

    /// <summary>
    /// A supported glow target with a nonempty character identity and a valid worn slot when required.
    /// </summary>
    public static bool ValidTarget(in GlowTarget target) => Enum.IsDefined(target.Kind) && !target.On.IsNone
        && (target.Kind != GlowTargetKind.Worn || Enum.IsDefined(target.Slot));

    /// <summary>
    /// The upstream light/tint ranges; only whole-character tints may show through walls.
    /// </summary>
    public static bool ValidGlow(GlowMode mode, in GlowTarget target, Vector3 colour, float strength, bool throughWalls) =>
        Enum.IsDefined(mode) && Colour(colour) && strength is >= 0 and <= 4
        && (mode != GlowMode.Tint || colour.X <= 1 && colour.Y <= 1 && colour.Z <= 1 && strength <= 1)
        && (!throughWalls || mode == GlowMode.Tint && target.Kind == GlowTargetKind.Character);

    /// <summary>
    /// Exact style bounds of the upstream ribbon API, with bounded portable material identities.
    /// </summary>
    public static bool ValidTrail(TrailStyle? style) => style is not null && style.Span is >= 0 and <= 1000
        && style.Width is > 0 and <= 1000 && (style.EndWidth is not { } width || width is >= 0 and <= 1000)
        && style.Seconds is > 0 and <= 5 && Colour4(style.Colour) && (style.EndColour is not { } end || Colour4(end))
        && (style.Material is null || Name(style.Material, 4096));

    /// <summary>
    /// Finite ribbon RGB and opacity in the upstream ranges.
    /// </summary>
    static bool Colour4(Vector4 colour) => Colour(new(colour.X, colour.Y, colour.Z)) && colour.W is >= 0 and <= 1;

    /// <summary>
    /// A bounded nonempty textual identity that the exact UTF-8 wire representation can preserve.
    /// </summary>
    static bool Name(string? text, int max)
    {
        if (text is not { Length: > 0 } || text.Length > max) return false;
        for (int i = 0; i < text.Length; i++)
        {
            if (!char.IsSurrogate(text[i])) continue;
            if (!char.IsHighSurrogate(text[i]) || i + 1 >= text.Length || !char.IsLowSurrogate(text[i + 1])) return false;
            i++;
        }
        return true;
    }
    /// <summary>
    /// A supported finite visual lifetime.
    /// </summary>
    static bool Duration(float seconds) => seconds is > 0 and <= MaxSeconds;
    /// <summary>
    /// Every component is finite.
    /// </summary>
    static bool Finite(Vector3 point) => float.IsFinite(point.X) && float.IsFinite(point.Y) && float.IsFinite(point.Z);
    /// <summary>
    /// Every quaternion component is finite.
    /// </summary>
    static bool Finite(Quaternion turn) => float.IsFinite(turn.X) && float.IsFinite(turn.Y) && float.IsFinite(turn.Z) && float.IsFinite(turn.W);
    /// <summary>
    /// The upstream effect and glow RGB range, including full black.
    /// </summary>
    static bool Colour(Vector3 colour) => colour.X is >= 0 and <= 4 && colour.Y is >= 0 and <= 4 && colour.Z is >= 0 and <= 4;

    #endregion
}
