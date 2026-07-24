# Pose surface layout

## Purpose

The Pose tab is a viewport, not one long form. Its mode selector and actor-wide
footer must remain visible while the selected Body, Face, Matrix, or 3D surface
uses the space between them.

`PoseInspectorPane` owns this three-region layout:

1. A fixed 44 px mode header at the top.
2. One internal viewport filling the remaining height. It scrolls only when
   the selected surface is a document taller than the viewport.
3. A fixed 47 px pose footer at the bottom.

This prevents a long bone matrix from pushing global pose controls below the
window and prevents switching to a shorter Body, Face, or 3D surface from moving
those controls.

## Shell contract

`AppShellViewModel.DrawContent` receives the stable content-box origin and size,
after the shell inset and scrollbar gutter are removed. A pane sets
`ContentOwnsViewport` when it needs fixed internal chrome. The shell then
disables its outer scrollbar and the pane becomes responsible for scrolling its
middle region.

Pose is currently the only viewport-owning pane. Other tabs remain ordinary
documents that scroll in the shell child.

The mode selector, surface content, and footer share the same 12 px horizontal
inset as the toolbar above them. Viewport-owning panes begin directly below the
toolbar instead of inheriting the 4 px document-content top gap.

Alignment is semantic: the first mode label begins at the same X coordinate as
the active shell-tab label. A segmented control has 3 px of decorative outer
pill padding before its first tab. `alignFirstTabToCursor` excludes that chrome
from layout alignment, so the Body tab edge—not the pill container edge—owns the
content cursor. The component remains unchanged in forms where outer-container
alignment is desired.

The header uses the shell's 44 px toolbar height and paints a full-viewport
bottom hairline.

## Scrolling ownership

The middle child owns scrolling, and its scroll capability follows the
selected surface:

- **Matrix** is a document: the child allows a vertical scrollbar and wheel
  input, inherits the shell-wide 12 px track and rounded-thumb treatment, and
  reserves scroll extent through one cursor sentinel emitted only when the
  generated document is genuinely taller than the viewport.
- **Body, Face, and 3D** are bounded canvases: their child explicitly
  disables the scrollbar and scroll-wheel capture. A canvas mode can never
  acquire a scrollbar from a one-pixel sentinel, rounding, border, or
  child-window padding mismatch, because the capability itself is off. All
  canvas annotations (hint labels, overlays) paint through the draw list
  inside the canvas rectangle and submit no layout items.

## Surface behavior

- **Body:** `GraphicalBonePane` uses one 2054 × 1147 design-space canvas.
  Body, armour, hands, tail, and toes occupy fixed slots. The entire canvas is
  uniformly scaled and centered with a 12 px margin. Optional tail/toe images
  leave their slots empty instead of causing neighbouring images to reflow.
- **Face:** the selected race texture is uniformly scaled to fit and centered
  on both axes. Bone coordinates are transformed through the same image
  rectangle, so dots cannot drift when the viewport aspect ratio changes.
- **Matrix:** the shared `Crystarium.FilterPill` remains part of the scrolling
  middle surface with the generated matrix below it. The fixed mode selector
  and footer never scroll.
- **3D:** the selection canvas rectangle is the middle viewport inset by
  12 logical px on every side — the same horizontal inset as the header and
  footer plus a 12 px top/bottom canvas inset. The inset is applied once;
  panel chrome, projection scaling, clipping, orbit input, bone-dot hit
  testing, and the hint label all use the same resulting content rectangle.
  At very small supported sizes the projection scales down to that rectangle;
  it never requests a larger scroll extent.
- **No skeleton:** the selection prompt uses the fixed shell viewport without
  inventing a second scrolling surface.

## References

- Brio's `UI/Windows/Specialized/PosingGraphicalWindow.cs` calculates a bounded
  child height from the remaining viewport and keeps actions outside that
  scrolling child. Poser follows that containment rule without copying Brio's
  visual treatment.
- Ktisis' `Interface/Editor/Properties/PosePropertyList.cs` keeps global pose
  actions separate from the transform target content. Poser preserves that
  scope distinction in a dedicated footer.
- The visual spacing follows Picto's aligned toolbar/content surfaces and the
  approved Poser M1/M2 shell.

## Verification

At multiple Dalamud UI scales and window heights:

1. Switch among Body, Face, Matrix, and 3D; the selector and footer do not move.
2. Scroll a long Matrix; only the middle surface moves.
3. Confirm the footer remains flush with the bottom of the Pose content area.
4. Confirm the **Body** label starts at exactly the same X coordinate as the
   **Pose** label; ignore the segmented control's decorative outer pill.
5. Resize vertically; Body/Face/3D consume the new middle height and Matrix
   preserves scrolling.
6. Resize horizontally and repeatedly switch Body → Face → Matrix → 3D → Body.
   Body sections retain the same ordering, both maps remain centered, and no
   scrollbar appears on Body, Face, or 3D at any size.
7. Confirm the 3D panel border sits 12 px inside the viewport on every side.
