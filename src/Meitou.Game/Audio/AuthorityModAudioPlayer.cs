using Meitou.Modding;

namespace Meitou.Game.Audio;

/// <summary>
/// Composes authority and local mod controls on every audio backend, independently of particle and character rendering.
/// </summary>
sealed class AuthorityModAudioPlayer(GameHost host)
{
    /// <summary>
    /// Receives authority controls when this client has no loaded code mods.
    /// </summary>
    readonly ModAudioModulations _fallback = new();
    /// <summary>
    /// The registry carrying this frame's authority owners and the client's local inputs.
    /// </summary>
    ModAudioModulations? _registry;
    /// <summary>
    /// The device last given the composed controls, including settings unchanged across a device restart.
    /// </summary>
    AudioSystem? _device;

    /// <summary>
    /// Applies the current authorized view or clears it at the title and between worlds; other owners' local controls still compose.
    /// </summary>
    internal void Update(ModAudioModulations? local)
    {
        var registry = local ?? _fallback;
        var device = host.Sound.Audio;
        if (!ReferenceEquals(_registry, registry) || !ReferenceEquals(_device, device))
        {
            if (!ReferenceEquals(_registry, registry))
            {
                _registry?.Attach(null);
                _registry?.ApplyAuthority([]);
            }
            _device?.Player.SetModAudio(null);
            _registry = registry;
            _device = device;
            registry.Attach(device is null ? null : device.Player.SetModAudio);
        }
        registry.ApplyAuthority(host.Player is { TitleOpen: false } && host.Session is { HasWorldView: true } session
            ? session.CurrentSnapshot.ModVisuals.Audio : []);
    }
}
