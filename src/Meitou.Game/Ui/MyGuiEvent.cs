namespace Meitou.Game.Ui;

/// <summary>
/// A widget event of a screen: the widget, the action (its own, or select, accept, text), the text and the value; <paramref name="Press"/> marks a
/// legacy slider's event raised by the mouse button going down on it, not by a drag.
/// </summary>
public sealed record MyGuiEvent(string Screen, string Widget, string Action, string Text = "", float Value = 0, bool Press = false);

/// <summary>
/// The shared routing keys for code-mod text boxes; the graphical screen host owns editing and focus.
/// </summary>
internal static class UiTextInput
{
    /// <summary>
    /// The virtual screen that owns extension text boxes.
    /// </summary>
    public const string Screen = "ui-overlay";
    /// <summary>
    /// Marks a shared control as an editable overlay widget.
    /// </summary>
    public const string EditProperty = "OverlayEdit";
}
