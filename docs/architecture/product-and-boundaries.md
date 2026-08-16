# Product scope and boundaries

Poser is an FFXIV GPose tool for posing actors and building scenes. It covers
GPose lifecycle, actor discovery and actions, selection and transforms,
expression/gaze/IK, animation and physics freeze, settings, the live harness,
and one undo journal. It also covers objects, lights, virtual cameras,
environment, overlays, adopted world objects, scenes, pose and MCDF libraries,
autosave, and reference pictures.

Runtime appearance covers opacity, model tint, wet-surface controls, and model
id changes. External appearance stays actor-scoped: Penumbra collections,
Glamourer designs, Customize+ profiles, MCDF import/export, and Open in
Glamourer. Poser does not own Glamourer equipment, customization, dyes,
materials, or saved designs.

Animation authoring, arbitrary actor-to-bone attachment, and VFX authoring are
not supported. Character Select+ actor application is not supported until its
public IPC can target arbitrary actors and restore them. General IPC and web
APIs are not product features.

## Current assembly boundaries

At this revision:

- `Poser.Domain` has no project references.
- `Poser.Application` references `Poser.Domain`.
- `Poser.Core` references `Poser.Domain`.
- `Poser.Game` references `Poser.Domain`, `Poser.Application`, and
  `Poser.Core`.
- The host `Poser` references `Poser.Domain`, `Poser.Application`,
  `Poser.Game`, `Poser.Core`, and `Poser.UI`.
- `Poser.UI` has no project references. Host-side UI composition remains in
  `Poser/UI`.

`Poser.Application` keeps scene state and user actions. `Poser.Game` talks to
the game and runs its hooks on the framework thread. `Poser.Core` still holds
legacy entities, services, file formats, configuration, and some game code.
The host wires the assemblies; UI shows application state.

See [posing-runtime.md](posing-runtime.md) for native ordering and
[application-state.md](application-state.md) for identity, gestures, and
lifecycle.
