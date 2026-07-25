# Product scope and boundaries

Poser is a focused FFXIV posing tool. Anything not listed as retained is not
in the active UI or dependency closure.

- Retained: GPose lifecycle, actor discovery/clone/visibility; stable ids;
  selection (tree, maps, matrix, 3D, overlay); local/world gestures with
  Self/Parent pivot, symmetry, linked bones, IK; reset/mirror/flip/stash/
  import/export; one undo journal; expression, gaze, animation/physics
  freeze; settings; the live harness. Animation may run while posing.
- Deferred (no dormant UI or registrations): appearance, animation
  authoring, cameras, lights, environment, world objects, references,
  libraries/projects, autosave, status/VFX, IPC/web API.
- Layers: Domain → nothing; Application → Domain; Game → Domain+Application+
  PosingCore; UI → rendering only; Poser composes. Domain/Application never
  touch Dalamud/ImGui/pointers; addresses never leave `Poser.Game`; UI owns
  no pose state or history. Brio/Ktisis are read-only reference clones.
- Startup eagerly activates `CleanSceneLifecycle` before the UI, or the
  sidebar stays permanently empty.
- End state: Domain+Application → `Poser.Core`, Game → `Poser.Runtime`,
  PosingCore deleted.
- Native behavior is accepted via the live harness; UI manually in game.
