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
carrying the session generation and a terminal outcome: `Pending`, `Applied`,
`RolledBack`, `Failed`, `RecoveryRequired`, or `Cancelled`. The workflow owns
its active operation epoch, and late results are rejected against the exact
session and epoch. UI renders the receipt; it does not infer success from
callbacks or hidden state. File and MCDF transaction details live in
[files-and-transfer.md](../features/files-and-transfer.md).

## Session lifecycle

One explicit `SessionLifecycleCoordinator` owns startup, quiesce, and exit
ordering. Event-subscription or dependency-construction order is not a
lifecycle contract. On final exit it:

1. captures the immutable final autosave snapshot while the session is still
   readable;
2. invalidates operation epochs and cancels/drains active work;
3. restores Poser-owned state;
4. tears down native entities and resources;
5. clears/detaches bindings and publishes the resulting read-model/UI change.

The persistence worker receives immutable snapshots only and final exit joins
it. It never reads live Game/runtime state and is not a detached best-effort
worker. A final snapshot is not successful merely because capture returned:
atomic write failure, persistence-worker failure, or final worker join/drain
failure also makes the outcome `RecoveryRequired` with durable failure
evidence, as do capture, cancellation/drain, restoration, or teardown
failures.
Autosave file layout, retention, and atomic-write rules are normative in
[files-and-transfer.md](../features/files-and-transfer.md).
