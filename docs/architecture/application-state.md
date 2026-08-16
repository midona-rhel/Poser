# Application state

`Poser.Application` owns logical scene, selection, gesture, history, session,
transaction, recovery, and read-model state. It does not own pointers, native
entities, framework services, or live UI state.

- `ActorId` is lineage plus generation. `SkeletonId` is actor plus slot
  (Character/MainHand/OffHand/Prop/Ornament) plus that slot's generation;
  replacing a weapon bumps only its slot. `BoneId` adds partial, index, and
  canonical name and inherits the one slot field. Commands require an exact
  generation match: stale is `StaleTarget`, never an address/name or cross-slot
  fallback. Bone groups are selection-only.
- `SceneSession` converges on one pointer-free `SceneSnapshot` and revision;
  `Contains(target)` is the staleness guard. `SelectionSession` is the sole
  ordered stable-id selection. UI rows and the viewport consume read models,
  while surface state such as filters, disclosure, hover, and picker lifetime
  remains local to each UI surface.
- A gesture captures all baselines once, applies total deltas from those
  frozen values, rolls back on partial failure, and commits one
  `TransformPatch`. Cancel and undo/redo use the same restore path; discrete
  edits are rejected during a gesture. There is one transform history owner.
- `PortablePose` is actor-independent and matches structural
  `PortableBoneKey`/`BonePath` identity, retains ordered duplicate-name
  variants, and treats the native index only as a hint; legacy
  slot-plus-partial-plus-name matching or broadcast requires the explicit
  compatibility adapter or legacy matching policy;
  named producer layers never transfer. Transform and pose contracts cross to
  the native boundary through the narrow ports described in
  [posing-runtime.md](posing-runtime.md).

## Transactions, identity, and receipts

Application workflows own logical transactions and rollback/recovery meaning;
Game owns native materialization and native failure evidence; optional
Persistence owns codecs and atomic storage. A transaction is not a generic
manager or mediator framework. `OperationReceipt` is an immutable read model
carrying the session generation and an operation state/outcome: `Pending` is
  a non-terminal acknowledgement; `Applied`, `RolledBack`, `Failed`,
  `RecoveryRequired`, and `Cancelled` are terminal outcomes. The workflow owns
its active operation epoch, and late results are rejected against the exact
session and epoch. UI renders the receipt; it does not infer success from
callbacks or hidden state. File and MCDF transaction details live in
[files-and-transfer.md](../features/files-and-transfer.md).

## Session lifecycle

This slice gives `SessionLifecycleCoordinator` one idempotent normal-exit and
plugin-unload edge. It does not yet own startup or the complete session phase
machine; those remain deferred to Slice 3. Event-subscription or
dependency-construction order is not a lifecycle contract. The first accepted
framework-thread GPose entry mints one opaque `SessionGeneration`; duplicate
entry observations return that token, and a later normal re-entry mints a new
one. Exit clears the active token before capture/worker drain and the legacy
false-GPose event. A reentrant or running exit cannot admit a new token.
`InvalidateForUnload` is an any-thread, idempotent permanent admission closure;
it clears the token without capture, events, or native work.

On a successfully dispatched framework-thread exit edge it attempts/reserves
the immutable final autosave capture when applicable, while the graph and
session are still readable. Disabled autosave or no eligible actors may return
`NotCaptured`; `CleanOnExit` instead closes periodic admission and drains the
owned worker without reserving a final pose. The edge then drains and joins the
owned worker to a terminal result, deletes only after that drain for
`CleanOnExit`, and publishes the existing legacy false-GPose event exactly
once.

This ordering is conditional on successful framework-thread lifecycle
dispatch. If dispatch faults or is canceled, the host logs the failure,
permanently invalidates session admission before cleanup, and guarantees
provider disposal; this slice does not claim that final capture, worker
drain/join, or the legacy false event completed before provider disposal
begins.

The worker receives immutable snapshots only and never reads live Game/runtime
state. On the successfully dispatched edge it is joined before
unload/provider disposal; a dispatch fault or cancellation does not claim that
join occurred. Capture and worker failures are typed; a final snapshot is not
successful merely because capture returned. Operation-epoch invalidation,
Poser-owned restoration, native/resource teardown, and binding clearing remain
with the current legacy subscribers and are deferred migration work for Slice
3.
Autosave file layout, retention, and autosave storage rules are normative in
[files-and-transfer.md](../features/files-and-transfer.md).
The autosave health read model remains storage-owned there; Application only
interprets its terminal persistence meaning through the final-capture receipt.
