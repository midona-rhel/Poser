# Posing runtime

`Poser.Game` is the native boundary: framework-thread work, unsafe offsets,
signatures, hooks, native index hints, and handle revalidation stay there;
pointers and native addresses never escape it. The current assembly is
compiler-real even while its `LegacyRuntime` folder is a compatibility seam.
The folder contains concrete owners such as actor/GPose, skeleton/slot/bone,
transform/pose/IK/gaze, spawn/companion/prop, animation/presentation/
integration/MCDF, camera/light/environment, and the native portions of files
and configuration. They still consume `PosingCore` entities, services, and
policies through the transitional graph; see
[product-and-boundaries.md](product-and-boundaries.md) for the complete
inventory and exit proof.

The target keeps `Poser.Game` (never `Poser.Runtime`) as the sole native/runtime
assembly. Its public edges expose opaque handles, validated observations, and
narrow Application ports. A native `BoneIndex` is a lookup hint and mismatch
guard, never portable identity. Exact actor, skeleton, slot, and bone
generations are re-resolved immediately before a write; failed resolution is
explicit, and no native object or address is handed to Application or UI.

A spawned client object has two different indices and they are never
interchangeable. The **ClientObjectManager slot** is what `CreateBattleCharacter`
returns and the only number `GetObjectByIndex`, `GetIndexByObject`, and
`DeleteObjectByIndex` accept; it is the sole index used for identity, deletion,
destruction stamps, and naming. `GameObject.ObjectIndex` is the global
object-table number — a client object's is its slot plus 200, which is why the
GPose range starts at 200. Within the spawn-ownership path it is read in exactly
one place, the ClientObjectManager seam that reports it: feeding an object-table
index back into a slot-taking call deletes a foreign object, and the seam keeps
both numbers visible so a test ClientObjectManager reproduces the difference.
Elsewhere the object-table index is the correct number and those readers stand
as they are — the GPose 201–439 write gates, world-actor discovery, the preview
slot, and the parent index handed to Penumbra and Glamourer
(`IntegrationRuntimePort.IndexOf`) all address the object table, not the
manager.

Spawned actors follow the same boundary: the service owns one private record
per operation, keyed by a service token plus the exact slot, address,
verified `GameObject.EntityId`, and a destruction stamp advanced inside a
native `Character` finalize hook — the same authoritative lifetime transition
Brio's ObjectMonitorService consumes. Because the stamp advances at the native
destructor itself, an external delete-and-reuse with an identical triple is
still observed and fails closed; when the hook cannot be installed, spawning
and delayed callbacks refuse outright — authority never spans frames without
the transition. Every native read or write, owned or legacy non-owned,
re-resolves the exact descriptor immediately before dereferencing inside the
adapter; the sole exception is the spawn seed copy, which reads its source
within the same framework tick the source was obtained — the local player's
address straight from the object table, a clone source only after adapter
resolution refuses a stale wrapper at entry. Unresolved identity refuses the
operation, and all operations refuse off the framework thread. Binding also compares the wrapper's logical
`EntityId` against the production minting formula, never address alone.
Post-create and GPose-exit deletion is exact and retryable: successful
deletion or verified absence retires one record, uncertain or failed deletion
remains pending, a created slot whose first resolve failed is retried on
framework ticks and promoted to exact pending deletion once its create-time
slot stamp proves the occupant, and a create that never yielded a usable
identity is an explicit non-recoverable readout: it never touches native state,
is announced once when the record is made, and is dropped at session end only
once its slot is provably vacated — a destruction stamped there since create,
or an empty slot under an available manager. Absence of proof is never vacancy,
and a readout that never had a slot is never probed. Non-owned visibility
overrides live in a
separate descriptor-keyed store that dies with the native lifetime and the
GPose session. Records are session-only and never become a public handle or
background retry mechanism. This preserves Brio's
create/copy/model-before-draw/GPose ordering without allowing a stale actor
wrapper to affect a replacement.

Overworld-actor discovery is a separate READ-ONLY enumeration outside the
201–439 GPose scan: candidates are pointer-free opaque ids in Application/UI,
backed by a Game-private (reference, address, index, GameObjectId)
observation, re-minted per listing pass and revalidated in full immediately
before use — any drift is a typed stale refusal. No overworld object is ever
handed to a pose or mutation surface; the single crossing is the world-actor
clone, which funnels the revalidated source address into the owned spawn
transaction above, and the clone enters the scene at its own 201–439 index
through the ordinary registry scan. The source is never adopted, mutated, or
deleted.

The current Application-facing native transform write/capture path is
`ITransformRuntimePort`, implemented by Game's `TransformRuntimePort`; it
captures, applies, and restores transform targets. It resolves exact
generations through `StableBindingRegistry` immediately before native access.
`ViewportProjection` is the frame-scoped spatial read for UI presentation, not
a gesture baseline. The remaining Game runtime ports and hooks include
animation, presentation, integration/MCDF, scene lifecycle, and the native
camera, lighting, environment, and skeleton-finalization hooks; these remain
Game-owned while their transitional callers move off PosingCore.

- Animation/IK/physics run first; Poser reapplies persistent layers in the
  skeleton hook, then caches, reparents, caches, and publishes the final
  snapshot. Freeze is a convenience, not a suppression precondition.
- Slot discovery follows the actor draw object, weapon draw data, and ornament
  object. Missing slots are normal; present slots share ordering, and slot
  replacement releases only that slot's bindings, caches, and pose state.
- Pose deltas are keyed by `(Slot, BoneName, PartialId)`. Slot-blind or
  name-only lookup is invalid. Named producer layers are replaced in place;
  normal reset/history preserves them and Reset All is explicit.
- `LastTransform` and `LastRawTransform` are observations, not storage.
  Absolute targets never mix partial caches, and the viewport is a frame-scoped
  read projection rather than a gesture baseline.

The runtime-side lifecycle implementation is transitional. Its correctness
must come from the single Application `SessionLifecycleCoordinator`, not from
EventBus subscription order or construction order. Lifecycle phases and final
autosave capture are normative in
[application-state.md](application-state.md) and
[files-and-transfer.md](../features/files-and-transfer.md).

Persistence, when justified as a separate assembly, is host-free and never
reads live Game state. It receives immutable snapshots through Application
contracts. UI receives read models/actions only; it does not reference native
entities or baselines. The eventual deletion rule for this boundary is in
[product-and-boundaries.md](product-and-boundaries.md).
