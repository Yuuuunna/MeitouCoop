# Native UI and extensions

UI mode (--ui/Tab) is independent of graphics. Both modes use the installed MyGUI layouts,
skins and fonts for the in-game HUD, windows and Escape menu. Meitou customises the title,
scene transitions and settings; Faithful retains their original presentation where implemented.
Vulkan paints both, choice persisted. `Meitou.Ui` has no window/GPU/simulation dependency.
Host supplies snapshot text/availability and actions that enqueue commands. Extensions receive
read-only binding copies, never mutable world. [MyGUI](formats/mygui.md), [parity](faithful-ui-parity.md).

## Settings

`SettingsMenu` shares category/row geometry and input between both modes: Display, Graphics
(World/Lighting/Water/Effects), Interface, Controls and Advanced. Faithful uses MyGUI skins;
Meitou uses the [UI kit](meitou-ui.md)'s rounded rust plates and paper Save action. Tab or Options
opens it; Esc/the original Kenshi X/Save saves preferences and returns. Changes apply live, with the active
upscaler shown separately from the requested one. Terrain texture quality needs a restart.
Every Graphics sub-page starts with two rows (`SettingsMenu.Presets.cs`):
- **Quality preset** Low / Medium / High / Ultra sets the distance, foliage, grass, reflection, shadow, particle density,
  filtering, texture, ground-relief quality and light-shaft quality sliders; High is the start default (texture quality: full
  size). Distances that follow a switch's side (object distance, terrain error: Draw distances; shadow distance: Shadows) are
  multiples of that side's mark, so High reads High in either look. Low is `--low-end` (`WorldOptions.LowEnd*`, the same values):
  its distances, grass and particle density, texture quality, 1x filtering, shadows off, AO / anti-aliasing / light shafts / water /
  shadows Faithful, ground relief and its tessellation effects off, quarter-size particles, no anti-aliasing (not even FXAA) at 0.83
  render scale; what `--low-end` leaves alone stays at High. Its VSync off and material distance are not Tab panel settings and are
  not set. No preset turns a Meitou effect on; the others leave anti-aliasing and render scale alone. The shown preset is
  derived (sliders and the fifth cascade only; Custom otherwise); its name is saved (`graphicsPreset`) for the launcher, and a saved one the settings do not sit at is applied at start ([launcher](launcher.md#graphics)).
- **All graphics features** sets every Faithful / Meitou graphics switch to one side (gameplay switches and sliders
  untouched); mixed switches show neither side. A switch that does not take (indirect light without ray queries) is ignored.
  Advanced (**All gameplay changes**) and Fixes (**All fixes**) start with the same switch over their own page's
  switches and sliders with both marks (attackers per target); luckyren Mode is left out.
The modal pauses simulation and captures world input; wheel/PageUp/PageDown scroll, Tab/arrows
focus/adjust, Enter activates. Binding capture rejects conflicting/reserved controls; Esc cancels,
Delete unbinds. Defaults and cache deletion ask first. The viewer retains its debug panel.
Settings use 42px rows with a concise description for every setting, 14px labels,
13px values/buttons and 11px hints, with a 17px label-to-description offset;
paper section headers break up the dark plate. `FontPixels` specifies em pixels,
with 2×2 oversampling, kerning and fractional glyph positioning for small text and motion.
Wheel/PageUp/PageDown scrolling eases toward a clamped target independently of frame rate;
clicking stops the glide. Opening/closing takes 140/100ms, page changes 120ms, hover
brightening eases in/out over 160ms and dropdowns fade over 100ms. Hover keeps the raised
button artwork; the sunken surface indicates a press or selection.
Motion changes opacity/position, keeping type size fixed and input aligned.
Window art is a continuous surface baked at 2x the logical size;
only buttons use nine-slice scaling. The dialog is 1120×720 logical pixels; selection uses
pressed surfaces without yellow focus rings/underlines. Weather/time previews are omitted.
Graphics includes the Meitou switches under their related tabs; Advanced contains cache actions.
Pages omit descriptive blurbs; field hints explain the setting's effect and useful constraints.
The sidebar shows the build's nearest Git tag and short commit, embedded during compilation
(`BuildGitTag`/`BuildGitCommit` can override them for source archives).
Click a slider's number to type a value (selected initially); Enter/Tab or clicking outside
applies it, Esc cancels. Decimals (dot/comma), scientific notation, numpad, Ctrl+A and cursor/
selection editing are supported. Invalid/non-finite input leaves the setting unchanged;
valid input is clamped to the slider range and uses its existing rounding rules.

## Extension API

Provisional ApiVersion1: IUiExtension registered explicitly with UiHost.Register, returning
IDisposable. No automatic mod-DLL scanning/loading; incompatible versions/duplicate module IDs
or keys rejected. Keys namespace extension-id:local-name. Failed registration rolls back;
disposal removes factories/styles/overrides/actions/contributions; re-register supported.

RegisterControl builds UiNodes; RegisterStyle supplies state colours/alignment/skin/font/scale/
TextPadding UiInsets; OverrideStyle scoped replacement. RegisterAction receives read-only frame
context/string argument; disabled controls never call it. Contribute supplies ordered slot tree
with optional UI-mode restriction. OverrideControl can extend context.CreateBaseControl(id).
Source/test interfaces are authoritative for signatures; samples in Ui extension tests.

Builtin `meitou` actions drive the original controls in both modes. UiSlots:
hud.toolbar,hud.selection,hud.squad,hud.overlay; first3 map original regions in both modes,
overlay viewport. Equal-order later contribution draws above earlier; shared slot does not automatically
insert into builtin layout. Override factory or choose explicit child placement.

## Layout, input and lifetime

Panels/labels/buttons/compound children: pixel/percentage/offset/right-bottom placement,
absolute/row/column flow, padding/gaps. Parent disabled propagates; clip drawing/hits to parent/
slot. Labels pass input through; panels block world in region. F7 suppresses all UI mouse regions.
Registration/build/action on UI thread; fresh tree each UiFrameContext, no blocking callbacks.
Painter only receives UiControls, never calls module code. Contribution/action exceptions raise
UiHost.Diagnostic; failed contribution omitted atomically, others continue. Modules own resources
outside registration. HUD uses actual snapshot aggregates, shared portrait rendering and original
screen input in both modes, including I/C/M/T/Y shortcuts, map pan/zoom/orders and modal pause.
Inventory/stat interactions and current gaps are tracked in parity ledger.
The painter collects vertices into one upload/rendering and merges adjacent batches with matching
texture, sampling mode and scissor, retaining painter order. A scissor becomes the full viewport only
when all batch vertices lie inside it. Full GPU barriers/shared state occur at pass boundaries.
Font paths are cached; text geometry is keyed by caption/position/scale/colour and capped
per font at 512 entries/1 MiB, and dropped when the font's glyph atlas grows or restarts (its
generation). Fonts are FreeType glyph caches ([fonts](formats/mygui.md)); a text batch records
its atlas texture per run, so an atlas replaced mid-batch still draws correctly. Shape vertices are written in blocks. Opaque union area is reused
until its rectangle sequence changes. Standalone debug-overlay flushes still own their pass.
Inventory drags draw the cell's icon/count at its footprint size and original grab point,
above windows without intercepting drops; the source hides until release/cancellation.
Escape/right click cancels; changing the source snapshot or closing its window cancels too.
[Host](engine.md), [saves](simulation.md#quicksave-and-quickload).

## Painter cache

**Observed** (current `UiPainter.Cache` implementation): controls replay cached quads, glyphs, opaque areas and edit boxes when content and hover/pressed state match. Recording starts on the second frame; caret, loading icons and renderer-owned pictures remain volatile. Texture/font changes, dimensions, minimum text size or UI mode invalidate all records; unused records expire after 120 frames.

**Observed** (`UiPainter.ReadStoredTop`): eligible DDS textures upload their stored top surface in BC1/2/3 compression. The large GUI map reads only that surface on demand; prefetch reads other DDS files on workers.
