# Pose action layout and shared button treatment

## Purpose

Every Pose inspector action cluster — flip/mirror, regional reset, pose
stash/transfer, IK arming, the rail head, the pose footer, and the pose-file
import/export row — must read as one control system: identical compact
buttons, identical gaps, honest disabled treatment, and wrapping that cannot
diverge from the rendered buttons.

## Layout contract

- All action clusters use `Crystarium.Button` with the shared `compact`
  class. No cluster carries a redundant inline height/style override.
- Buttons within a cluster have a 6 logical-pixel horizontal gap and rows a
  6 logical-pixel vertical gap (24 px compact row height, 30 px row
  advance). The former 8 px `SameLine` spacing in the rail head, footer, IK,
  and import/export rows is retired in favor of the same 6 px system.
- `PoseInspectorPane.DrawWrappedActions` measures each button through the
  component's own measurement (`Crystarium.MeasureButton`), which resolves
  the button's stylesheet class (padding **and** declared font) exactly as
  rendering does. There is no separate hand-authored
  `CalcTextSize + constant` estimate that can drift from the component.
- `Crystarium.Button` pushes its resolved stylesheet font for both measuring
  and drawing, so the declared compact 12 px face is real and measured size
  always equals rendered size.
- Greedy packing fills each row against the actual available rail width and
  starts a new row only when the next complete button would overflow. When
  greedy packing would create a four-plus-one orphan, the last button of the
  fuller row moves down so the result is balanced.
- Callers receive the exact consumed height, so section labels and following
  sections begin after however many rows were actually required; there are
  no hand-authored line breaks.
- No action overflows the inspector at supported widths and UI scales.

## Disabled treatment (shared primitive)

`Disabled = true` on `Crystarium.Button`:

- prevents activation (no hover/active state, no click callback);
- applies the stylesheet's disabled opacity (`.btn:disabled`, 0.35) to the
  **fill, border, label text, and icon** — not only the fill. The opacity is
  folded in once, in the shared render path (`ChromeBuilder` for chrome, the
  text/icon paint for content), so every disabled Crystarium button in the
  application fades uniformly. **Apply stash** with no stash therefore looks
  disabled, not merely unclickable.
- still shows the button's tooltip on pointer hover, so a disabled action can
  quietly explain itself ("Nothing stashed yet").

The fix lives in the shared primitive/stylesheet path; no pane special-cases
any label.

## Verification

Resize the main window and change UI scale while the Pose actions are visible.
Buttons must remain inside the rail, preserve six-pixel gaps in both axes,
reflow only when a complete button no longer fits, and never overlap the
following section. With no stash, **Apply stash** must render faded (text
included), reject clicks, and show its explanatory tooltip; after stashing it
must return to full strength.
