using System.Numerics;
using Meitou.Game.Host.Input;
using Meitou.Simulation;

namespace Meitou.Modding;

/// <summary>
/// Host-neutral reads for the mod frame; the shared contract does not expose a writable world.
/// </summary>
internal interface IModFrameHost
{
    /// <summary>
    /// The authorized presented world snapshot.
    /// </summary>
    WorldSnapshot Snapshot { get; }
    /// <summary>
    /// The next command tick.
    /// </summary>
    long Tick { get; }
    /// <summary>
    /// The currently selected characters.
    /// </summary>
    IReadOnlyList<CharacterId> Selected { get; }
    /// <summary>
    /// The inspected or selected character.
    /// </summary>
    CharacterId? Shown { get; }
    /// <summary>
    /// The currently pointed character.
    /// </summary>
    CharacterId? Hovered { get; }
    /// <summary>
    /// The pointer in screen pixels.
    /// </summary>
    Vector2 Pointer { get; }
    /// <summary>
    /// Whether either control key is held.
    /// </summary>
    bool Ctrl { get; }
    /// <summary>
    /// Whether either shift key is held.
    /// </summary>
    bool Shift { get; }
    /// <summary>
    /// Whether either alt key is held.
    /// </summary>
    bool Alt { get; }
    /// <summary>
    /// The current highlight modifier.
    /// </summary>
    bool Highlight { get; }
    /// <summary>
    /// The presented pause state.
    /// </summary>
    bool Paused { get; }
    /// <summary>
    /// The presented simulation speed.
    /// </summary>
    double Speed { get; }
    /// <summary>
    /// The screen size in pixels.
    /// </summary>
    Vector2 ScreenSize { get; }
    /// <summary>
    /// The interface scale.
    /// </summary>
    float UiScale { get; }
    /// <summary>
    /// Whether the interface is currently visible.
    /// </summary>
    bool UiVisible { get; }
    /// <summary>
    /// The presented camera eye.
    /// </summary>
    Vector3 CameraEye { get; }
    /// <summary>
    /// The presented camera target.
    /// </summary>
    Vector3 CameraTarget { get; }
    /// <summary>
    /// The open game screens in display order.
    /// </summary>
    IReadOnlyList<string> OpenScreens { get; }
    /// <summary>
    /// Projects a world point to screen pixels.
    /// </summary>
    Vector2? Project(Vector3 point);
    /// <summary>
    /// Whether the named screen is open.
    /// </summary>
    bool IsOpen(string id);
    /// <summary>
    /// The current bindings of an action.
    /// </summary>
    IReadOnlyList<Binding> Bindings(InputAction action);
    /// <summary>
    /// Moves the local camera to a world X/Z point.
    /// </summary>
    bool FocusCamera(Vector2 worldPos) => false;
    /// <summary>
    /// Selects one locally controllable presented character.
    /// </summary>
    bool SelectCharacter(CharacterId character) => false;
    /// <summary>
    /// Shows a local interface notice.
    /// </summary>
    void Notify(string text, bool error);
}

/// <summary>
/// What the host shows this frame, for a code mod's host-side code (docs/modding.md "Frame"): the snapshot on screen, the selection, the
/// character under the pointer, the modifiers held, the game speed, the screen, the camera, the projection of world points, the open screens and
/// the player's bindings, read when asked; and the interface's notice line. Given to <see cref="ModContext.AddFrame"/> callbacks once a frame
/// and to key presses (<see cref="ModKeyContext.Frame"/>). Host thread only, valid during the call that got it: a tick may be in flight, so a
/// mod must not touch the world, and keeps copies, not the frame.
/// </summary>
public sealed class ModFrame
{
    /// <summary>
    /// The host the reads come from.
    /// </summary>
    readonly IModFrameHost _host;

    /// <summary>
    /// A frame of <paramref name="host"/>'s interface.
    /// </summary>
    internal ModFrame(IModFrameHost host) => _host = host;

    /// <summary>
    /// The world snapshot on screen.
    /// </summary>
    public WorldSnapshot Snapshot => _host.Snapshot;

    /// <summary>
    /// The tick the next command is due at.
    /// </summary>
    public long Tick => _host.Tick;

    /// <summary>
    /// The selected characters, the first the lead.
    /// </summary>
    public IReadOnlyList<CharacterId> Selected => _host.Selected;

    /// <summary>
    /// The character the HUD shows: an inspected one, else the first selected; null without either.
    /// </summary>
    public CharacterId? Shown => _host.Shown;

    /// <summary>
    /// The character under the pointer as the last pick found it; null for none.
    /// </summary>
    public CharacterId? Hovered => _host.Hovered;

    /// <summary>
    /// The pointer in screen pixels.
    /// </summary>
    public Vector2 Pointer => _host.Pointer;

    /// <summary>
    /// Either Ctrl key is held now.
    /// </summary>
    public bool Ctrl => _host.Ctrl;

    /// <summary>
    /// Either Shift key is held now.
    /// </summary>
    public bool Shift => _host.Shift;

    /// <summary>
    /// Either Alt key is held now.
    /// </summary>
    public bool Alt => _host.Alt;

    /// <summary>
    /// The Highlight key (Alt by default) as the interface last saw it: what lets an order steal or pick a lock and turns the pointer over a downed
    /// character into the loot one.
    /// </summary>
    public bool Highlight => _host.Highlight;

    /// <summary>
    /// Whether the game is paused.
    /// </summary>
    public bool Paused => _host.Paused;

    /// <summary>
    /// The game speed (0 while paused).
    /// </summary>
    public double Speed => _host.Speed;

    /// <summary>
    /// The screen's size in pixels.
    /// </summary>
    public Vector2 ScreenSize => _host.ScreenSize;

    /// <summary>
    /// The interface's scale: the screen's height over the 1080 lines the layouts are made for, times the player's interface scale.
    /// </summary>
    public float UiScale => _host.UiScale;

    /// <summary>
    /// Whether the interface shows (not hidden by its key, a photo mode or the title).
    /// </summary>
    public bool UiVisible => _host.UiVisible;

    /// <summary>
    /// The camera's eye in the world.
    /// </summary>
    public Vector3 CameraEye => _host.CameraEye;

    /// <summary>
    /// The point the camera looks at.
    /// </summary>
    public Vector3 CameraTarget => _host.CameraTarget;

    /// <summary>
    /// Places the local strategy camera over a finite world X/Z point, preserving angle and distance.
    /// </summary>
    public bool FocusCamera(Vector2 worldPos) => _host.FocusCamera(worldPos);

    /// <summary>
    /// Requests selection of a visible character controlled by the local player through the native selection path.
    /// </summary>
    public bool SelectCharacter(CharacterId character) => _host.SelectCharacter(character);

    /// <summary>
    /// The screen pixel of world point <paramref name="point"/> as the last frame was drawn; null behind the camera.
    /// </summary>
    public Vector2? Project(Vector3 point) => _host.Project(point);

    /// <summary>
    /// The game screens open now by their layout ids (<c>inventory</c>, <c>overview</c>, <c>build</c>, <c>editor</c>, <c>trader</c>...), the
    /// front one last.
    /// </summary>
    public IReadOnlyList<string> OpenScreens => _host.OpenScreens;

    /// <summary>
    /// Whether game screen <paramref name="id"/> is open.
    /// </summary>
    public bool IsOpen(string id) => _host.IsOpen(id);

    /// <summary>
    /// The player's keys and buttons for a game action (or a mod's key action), as the Controls page binds them.
    /// </summary>
    public IReadOnlyList<Binding> Bindings(InputAction action) => _host.Bindings(action);

    /// <summary>
    /// Shows <paramref name="text"/> on the interface's notice line, as the game's own notices (a message of the game's catalog translated;
    /// <paramref name="error"/>: the error tone).
    /// </summary>
    public void Notify(string text, bool error = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        _host.Notify(text, error);
    }

}
