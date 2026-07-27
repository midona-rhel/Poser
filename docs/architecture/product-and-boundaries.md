# Product scope and boundaries

Poser is a focused FFXIV posing tool. Anything not listed as retained is not
in the active UI or dependency closure.

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
  may run while posing.
- Deferred (no dormant UI or registrations): animation
  authoring, cameras, lights, environment, world objects, references,
  libraries/projects (no file library, scene format, thumbnails, or
  recent-file database), autosave, status/VFX, Character Select+ actor
  application (its public IPC has neither arbitrary-actor targeting nor a
  restore call — deferred until both exist), and any general IPC/web API
  beyond the integration port above.
- Layers: Domain → nothing; Application → Domain; Game → Domain+Application+
  PosingCore; UI → rendering only; Poser composes. Domain/Application never
  touch Dalamud/ImGui/pointers; addresses never leave `Poser.Game`; UI owns
  no pose state or history. Brio/Ktisis are read-only reference clones.
- Startup eagerly activates `CleanSceneLifecycle` before the UI, or the
  sidebar stays permanently empty.
- End state: Domain+Application → `Poser.Core`, Game → `Poser.Runtime`,
  PosingCore deleted.
- Native behavior is accepted via the live harness; UI manually in game.
