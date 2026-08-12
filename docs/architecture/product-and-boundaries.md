# Product scope and boundaries

Poser is a focused FFXIV GPose posing and scene-control tool. Anything not
listed as retained is not part of the active product surface.

- Retained: GPose lifecycle, actor discovery and lifetime actions (clone,
  despawn, visibility, rename, target, companion detach); stable ids;
  selection (tree, maps, matrix, 3D, overlay); local/world gestures with
  Self/Parent pivot, symmetry, linked bones, IK; reset/mirror/flip/stash/
  import/export; one undo journal; expression, gaze, animation/physics
  freeze; settings; the live harness; runtime appearance (opacity,
  whole-model tint, granular wetness — [features/runtime-appearance.md](
  ../features/runtime-appearance.md)); actor-scoped external appearance
  workflows — Penumbra collection, Glamourer design, and Customize+
  profile selectors, MCDF import/export ([features/files-and-transfer.md](
  ../features/files-and-transfer.md)), and outbound Open-in-Glamourer —
  through ONE integration port, the only allowed IPC surface. Animation
  may run while posing. Props, lights, virtual cameras, the pose/MCDF
  library, and AutoSave are retained workspace surfaces. The environment is
  a selectable scene entity: time, weather, the eight holdable environment
  sections, water rendering, and the festival slots.
- Deferred or parked (no dormant UI or registrations): animation authoring,
  whole-shot scene/project save and restore, reference images, overworld
  actor import, arbitrary actor-to-bone attachment, and VFX authoring.
  Character Select+ actor application remains deferred until its public IPC
  has arbitrary-actor targeting and a restore call. General IPC/web APIs
  beyond the integration port remain out of product scope.
- Rejected product boundaries: an animation-authoring timeline and
  Glamourer-owned equipment, customization, dyes, materials, and saved
  designs. Poser may expose its retained presentation fields and the narrow
  external appearance workflows listed above, but does not take ownership of
  those systems.
- Layers: Domain → nothing; Application → Domain; Game → Domain+Application+
  PosingCore; UI → rendering only; Poser composes. Domain/Application never
  touch Dalamud/ImGui/pointers; addresses never leave `Poser.Game`; UI owns
  no pose state or history. Brio/Ktisis are read-only reference clones.
- Startup eagerly activates `CleanSceneLifecycle` before the UI, or the
  sidebar stays permanently empty.
- End state: Domain+Application → `Poser.Core`, Game → `Poser.Runtime`,
  PosingCore deleted.
- Native behavior is accepted via the live harness; UI manually in game.
