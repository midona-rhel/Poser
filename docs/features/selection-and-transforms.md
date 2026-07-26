# Selection and transforms

- All surfaces mutate the one `SelectionSession` and re-read per frame; mode
  changes never clear selection. Ring drags own the pointer through release
  (`GizmoPointerOwnership`) so ending a drag never picks a bone.
- One drag or typed edit = one gesture = one history patch. Baselines freeze
  at pointer-down; every frame dispatches the TOTAL delta — no feedback, so
  nothing compounds. Escape cancels once; tool/space/pivot/selection changes
  cancel rather than mutate a live drag. Multi-selection applies the delta to
  each target's own frozen baseline, never the primary's absolute.
- Frames (Brio, deliberate): wells edit **model-space** values; gizmo World
  mode manipulates the character's model axes, so well drags match World
  arrows 1:1; Local = the bone's own axes.
- Rings: one shared module for inspector and world, projected
  **direction-only** (Brio's ImBrio.Gizmo contract): camera ROTATION only —
  no translation, perspective, FOV, or depth — with one X-mirror handedness
  decision, so ring shape and pixel radius are screen-stable; only the
  world gizmo's centre moves (one `WorldToScreen` for the pivot; an
  unprojectable pivot draws nothing). Orientation still comes from the real
  frame + camera. A drag freezes pivot, frame, axis, and tangent at grab;
  the tangent is the true positive-rotation direction (epsilon-rotated
  direction, same basis). Ctrl 0.1× / Shift 10× / both 1×. Translate and
  scale stay on stock ImGuizmo with component constraints (each tool
  restores what it does not own).
- Pivot (Rotate + bone only): Self rotates in place; Parent orbits the frozen
  parent position using the parent→child radial frame. The gizmo draws at the
  pivot it rotates around. Inspector wells always rotate in place.
- Symmetry adds the `_l`/`_r` partner as an explicit target (Mirror =
  reflected, Link = same-local-motion; math in
  [pose operations](pose-operations.md)). Linked bones is the separate
  Anamnesis eyes/Viera-ears catalog, resolved per partial. Ctrl selection
  may span slots of one actor; symmetry, linked lookup, ancestry, and
  parent traversal never cross a slot boundary.
- Precision wells: drag with modifiers, double-click for numeric entry,
  Escape cancels, the wheel only scrolls. X/Y/Z are literal axes.
