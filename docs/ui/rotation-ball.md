# Rotation gizmo (inspector + world)

## Purpose

Poser has ONE rotation-gizmo system (`RotationGizmoRings`), consumed by two
surfaces: the compact inspector widget and the in-world overlay. The shared
module owns the frame basis, axis handedness, camera projection of the ring
geometry, front/rear classification, hit testing, projected drag tangents,
the Ctrl/Shift sensitivity policy, the outer camera-roll axis, and the
total-delta calculation from the frozen drag baseline. Both surfaces
dispatch through the existing clean `TransformGestureService` lifecycle —
there is no second gesture state machine.

## Shared geometry and alignment

- Ring points are true world-space circles around the pivot, projected
  through the actual game camera (`WorldToScreen`); the inspector re-centers
  the projection into its fixed widget circle. For the same actor, bone,
  camera, Local/World setting, and pivot, red/green/blue in the inspector
  are the SAME real rotation axes as in the world, and rotating the camera
  updates both consistently.
- **Local** frames the target's own current world orientation
  (actor ∘ bone model rotation); **World** uses the character's MODEL axes
  (Brio parity: numbers and World gizmo share one frame; for actors the
  model frame IS world). The **Parent**
  pivot uses the parent→child radial frame: red points along normalized
  `child − parent`, the remaining axes form a stable orthonormal basis with
  a deterministic fallback near the reference axis. The parent bone's own
  orientation is not the frame source.
- Front segments are those closer to the camera than the pivot. The wide
  outer ring rolls about the camera→pivot axis.
- **A drag freezes its complete context at grab**: pivot position, ring
  frame (and therefore every ring plane), rotation axis, and screen
  tangent. Nothing is re-derived from the moving bone until release. The
  inspector's DISPLAYED rings still animate: they rotate by the
  accumulated drag angle about the frozen axis — presentation computed
  from the frozen frame plus the drag total, never from the live bone —
  and hand back to the live frame at release without a snap.
- The frozen tangent is the true positive-rotation direction: the grab
  point is rotated a small epsilon about the axis and both points are
  projected, so dragging along the tangent always applies the sign the
  ring shows, on every ring and from every camera angle (~200 px/rad).
  The applied value is always the TOTAL from drag start against the
  gesture's frozen baseline. Ctrl = 0.1×, Shift = 10×, Ctrl+Shift = 1×.
  The mouse wheel is never consumed.
- Hit testing picks the nearest visible projected ring segment within
  tolerance; exact ties resolve X → Y → Z → Roll. Hover and active rings
  brighten and thicken; there is NO cursor-following circle and NO
  drag-origin dot.

## Surface presentation

- **Inspector**: the approved visual reference — dark circular plate,
  pastel axis palette, subdued rear arcs, hover/active emphasis, outer roll
  ring, fixed widget size.
- **World**: same palette, line style, emphasis, and roll ring, drawn at the
  projected pivot — but only meaningful front-facing arcs: no rear arcs, no
  background plate, no decorative guides over the game. Translate and scale
  continue through stock ImGuizmo; only rotation uses the custom renderer.
  The overlay claims the mouse (`SetNextFrameWantCaptureMouse`) while the
  pointer engages a ring.
- **Pointer ownership**: while either surface's ring drag is engaged — and
  on its release frame — `GizmoPointerOwnership` marks the pointer owned.
  Selection surfaces (skeleton overlay actor/bone picking) check it beside
  the stock `ImGuizmo.IsUsing()/IsOver()` guards, so ending a ring drag
  never selects whatever bone sits under the cursor.

## Lifecycle

One drag produces one clean gesture and one history item. Escape, selection
changes, target invalidation, scene changes, failed updates, and pointer
release keep the once-only cancel/commit guarantees: no double rollback, no
same-drag restart (suppression covers both ImGuizmo and ring drags while
the pointer is down), no stale local state, no history item after
cancellation.

## Verification

In-game: compare inspector and world red/green/blue on the same bone in
Local and World; rotate the camera and confirm both stay aligned; select
Parent pivot and confirm origin and radial axes update while the frame
follows the orbiting child; drag each axis with normal/Ctrl/Shift; confirm
no cursor markers appear; confirm world rings show no rear arcs or plate;
one-step undo/redo for both surfaces.
