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
