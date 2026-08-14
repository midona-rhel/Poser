# Product scope and boundaries

Poser is a focused FFXIV GPose posing and scene-control tool. Anything not
listed as retained is not part of the active product surface.

- Retained: GPose lifecycle, actor discovery and lifetime actions (clone,
  despawn, visibility, rename, target, companion detach); stable ids;
  selection (tree, maps, matrix, 3D, overlay); local/world gestures with
  Self/Parent pivot, symmetry, linked bones, IK; reset/mirror/flip/stash/
  import/export; one undo journal; expression, gaze, animation/physics
  freeze; settings; the live harness; runtime appearance (opacity,
  whole-model tint, granular wetness - [features/runtime-appearance.md](
  ../features/runtime-appearance.md)); actor-scoped external appearance
  workflows - Penumbra collection, Glamourer design, and Customize+ profile
  selectors, MCDF import/export ([features/files-and-transfer.md](
  ../features/files-and-transfer.md)), and outbound Open-in-Glamourer through
  one integration port. Animation may run while posing. Props, lights, virtual
  cameras, the pose/MCDF library, and AutoSave are retained workspace surfaces.
  The spawn browser's World tab clones a visible overworld actor into the
  scene (read-only discovery; the clone enters through the owned spawn
  transaction — [posing-runtime.md](posing-runtime.md)).
  The environment is a selectable scene entity: time, weather, the eight
  holdable environment sections, water rendering, and festival slots.
- Deferred or parked (no dormant UI or registrations): animation authoring,
  whole-shot scene/project save and restore, reference images, arbitrary
  actor-to-bone attachment, and VFX authoring. Character
  Select+ actor application remains deferred until its public IPC has
  arbitrary-actor targeting and a restore call. General IPC/web APIs beyond
  the integration port remain out of product scope.
- Rejected product boundaries: an animation-authoring timeline and
  Glamourer-owned equipment, customization, dyes, materials, and saved
  designs. Poser may expose retained presentation fields and narrow external
  appearance workflows, but does not take ownership of those systems.

## Compiler-real boundaries

The current solution is transitional. `PosingCore` is a shared mixed legacy
layer that still contains transitional unsafe/native/Dalamud/runtime code,
including entities such as `ActorBase` and `Skeleton` and its
`AllowUnsafeBlocks` setting. `Poser.Game` is the compiler-real native/runtime
destination and boundary, but it is not yet the sole native/runtime assembly
while that PosingCore code remains; in the target graph, `Poser.Game` is the
sole native/runtime assembly. Many Game files are under `LegacyRuntime` and
use `Poser.Game` namespaces. `Domain` has no project references, `Application`
references `Domain`, `Game` references `Domain`, `Application`, and
`PosingCore`, and host `Poser` composes all of those with the single
`Poser.UI` assembly. Product UI still has host-side `Poser/UI` code alongside
the rendering/primitives assembly; that split is transitional, not a second UI
ownership model.

The target graph keeps the `Poser.Game` name; there is no `Poser.Runtime`
rename. `Poser.Domain` contains pure values and policies. `Poser.Application`
owns logical session/state, actions, transactions, receipts, and read models.
`Poser.Game` owns opaque native handles, native reads/writes, hooks, unsafe
code, and framework-thread rules. A `Poser.Persistence` assembly is created
only if it can reference `Domain` and the minimum `Application` storage
contracts while never referencing `Game`, Dalamud, ImGui, native state, or live
UI state; otherwise it stays behind those contracts in `Application`.
`Poser.UI` remains one assembly: there is no `UI.Kernel` project. Host `Poser`
is composition and lifecycle wiring only. `PosingCore` eventually disappears
after its callers move.

Namespaces do not imply assembly ownership. `PosingCore` currently has
`RootNamespace` `Poser`; `LegacyRuntime` is a folder and compatibility seam
inside `Poser.Game`. “Core”, Domain, and Game/runtime are therefore not
interchangeable terms.

## Current traversal and exit proof

Before Slice 1, native writes and feature ownership still traverse the mixed
contracts/entities in `PosingCore` and concrete `Poser.Game`/`LegacyRuntime`
owners. The inventory is: actor and GPose discovery/lifetime; skeleton, slot,
and bone discovery; transforms, pose application, IK, and gaze; spawn,
companion, and prop creation/deletion; animation, presentation, integration,
and MCDF; camera, light, and environment; and pose files, AutoSave, library,
and configuration. These paths include the legacy entities/services, file and
library codecs, policies, EventBus notifications, and mutable owners still
consumed by Game, host composition, and current UI surfaces. The source search
and project graph, not folder names, are the caller inventory.

The target naming/interface policy is deliberately narrow: Domain has pure
values/policies; Application has logical session/state/transactions/receipts,
read models, and narrow ports; Game has opaque handles/native reads, writes,
and hooks; optional Persistence has codecs, atomic storage, and workers; UI
consumes Application read models/actions and owns only surface state; Host
composes. No generic manager, service, repository, mediator, service locator,
capability bag, or interface-per-class framework is introduced.

Deletion proof has two distinct levels. A `LegacyRuntime` owner/file may leave
only when its exact callers are zero or replaced, its composition registration
is removed or replaced, its native leases are transferred or released, and
the replacement passes ordinary and fault-path tests. The replacement must
also preserve the accepted generation/lifecycle contract and keep pointers,
addresses, indices, and native objects inside `Poser.Game`. The `PosingCore`
project may be removed only after its solution/project-reference edges and
production/test callers are zero, migrated generation/lifecycle contracts are
accepted, and no forbidden native edge remains. PosingCore codecs, assets,
and policies migrate selectively. Broad interfaces/entities, EventBus, and
legacy mutable owners are removed after their callers move; they are not
recreated under new generic names.

The actual in-game Poser UI is the only visual oracle. The durable visual and
testing rules live in [ui-workspace.md](ui-workspace.md) and
[process/testing.md](../process/testing.md); this document does not create a
synthetic lab or substitute visual gate.
