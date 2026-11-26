using System.Numerics;
using Meitou.Data.Particles;
using Meitou.Modding;
using Meitou.Simulation;

namespace Meitou.Game.Effects;

/// <summary>
/// Presents the authority's sparse mod requests. Stable identities prevent replay, snapshots renew emission lifetimes, and particle state,
/// anchor transforms and glow fades are derived on this client. A missing client code-mod entry does not suppress server visuals.
/// </summary>
sealed class AuthorityModVisualPlayer(GameHost host, ModEffectPlayer player)
{
    /// <summary>
    /// One authoritative request's locally playing unit, if its anchor and particle system have become available.
    /// </summary>
    sealed class Playing(ModEffectView view)
    {
        /// <summary>
        /// The last authoritative request, including renewal and tint changes.
        /// </summary>
        public ModEffectView View = view;
        /// <summary>
        /// The local particle unit; null before start or while a live anchor is unavailable.
        /// </summary>
        public EffectUnit? Unit;
        /// <summary>
        /// The one-shot has already emitted; it must not restart when its retained request is seen again.
        /// </summary>
        public bool Started;
    }

    /// <summary>
    /// Effects in the latest recipient projection, keyed by the authority's identity.
    /// </summary>
    readonly Dictionary<long, Playing> _effects = [];
    /// <summary>
    /// Every authority unit still alive, including tails retired by a tint change or natural request expiry.
    /// </summary>
    readonly Dictionary<EffectUnit, long> _units = [];
    /// <summary>
    /// Requests withdrawn before their natural retention ends; all their units disappear together.
    /// </summary>
    readonly HashSet<long> _withdrawn = [];
    /// <summary>
    /// Completed or withdrawn units removed after walking the ledger.
    /// </summary>
    readonly List<EffectUnit> _finishedUnits = [];
    /// <summary>
    /// Ribbons sampled separately from client-local UI requests.
    /// </summary>
    readonly ModTrails _trails = new();
    /// <summary>
    /// The last accepted ribbon identity and anchor.
    /// </summary>
    readonly Dictionary<long, ModTrailView> _trailViews = [];
    /// <summary>
    /// Reused output combined with the caller's local ribbons.
    /// </summary>
    readonly List<TrailQuad> _quads = [];
    /// <summary>
    /// One-shots already shown or skipped as baseline history, retained through the server's short history window.
    /// </summary>
    readonly Dictionary<long, double> _seen = [];
    /// <summary>
    /// Reused identity set for removing effects that leave this recipient's projection.
    /// </summary>
    readonly HashSet<long> _present = [];
    /// <summary>
    /// Session identity; a new world must not reuse another world's requests or character handles.
    /// </summary>
    object? _world;
    /// <summary>
    /// The remote projection generation; re-entry takes another baseline without replaying retained expired bursts.
    /// </summary>
    ulong _epoch;

    /// <summary>
    /// Follows, renews and starts authority effects on the render thread, within the upstream renderer's start and active-emitter capacities.
    /// </summary>
    public void UpdateEffects()
    {
        var session = host.Session;
        if (session is not { HasWorldView: true })
        {
            Suspend();
            return;
        }
        ulong epoch = session is Host.RemotePlayerSession remote ? remote.PresentationEpoch : 0;
        bool baseline = !ReferenceEquals(_world, session.WorldIdentity) || _epoch != epoch;
        if (baseline) Reset(session.WorldIdentity, epoch);
        double now = Math.Max(0, session.PresentationTick) * session.SimulationStepSeconds;
        var snapshot = session.CurrentSnapshot.ModVisuals;
        _present.Clear();
        _withdrawn.Clear();
        foreach (var view in snapshot.Effects)
        {
            _present.Add(view.Id);
            if (!_effects.TryGetValue(view.Id, out var effect))
            {
                bool expiredHistory = baseline && view.ExpiresAt <= now;
                effect = new Playing(view) { Started = expiredHistory || !view.Kept && _seen.ContainsKey(view.Id) };
                _effects.Add(view.Id, effect);
                if (expiredHistory) _seen[view.Id] = view.ExpiresAt + 3;
            }
            Update(effect, view, now);
        }
        foreach (long id in _effects.Keys.Where(id => !_present.Contains(id)).ToArray())
        {
            var effect = _effects[id];
            if (now < effect.View.ExpiresAt + 3) _withdrawn.Add(id);
            Stop(effect);
            _effects.Remove(id);
        }
        RetireUnits();
        foreach (long id in _seen.Where(pair => pair.Value < now).Select(pair => pair.Key).ToArray()) _seen.Remove(id);
        StartWaiting(now);
    }

    /// <summary>
    /// Carries renewal and tint changes into a live emitter and follows non-point anchors until emission ends.
    /// </summary>
    void Update(Playing effect, ModEffectView view, double now)
    {
        if (effect.Unit is { } unit)
        {
            if (effect.View.Tint != view.Tint || effect.View.System != view.System || unit.Stopped && view.ExpiresAt > now)
            {
                Stop(effect);
                effect.Started = false;
            }
            else if (view.ExpiresAt > now)
            {
                unit.Life = (float)(view.ExpiresAt - now);
                var request = Request(view, unit.Life) with { Age = Math.Max(0, now - view.StartedAt) };
                if ((!request.Fixed || effect.View.Anchor != view.Anchor) && !player.Follow(unit, request))
                {
                    Stop(effect);
                    effect.Started = false;
                }
            }
            else unit.Life = 0;
        }
        effect.View = view;
    }

    /// <summary>
    /// Starts newly arrived requests, including the living tail of a late one-shot. A kept effect restarts after its anchor returns.
    /// </summary>
    void StartWaiting(double now)
    {
        int alive = _effects.Values.Count(effect => effect.Unit is { Stopped: false });
        int started = 0;
        foreach (var effect in _effects.Values)
        {
            if (started >= ModEffects.MaxPerFrame || alive >= ModEffects.MaxAlive) return;
            if (effect.Unit is not null || effect.Started && !effect.View.Kept || effect.View.StartedAt > now) continue;
            if (effect.View.Kept && effect.View.ExpiresAt <= now) continue;

            if (player.StartAuthority(effect.View, now) is not { } unit) continue;
            effect.Unit = unit;
            _units[unit] = effect.View.Id;
            effect.Started = true;
            _seen[effect.View.Id] = effect.View.ExpiresAt + 3;
            started++;
            if (!unit.Stopped) alive++;
        }
    }

    /// <summary>
    /// The existing renderer's anchor-following input for a shared request.
    /// </summary>
    static ModEffectRequest Request(ModEffectView view, float seconds) =>
        new(view.Owner, view.Effect, view.System, view.Anchor, view.Tint, seconds);

    /// <summary>
    /// Stops emission while letting existing particles live out in their renderer group.
    /// </summary>
    static void Stop(Playing effect)
    {
        if (effect.Unit is { } unit) unit.Life = 0;
        effect.Unit = null;
    }

    /// <summary>
    /// Drops the previous world's handles and deduplication history.
    /// </summary>
    void Reset(object? world, ulong epoch)
    {
        foreach (var unit in _units.Keys) Discard(unit);
        _units.Clear();
        _withdrawn.Clear();
        _finishedUnits.Clear();
        _trails.Clear();
        _trailViews.Clear();
        _effects.Clear();
        _seen.Clear();
        _world = world;
        _epoch = epoch;
    }

    /// <summary>
    /// Withdraws the current view and stops its emitters before a replacement baseline is available.
    /// </summary>
    public void Suspend() => Reset(null, 0);

    /// <summary>
    /// Adds authority glows after the caller clears the renderer table; local interface glows may overlay them afterwards.
    /// </summary>
    public void ShowGlows()
    {
        if (host.Session is not { HasWorldView: true } session || host.Gpu?.Characters is not { } crowd) return;
        double now = Math.Max(0, session.PresentationTick) * session.SimulationStepSeconds;
        foreach (var glow in session.CurrentSnapshot.ModVisuals.Glows)
        {
            float envelope = (float)Math.Clamp(Math.Min(now - glow.StartedAt, glow.ExpiresAt - now) / ModGlows.FadeSeconds, 0, 1);
            if (envelope <= 0) continue;
            long key = host.DrawKey(glow.Target.On);
            crowd.SetGlow(key, glow.Target, glow.Colour, glow.Strength * envelope, glow.Mode);
            if (glow.ThroughWalls)
                host.Gpu.ThroughKeys.Add((key, new Vector4(glow.Colour, Math.Clamp(glow.Strength * envelope, 0, 1))));
        }
    }

    /// <summary>
    /// Drops completed tails every frame and all variants of an early-withdrawn request.
    /// </summary>
    void RetireUnits()
    {
        _finishedUnits.Clear();
        foreach (var (unit, request) in _units)
        {
            if (_withdrawn.Contains(request)) Discard(unit);
            if (unit.Done) _finishedUnits.Add(unit);
        }
        foreach (var unit in _finishedUnits) _units.Remove(unit);
    }

    /// <summary>
    /// Ends emission and all existing particles before the renderer's next update.
    /// </summary>
    static void Discard(EffectUnit unit)
    {
        unit.Life = 0;
        unit.Stopped = true;
        unit.Deactivate();
    }

    /// <summary>
    /// Samples authorized current anchors. Membership withdrawal drops their complete local ribbon immediately.
    /// </summary>
    public void UpdateTrails(float seconds, List<TrailQuad> into)
    {
        if (host.Session is not { HasWorldView: true } session) return;
        double now = Math.Max(0, session.PresentationTick) * session.SimulationStepSeconds;
        _present.Clear();
        foreach (var trail in session.CurrentSnapshot.ModVisuals.Trails)
        {
            _present.Add(trail.Id);
            string owner = trail.Id.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (_trailViews.TryGetValue(trail.Id, out var previous) && previous.Anchor.Key != trail.Anchor.Key) _trails.Drop(owner);
            _trailViews[trail.Id] = trail;
            if (trail.StartedAt > now) continue;
            if (trail.ExpiresAt <= now) _trails.Stop(owner, trail.Anchor);
            else _trails.KeepAuthority(owner, trail.Anchor, trail.Style, (float)Math.Min(30, trail.ExpiresAt - now),
                Math.Max(0, now - trail.StartedAt - seconds));
        }
        foreach (long id in _trailViews.Keys.Where(id => !_present.Contains(id)).ToArray())
        {
            _trails.Drop(id.ToString(System.Globalization.CultureInfo.InvariantCulture));
            _trailViews.Remove(id);
        }
        _trails.Advance(seconds, player, _quads);
        into.AddRange(_quads);
    }
}
