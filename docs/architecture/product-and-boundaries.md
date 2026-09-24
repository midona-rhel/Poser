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

Project directories and assembly names agree (`Poser.Core` and
`Poser.Core.Tests` included). The Core rename does not remove its legacy
native dependencies; those remain explicit migration work, not a clean
application boundary. Its embedded resources retain the `Poser` root namespace
so file catalogs and pose resources remain compatible.

See [posing-runtime.md](posing-runtime.md) for native ordering and
[application-state.md](application-state.md) for identity, gestures, and
lifecycle.

Scene save/load coordinates an injected native runtime and document store.
The document store owns native/Stagehand format routing and conversion notes;
the workflow owns admission, ordered execution, cancellation, and rollback.
Game-side composition owns the runtime lifetime and disposes the workflow
before its runtime. The workflow does not construct or dispose its dependencies.
This is an incremental boundary: scene policy still lives in Game and its
runtime still carries opaque native tokens; moving it to Application requires
typed identities and removing its legacy Core dependencies first.

Loaded group membership, nesting, order and transform-baseline policy live in
Application behind `ISceneStructureImport`, using stable selection IDs only.
Game resolves native identities and converts file data before that command.
The load waits for bindings and restores structure before terminal publication;
its rollback owns the imported groups. No pending native tokens reach the UI.
Group pruning/root eligibility run after binding publication and generation
remapping, not while drawing the sidebar.
