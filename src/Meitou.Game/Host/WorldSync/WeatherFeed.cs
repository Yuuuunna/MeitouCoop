using Meitou.Data.Particles;
using Meitou.Data.World;
using Meitou.Game.Rendering.Sky;
using Meitou.Simulation;
using Meitou.Runtime;

namespace Meitou.Game.Host.WorldSync;

/// <summary>
/// Copies the local runtime's or remote server's authoritative weather and effect nodes into presentation. Renderer weather never drives an
/// online simulation; the renderer-backed source is only a local harness fallback when no runtime weather system exists.
/// </summary>
sealed class WeatherFeed(GameHost host)
{
    /// <summary>
    /// The local harness fallback: region weather from the renderer scheduler and affecting particle units.
    /// </summary>
    internal sealed class HostWeather(Func<WorldWeather?> weather) : IWeatherSource
    {
        /// <summary>
        /// The live effect zones collected this frame (<see cref="UpdateWeatherSource"/>).
        /// </summary>
        public readonly List<WeatherAffectZone> ZoneList = [];
        /// <inheritdoc/>
        public IReadOnlyList<WeatherAffectZone> Zones => ZoneList;
        /// <summary>
        /// A weather every point gets instead of the schedule's (the <c>--scene acid</c> harness).
        /// </summary>
        public WeatherHere? Forced;

        /// <inheritdoc/>
        public WeatherHere? At(float x, float z)
        {
            if (Forced is { } forced) return forced;
            if (weather()?.World is not { } world) return null;
            var region = world.RegionAt(x, z);
            return new WeatherHere(region.Weather.AffectType, region.Strength, region.Def.AcidicGround, region.Def.AcidicWater,
                region.WindSpeed, region.Weather.Wetness);
        }
    }

    /// <summary>
    /// The optional local harness fallback, including a forced weather requested by a screenshot scene.
    /// </summary>
    internal HostWeather? WeatherSource;
    /// <summary>
    /// The runtime weather authority of the local world.
    /// </summary>
    internal SimulationWeather? SimulationWeather;
    /// <summary>
    /// The presentation source last accepted.
    /// </summary>
    object? _shownWorld;
    /// <summary>
    /// The last remote schedule accepted.
    /// </summary>
    WeatherFrame? _remoteFrame;
    /// <summary>
    /// The local authority tick last accepted.
    /// </summary>
    long _shownTick = -1;

    /// <summary>
    /// Accepts authoritative schedules and effect nodes once per frame, or updates the local harness fallback.
    /// </summary>
    internal void UpdateWeatherSource()
    {
        host.Gpu.AuthorityEffects = host.Session.WeatherEffects;
        host.Gpu.EffectContent = host.Scene.Database;
        host.Gpu.EffectWorldIdentity = host.Session.WorldIdentity;
        FollowWorld();
        if (host.Session.IsRemote) { AcceptRemote(); return; }
        SimulationWeather = host.LocalSystems.OfType<SimulationWeather>().FirstOrDefault();
        if (SimulationWeather is { } authority) { AcceptLocal(authority); return; }
        UpdateFallback();
    }

    /// <summary>
    /// Clears the previous world's presentation cursors and transient fallback zones.
    /// </summary>
    void FollowWorld()
    {
        if (ReferenceEquals(_shownWorld, host.Session.WorldIdentity)) return;
        _shownWorld = host.Session.WorldIdentity;
        _remoteFrame = null;
        _shownTick = -1;
        WeatherSource?.ZoneList.Clear();
        if (host.Session.IsRemote) host.ActiveWeather?.World.Snap();
    }

    /// <summary>
    /// Accepts a server schedule only when a new frame arrives.
    /// </summary>
    void AcceptRemote()
    {
        if (host.ActiveWeather is not { } presentation || host.Session.Weather is not { } frame || ReferenceEquals(_remoteFrame, frame)) return;
        presentation.AcceptRemoteAuthority(frame);
        _remoteFrame = frame;
    }

    /// <summary>
    /// Mirrors the runtime schedule; a forced harness weather is an explicit input to that local authority.
    /// </summary>
    void AcceptLocal(SimulationWeather authority)
    {
        authority.Forced = WeatherSource?.Forced;
        if (host.ActiveWeather is not { } view || _shownTick == host.Session.CommandTick) return;
        view.AcceptAuthority(authority.Schedule.World.Snapshot(), authority.Schedule.Time);
        _shownTick = host.Session.CommandTick;
    }

    /// <summary>
    /// Supplies only a local world without runtime weather, as used by synthetic screenshot scenes.
    /// </summary>
    void UpdateFallback()
    {
        WeatherSource ??= new HostWeather(() => host.Gpu.Weather);
        host.LocalSession.World.Weather = WeatherSource;
        WeatherSource.ZoneList.Clear();
        if (host.Gpu.Particles is not { } particles) return;
        Collect(particles.Groups);
        Collect(particles.PlacerGroups);
    }

    /// <summary>
    /// Collects live affecting units for the local harness; an effect without a radius contributes no zone.
    /// </summary>
    void Collect(IReadOnlyList<EffectGroup> groups)
    {
        foreach (var group in groups)
        {
            var effect = group.Effect;
            if (effect.AffectType == WeatherAffecting.None || !(effect.EffectRadius > 0)) continue;
            foreach (var unit in group.Units)
                if (!unit.Stopped)
                    WeatherSource!.ZoneList.Add(new WeatherAffectZone(unit.Position, effect.EffectRadius, effect.AffectType, effect.EffectStrength));
        }
    }
}
