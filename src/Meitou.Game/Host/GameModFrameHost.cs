using System.Numerics;
using Meitou.Game.Host.Input;
using Meitou.Modding;
using Meitou.Simulation;

namespace Meitou.Game.Host;

/// <summary>
/// Reads the client's authorized presentation and interface without giving shared mod contracts a mutable authority world.
/// </summary>
sealed class GameModFrameHost(GameHost host) : IModFrameHost
{
    /// <summary>
    /// The current local interface.
    /// </summary>
    PlayerInterface Ui => host.Player;

    /// <inheritdoc/>
    public WorldSnapshot Snapshot => host.Session.CurrentSnapshot;
    /// <inheritdoc/>
    public long Tick => host.Session.CommandTick;
    /// <inheritdoc/>
    public IReadOnlyList<CharacterId> Selected => Ui.SelectedIds();
    /// <inheritdoc/>
    public CharacterId? Shown => Ui.Lead()?.Id;
    /// <inheritdoc/>
    public CharacterId? Hovered => Ui.PointerPanel.PointedTarget;
    /// <inheritdoc/>
    public Vector2 Pointer => Ui.Mouse;
    /// <inheritdoc/>
    public bool Ctrl => Held(Key.LeftControl, Key.RightControl);
    /// <inheritdoc/>
    public bool Shift => Held(Key.LeftShift, Key.RightShift);
    /// <inheritdoc/>
    public bool Alt => Held(Key.LeftAlt, Key.RightAlt);
    /// <inheritdoc/>
    public bool Highlight => Ui.AltHeld;
    /// <inheritdoc/>
    public bool Paused => host.Session.IsPaused;
    /// <inheritdoc/>
    public double Speed => host.Session.TimeScale;
    /// <inheritdoc/>
    public Vector2 ScreenSize => new(Ui.Width, Ui.Height);
    /// <inheritdoc/>
    public float UiScale => Ui.UiScale;
    /// <inheritdoc/>
    public bool UiVisible => Ui.UiVisible && !Ui.TitleOpen;
    /// <inheritdoc/>
    public Vector3 CameraEye => host.Session.Camera.Current.Eye;
    /// <inheritdoc/>
    public Vector3 CameraTarget => host.Session.Camera.Current.Target;
    /// <inheritdoc/>
    public bool FocusCamera(Vector2 worldPos) => Ui.FocusCamera(worldPos);
    /// <inheritdoc/>
    public bool SelectCharacter(CharacterId character) => Ui.SelectModCharacter(character);
    /// <inheritdoc/>
    public Vector2? Project(Vector3 point) => Ui.Project(point);
    /// <inheritdoc/>
    public IReadOnlyList<string> OpenScreens => [.. Ui.FaithfulScreens.OpenIds];
    /// <inheritdoc/>
    public bool IsOpen(string id) => Ui.FaithfulScreens.IsOpen(id);
    /// <inheritdoc/>
    public IReadOnlyList<Binding> Bindings(InputAction action) => host.Session.Bindings.Get(action);
    /// <inheritdoc/>
    public void Notify(string text, bool error) => Ui.Notice(text, error);

    /// <summary>
    /// Whether either key of a modifier pair is held.
    /// </summary>
    bool Held(Key left, Key right) => host.Session.Input.IsHeld(left) || host.Session.Input.IsHeld(right);
}
