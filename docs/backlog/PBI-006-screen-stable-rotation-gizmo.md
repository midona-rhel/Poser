# PBI-006 — Screen-stable rotation gizmo

## Control

| Field | Value |
|---|---|
| Status | Ready |
| Size | Medium |
| Implementation owner | Claude |
| Review owner | Codex |
| Acceptance owner | User, in game |
| Base ref | `pbi-006-base` |
| Feature branch | `feature/pbi-006-screen-stable-gizmo` |
| Accepted head | Not accepted |

## Outcome

The inspector and in-world rotation gizmos retain one stable shape and size
wherever the actor appears on screen. Moving a pivot away from screen centre
must translate the world gizmo, not shear, stretch, or squash its rings.
Camera rotation still changes the apparent axis orientation, and every ring
continues to rotate around the axis it depicts.

## Confirmed cause and reference

`RotationGizmoRings.Project` currently constructs metre-sized world circles
and calls `WorldToScreen` for every point. Perspective projection therefore
changes their local shape with screen position. `PoseRailPane` then recentres
that already-distorted result inside the inspector.

Brio's inspector gizmo is the compatibility reference:
`Brio/UI/Controls/Stateless/ImBrio.Gizmo.cs`. It removes camera translation
and perspective, uses camera rotation only, and projects a constant-radius
screen-space sphere. Brio's stock ImGuizmo world overlay likewise remains
screen-stable away from the viewport centre.

## Projection contract

- Use one shared direction-only ring projection for the inspector and world
  rotation overlay. Do not maintain separate visual or hit-test geometry.
- Obtain the active camera's rotation basis without translation, perspective,
  pivot position, FOV scaling, or depth scaling.
- Transform each unit ring direction by the selected gizmo frame and camera
  rotation, then map its camera-space X/Y to the requested pixel radius.
- The world overlay uses `WorldToScreen` only once to locate the pivot centre.
  The inspector uses its fixed widget centre directly.
- Ring radius is expressed in UI pixels and respects global UI scale. It is
  independent of pivot depth, screen position, camera FOV, and actor scale.
- Front/rear classification comes from the same direction-only camera-space
  geometry. The inspector retains subdued rear arcs; the world overlay draws
  front arcs only.
- The roll ring remains a true screen-space circle about the camera view axis.
- A pivot outside the projectable viewport does not draw. A valid gizmo near
  an edge clips normally; its visible geometry must not deform.

The camera/view handedness must be established once from Brio and the game's
matrix convention. Do not repair individual axes with unrelated sign flips.

## Interaction invariants

- Hit testing uses the exact screen-space segments that were drawn.
- Positive drag tangents derive from the same direction-only projection; do
  not call perspective `WorldToScreen` on an epsilon-rotated ring point.
- X, Y, Z, and Roll retain their current domain axes in Local, World/model,
  and Parent-radial modes. This PBI changes presentation projection only.
- Freeze axis, pivot, frame, baseline, and tangent at gesture begin. Camera
  movement or selection refresh during a drag must not bend the active ring,
  change its sign, restart it, or create a second history entry.
- Preserve pointer ownership, release suppression, Escape cancellation,
  modifier sensitivity, multi-selection targeting, and symmetry behavior.
- Inspector and world gizmos must show the same axis orientation for the same
  target/frame. Their only intentional visual differences are the inspector
  plate/rear arcs and the world overlay's omitted decoration.

## Scope and cleanup

Refactor the existing `RotationGizmoRings` projection rather than adding a
second gizmo framework. Remove obsolete world-radius, pixels-per-metre,
per-point world projection, and recenter-after-perspective paths. Keep stock
ImGuizmo translation/scale behavior and the established pastel ring styling
unchanged.

Update only the rotation-gizmo portion of
`docs/features/selection-and-transforms.md`. Explain the screen-stable
camera-rotation contract there once; implementation math belongs in tight
source comments.

## Excluded

- New gizmo modes, palette redesign, camera controls, selection changes, or
  transform-core changes.
- DevHost, npm, IPC, screenshots, a new test framework, or broad documentation.

## Implementation order

1. Introduce the direction-only camera basis and projected ring geometry.
2. Migrate front/rear classification, hit testing, and positive tangents.
3. Use the shared geometry directly in the inspector and world overlay.
4. Remove the superseded perspective/recentring code and update the one
   normative document.

Use reviewable commits without amend or rebase after review starts.

## Acceptance

- With one bone selected, move or pan the actor from viewport centre to the
  left, right, top, bottom, and corners: ring shape and pixel radius remain
  stable; only the world-gizmo centre moves.
- Orbiting the camera changes axis orientation smoothly without changing the
  gizmo's radius or introducing off-axis shear.
- Changing camera distance or FOV does not resize the rings.
- Inspector and world X/Y/Z orientation match in Local, World/model, and
  Parent-radial modes; Roll remains circular.
- Every ring can be dragged while off-centre with the expected direction,
  frozen-axis behavior, one history item, and no selection click on release.
- Existing move, scale, Universal, symmetry, multi-selection, cancel, and
  modifier behavior does not regress.
- Claude runs only the game-loaded Debug build for handoff. Codex runs the
  Release build once, after live acceptance, as the closure gate.

## Handoff

Report base/head, commit map, changed paths, the camera-basis and handedness
decision, removed perspective paths, Debug build result, and remaining
in-game checks. Make no runtime or visual claim from compilation.
