# PBI-006 — Brio-parity inspector and world gizmos

## Control

| Field | Value |
|---|---|
| Status | Complete |
| Size | Large |
| Implementation owner | Claude |
| Review owner | Codex |
| Acceptance owner | User, in game |
| Base ref | `pbi-006-base` |
| Feature branch | `feature/pbi-006-screen-stable-gizmo` |
| Accepted head | `9dd7498` |

## Outcome

Match Brio's two deliberately different gizmo projections:

- The inspector rotation ball is a fixed, camera-relative screen-space widget.
- The in-world gizmo uses the real camera view/projection and remains
  perspective-correct.

Translate, Rotate, Scale, and Universal share Poser's approved pastel visual
grammar in-world. Moving a gizmo away from viewport centre must not produce
the current unnatural shear/stretch.

## Confirmed problem and references

Poser's custom `RotationGizmoRings.Project` creates a fixed metre-sized world
circle and perspective-projects all 96 points. Its apparent shape therefore
changes with screen position. `PoseRailPane` then recentres that already
distorted world projection inside the inspector.

Brio is authoritative for the split:

- Inspector: `Brio/UI/Controls/Stateless/ImBrio.Gizmo.cs` removes camera
  translation and perspective, uses camera rotation only, and draws at a
  fixed pixel radius.
- World: `Brio/UI/Windows/Specialized/PosingOverlayWindow.cs` passes the real
  view matrix, projection matrix, and target/model matrix to ImGuizmo.

Do not reuse one projection implementation for both surfaces.

## Inspector rotation contract

- Keep the current Poser plate, pastel X/Y/Z arcs, subdued rear arcs, hover
  emphasis, and outer Roll ring.
- Use only the active camera's rotation basis plus the selected Local,
  World/model, or Parent-radial frame.
- Do not use camera translation, perspective, FOV, actor depth, pivot screen
  position, world radius, or per-point `WorldToScreen`.
- Draw directly around the inspector widget centre at its fixed UI-scaled
  radius. The result changes with camera rotation, never actor screen position.
- Hit testing, front/rear classification, and positive drag tangent come from
  exactly that screen-space geometry.

## In-world projection contract

- Match Brio's perspective path: active camera view and projection matrices,
  with the target/model transform composed using the same matrix convention.
- The pivot and axis orientation must be perspective-correct at their actual
  world position. Do not recenter inspector geometry into the viewport.
- Match Brio/ImGuizmo's stable perceived handle size. Do not achieve size by
  projecting a fixed metre-radius circle, which caused the reported
  off-centre deformation.
- Camera pan, orbit, distance, and FOV changes must behave like Brio: correct
  perspective orientation and placement without unnatural shear or stretching.
- Near viewport edges, valid geometry clips normally. Behind-camera or
  non-projectable targets do not draw or accept input.

## World tools and visual grammar

Provide the same pastel axis palette, line weight, and hover/active emphasis
for every toolbar operation:

- **Translate:** X/Y/Z shafts and arrowheads, plus the expected planar handles.
- **Rotate:** perspective-correct X/Y/Z arcs and camera Roll ring.
- **Scale:** X/Y/Z shafts with scale endpoints and uniform centre control.
- **Universal:** one coherent combined Translate/Rotate/Scale gizmo with the
  same geometry and interaction meanings, not stacked duplicate widgets.

The in-world overlay omits the inspector's dark plate, faded rear arcs, and
decorative guides. It must not draw cursor circles, click-origin dots, or
stock ImGuizmo underneath custom geometry.

The current Dalamud ImGuizmo binding exposes no style/color API. Do not merely
draw pastel lines over a still-visible stock gizmo. Either own the complete
presentation/hit-test layer while retaining Brio's view/projection semantics,
or use a verified binding path that genuinely replaces the stock styling.

## Interaction invariants

- Drawn geometry and hit geometry are identical. Hover priority is
  deterministic where Universal handles overlap; the engaged handle owns the
  pointer until release/cancel.
- X/Y/Z manipulate the axis displayed. Roll uses the camera view axis.
- Preserve Local, World/model, and Parent-radial semantics. Presentation must
  not change transform-domain math.
- Freeze tool, axis/plane, pivot, frame, baseline, and drag mapping at gesture
  begin. Camera or selection changes must not bend or restart an active drag.
- Preserve one history item per drag, Escape restoration, restart suppression,
  modifier sensitivity, symmetry, multi-selection, and release-frame selection
  suppression.
- Inspector and world axes represent the same selected frame. Their different
  projections are intentional; their axis identities and applied results match.

## Architecture and cleanup

Extend the existing gizmo module and clean gesture lifecycle; do not introduce
a second selection, transform, or history authority. Remove superseded
world-radius/pixels-per-metre and inspector-recentring paths once unused.

Update only the gizmo contract in
`docs/features/selection-and-transforms.md`. Keep projection handedness and
surprising matrix/sign decisions in tight comments beside the code.

## Excluded

- New transform operations, camera controls, palette redesign, transform
  clipboard, or selection changes.
- DevHost, npm, IPC, screenshots, a new test framework, or broad documentation.

## Implementation order

1. Separate inspector camera-rotation projection from world perspective input.
2. Correct the inspector rotation ball and its hit/tangent math.
3. Implement the styled perspective-correct world Translate, Rotate, and Scale.
4. Compose those primitives into Universal with one interaction owner.
5. Remove superseded/double-render paths and update the normative contract.

Use reviewable commits without amend or rebase after review starts.

## Acceptance

- Inspector shape and radius are unchanged when the actor moves from viewport
  centre to every edge/corner; camera rotation alone reorients its axes.
- World gizmo matches Brio when centred and off-centre: perspective-correct
  placement/orientation, stable perceived size, and no unnatural deformation.
- Translate, Rotate, Scale, and Universal all use the approved pastel styling,
  have correct hover/active feedback, and expose every expected handle.
- Local, World/model, and Parent-radial axes match between inspector and world;
  dragging every axis/plane/roll handle produces the depicted result.
- Off-centre drags, camera movement, cancel, modifiers, symmetry,
  multi-selection, undo/redo, and release selection suppression do not regress.
- No stock/custom double drawing and no permanent scrollbar or overlay input
  leak is introduced.
- Claude runs only the game-loaded Debug build for handoff. Codex runs Release
  once after live acceptance as the closure gate.

## Handoff

Report base/head, commit map, changed paths, the distinct inspector/world
projection decisions, world styling approach, removed legacy paths, Debug
build result, and remaining in-game checks. Compilation is not visual proof.
