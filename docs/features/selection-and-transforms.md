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
- Gizmos (Brio's split, deliberate): the inspector ball projects
  **direction-only** — camera ROTATION only, one X-mirror handedness
  decision, fixed pixel radius at the widget centre — so only camera
  rotation reorients it. The world overlay is **perspective-correct**:
  the real view/projection matrices place every handle at the pivot's
  world position, sized in world units measured at the pivot's depth
  (stable perceived size, no off-centre shear); an unprojectable pivot
  draws nothing.
- World tools are fully custom pastel (no stock ImGuizmo): Translate
  shafts/arrowheads/planes, Rotate rings + camera-roll circle, Scale
  local-axis knobs + uniform centre, Universal composing all three with
  deterministic hover priority. Drawn geometry IS hit geometry; each
  handle's tangent/plane mapping freezes at grab and the true
  positive-rotation tangent is epsilon-derived through the surface's own
  projection. Ctrl 0.1× / Shift 10× / both 1×.
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
