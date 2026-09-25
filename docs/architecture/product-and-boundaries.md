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
- `Poser.Documents` references only `Poser.Domain`; it owns portable file
  models, codecs, validation, storage and document-placement math.
- `Poser.Application` references `Poser.Domain` and `Poser.Documents`.
- `Poser.Game` references `Poser.Domain`, `Poser.Application`, and
  `Poser.Documents`.
- The host `Poser` references `Poser.Domain`, `Poser.Application`,
  `Poser.Game`, and `Poser.UI`.
- `Poser.UI` has no project references. Host-side UI composition remains in
  `Poser/UI`.

`Poser.Application` keeps scene state and user actions. `Poser.Game` talks to
the game and runs its hooks on the framework thread. Native entities, skeletons
and low-level service contracts live in Game; the Core project is retired.
The host wires the assemblies; UI shows application state.

Configuration data and JSON recovery/storage live in Documents; settings
migrations and notifications live in Application behind host-provided persistence.
Legacy configuration type metadata is read against the known schema, without
loading old assemblies. Keybinding text and key codes are portable Domain values;
ImGui and native key conversion remain at the input boundary.
Stagehand conversion and MCDF package storage also live in Documents; actor
resource discovery and IPC remain in Game.

Environment readings and commands cross `IEnvironmentControl`. Application owns
capture, apply ordering, gesture coalescing and history; Game implements the
runtime ports. Scene transactions apply through that same control without a
second history entry. UI reads detached values, not native environment services.

Library refresh/cancellation and autosave cadence/admission live in Application.
Documents owns scans, file actions, snapshot writes and disk retention. Game
captures detached poses/scenes and owns framework subscriptions; panel visibility
does not drive this work. Pose and scene snapshots retain their separate formats,
roots and limits, and scene progress is exposed through `ISceneAutoSave`.

Actor appearance commands own their history inverses in Application. UI
supplies an actor and the selected value; it never constructs restore callbacks
or starts native catalog work. Character-file progress and cancellation use
the same application boundary as import/export. Host composition starts catalog
warm-up; native integration sessions remain the single owners of provider state.

Embedded resources retain their original `Poser.Data.*` logical names after
moving to Documents (rest poses and graphical-bone data) or Game (expressions).
Portable transforms live in Domain; file conversion operators live on the
Documents DTOs. Existing serialized fields and keybinding enum values are unchanged.

See [posing-runtime.md](posing-runtime.md) for native ordering and
[application-state.md](application-state.md) for identity, gestures, and
lifecycle.

Scene save/load coordinates an injected native runtime and document store.
The document store owns native/Stagehand format routing and conversion notes;
the workflow owns admission, ordered execution, cancellation, and rollback.
Game-side composition owns the runtime lifetime and disposes the workflow
before its runtime. The workflow does not construct or dispose its dependencies.
Scene workflow policy lives in Application, without a Core or Game reference.
Game implements the runtime interface and outward logging/library notifications.
Portable documents depend on domain values, never native entities or services;
transform conversions do not introduce a reverse dependency from Domain.
Serialized fields, enum values and format rules are unchanged.
Runtime calls exchange session-scoped `SceneEntityHandle` receipts, not native
instances. Game retains the exact instance while workflow/history holds its
receipt, drops it after confirmed removal, and invalidates all receipts on
session change or disposal. Receipts use reference identity, are not serialized,
and never rebind by name, address or a newly published selection generation.
Native owners still validate native lifetime; a receipt alone is not proof that
an entity remains alive. Native reference storage uses weak keys so abandoned
operations and discarded history cannot keep entities alive through the adapter.

The public `ISceneWorkflow` save/load contract, options and progress/results
live in Application. Placement modes are shared Domain values; file DTOs live
in Documents and native workflow mechanisms stay in Game.

Group capture, membership, nesting, order and transform-baseline policy live
in Application behind `ISceneStructure`, using stable selection IDs only.
Save captures a detached structure snapshot at the framework capture boundary;
Application maps those values to file references without reading mutable group stores.
On load, Game resolves native identities; Application converts file data before import.
The load waits for bindings and restores structure before terminal publication;
its rollback owns the imported groups. No pending native tokens reach the UI.
Group pruning/root eligibility run after binding publication and generation
remapping, not while drawing the sidebar.
