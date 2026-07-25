# PBI-004 — Slot-qualified auxiliary skeleton posing

## Control

| Field | Value |
|---|---|
| Status | Ready |
| Size | Large |
| Implementation owner | Claude |
| Review owner | Codex |
| Acceptance owner | User, in game |
| Base ref | `pbi-004-base` (immutable annotated Git tag) |
| Feature branch | `feature/pbi-004-slot-skeleton-posing` |
| Accepted head | Not accepted |

## Outcome

One scene actor can pose every live skeleton owned by its draw state:
Character, Main Hand, Off Hand, Prop, and Ornament. They remain one actor in
the UI and application state, while each skeleton and bone is resolved,
stored, transformed, reset, and serialized in its exact slot.

## Reference behavior

Brio is authoritative for native discovery, update ordering, pose ownership,
and `.pose` collection mapping:

- `CharacterExtensions.GetCharacterBases` discovers the character base,
  three weapon draw-data bases, and ornament base.
- `SkeletonPosingCapability` retains and registers one skeleton per
  `PoseInfoSlot`.
- `PoseInfo` keys authored layers by bone name, partial, and slot.
- `ExportSkeletonPose` and `PoseImporter` map each slot to its matching file
  collection.

Ktisis remains a UI/interaction reference, but its posing backend is not the
slot-lifecycle authority. Preserve Poser's retained Picto-like workspace and
PBI-001 gesture contracts.

## Decisions

1. Slots are not actors. The sidebar keeps one actor row.
2. A slot identifies an independently replaceable native skeleton. Skeleton
   identity therefore includes actor, slot, and that slot's generation.
   Bone identity inherits that slot; do not keep two slot fields that can
   disagree.
3. Character, MainHand, OffHand, Prop, and Ornament are the only supported
   slots. `Unknown` is never selectable, persisted, or silently substituted.
4. Missing auxiliary slots are normal. They do not make the actor or
   character skeleton unavailable.
5. Character posing and its existing tree/maps remain visually unchanged.
6. No lookup may fall back from one slot to Character or match only by bone
   name, partial, or index.
7. Animation may continue while any slot is posed, using Brio's reapply-after-
   game-update model. Freeze remains optional.

## Identity and scene state

- Replace the single-skeleton actor snapshot with a slot-indexed collection
  of present skeleton descriptors.
- Give every slot an independent generation. Replacing a weapon must not
  invalidate Character or the other auxiliary slots.
- `StableBindingRegistry`, `SceneSession`, `TransformTargetResolver`, and
  `ViewportProjection` resolve the exact actor generation, skeleton slot and
  generation, partial, index, and canonical name.
- Scene signatures include slot presence and each slot's structural
  identity. Identical refreshes publish nothing.
- If a slot disappears or is replaced, reconcile matching bones only within
  that slot. Cancel an active gesture touching stale slot data atomically;
  do not disturb unrelated actor or slot selection.
- Ctrl multi-selection may span slots of the same actor. Every target still
  uses its own frozen baseline. Symmetry, linked-bone lookup, ancestry, and
  parent traversal never cross a slot boundary.

## Native discovery and posing runtime

- Discover Character from the actor draw object, MainHand/OffHand/Prop from
  the matching weapon draw-data entries, and Ornament from the ornament
  object's draw object, following Brio's null and readiness checks.
- Refactor the legacy single-actor `SkeletonService` API into slot-aware
  discovery. Native pointers and `CharacterBase` values remain inside
  `Poser.Game`.
- Register every present slot skeleton for the same per-frame cache,
  reparent, transform reapplication, and finalization ordering used by the
  Character skeleton.
- Qualify every durable or frame cache by slot: pose stacks, IK state,
  evaluation observations, skeleton-update sets, bone bindings, parent
  links, animated baselines, and spatial matrices.
- The posing runtime must never apply a Character stack to an identically
  named weapon/ornament bone or vice versa.
- Slot redraw, sheathe/unsheathe, equipment replacement, prop replacement,
  ornament spawn/despawn, and actor teardown release obsolete native
  skeletons without leaking hooks, bindings, or pose state.

## Operations

- Inspector wells, custom rotation rings, world gizmo, undo/redo, cancel,
  reset bone, flip bone, copy/stash/apply, and overlay selection work for any
  selectable slot bone through the existing stable-id command path.
- Body, Face, and Hair resets remain Character-only. Reset All keeps its
  existing order: expression → gaze → manual layers across every present
  slot → IK disarm.
- Whole-pose mirror and transferred poses include auxiliary slots. Pairing
  and center-bone mirroring happen within the source slot only.
- Portable poses retain slot identity. A missing destination slot is reported
  as unmatched rather than redirected to Character.
- Existing atomicity remains mandatory: a multi-slot edit is one history
  patch; any target failure rolls back every target.

## UI behavior

- Expanding an actor shows the existing Character categories directly; do
  not add a redundant `Character` wrapper row.
- Each present auxiliary slot appears as one additional group under that
  actor: Main Hand, Off Hand, Prop, Ornament. Groups are absent when their
  skeleton is absent and start collapsed like every other tree disclosure.
- Expanding an auxiliary group shows its real parent/child bone hierarchy.
  Rows carry exact stable bone ids; duplicate names across slots are valid.
- Selecting an auxiliary bone drives the existing inspector and world gizmo.
  Matrix and 3D operate on the primary bone's slot skeleton. Body and Face
  maps remain Character-only and must not mis-highlight a same-named
  auxiliary bone.
- Skeleton overlay projects all present slots. Hover, click, Ctrl, Shift,
  Alt-hide, pointer ownership, filter behavior, and disclosure ownership keep
  the current contracts.
- Do not add another window, global slot selector, actor row, or persistent
  toolbar mode.

## Pose files

- Refactor file operations to consume an actor's slot skeleton set rather
  than one `ISkeleton`.
- Export absolute `LastRawTransform` values, skipping non-root partial roots,
  to exactly:

| Slot | `.pose` collection |
|---|---|
| Character | `Bones` |
| MainHand | `MainHand` |
| OffHand | `OffHand` |
| Prop | `Prop` |
| Ornament | `Ornament` |

- Import each collection only into its matching live slot. Unknown and
  unavailable slots are skipped and reported; no name-based cross-slot
  fallback is allowed.
- Add explicit Prop and Ornament import options beside the existing weapon
  options. Full includes every slot; Body and Expression remain
  Character-only; Selected uses the selected bones' exact slots.
- Reset-before-import applies only to the slots/bones in the chosen scope.
  Model transform is applied once to the owning actor. Face compatibility and
  reconcile logic remain Character-only; `.cmp` remains Character-only.
- Export followed by reset and import must reproduce the same authored pose
  in every present slot.

## Architecture and cleanup

- Evolve the clean Domain/Application model first; do not add another
  selection projection, slot facade, event mirror, or entity-keyed command
  path.
- Replace single-skeleton overloads in retained consumers when their meaning
  becomes ambiguous. Delete superseded slot-blind paths and registrations
  after migration.
- Extend the existing canonical documents only:
  `architecture/application-state.md`, `architecture/posing-runtime.md`,
  `architecture/ui-workspace.md`, `features/selection-and-transforms.md`, and
  `features/files-and-transfer.md`. Do not create class-by-class documents.

## Excluded

- Appearance/equipment changing or spawning an item to manufacture a slot.
- Pose autosave, arbitrary attachments, reference poses, and A/T poses.
- Advanced IK configuration (PBI-005).
- Custom Move/Scale/Universal gizmos and transform clipboard (PBI-006).
- Animation authoring or keyframes.
- A new test framework, standalone host, npm, browser, screenshot, or IPC
  validation.

## Implementation order

1. Update the canonical contracts and slot-qualified domain identities.
2. Implement native slot discovery, independent lifecycle, and bindings.
3. Migrate pose storage, runtime application, projection, and clean commands.
4. Migrate scene tree, Matrix/3D, inspector, gizmo, and overlay.
5. Migrate portable poses and `.pose` import/export.
6. Remove slot-blind compatibility paths and verify the full dependency path.

Each step should be a reviewable commit that leaves production code compiling.
Do not amend or rebase after review begins.

## Acceptance

- [ ] One actor snapshot exposes every present native slot exactly once.
- [ ] No auxiliary skeleton is represented as another actor.
- [ ] Slot replacement increments only that slot's skeleton generation.
- [ ] Every retained bone selection and transform surface resolves the exact
      slot-qualified bone.
- [ ] Same-named bones in different slots never share state or writes.
- [ ] Every present slot receives persistent per-frame pose reapplication.
- [ ] Gesture cancellation, rollback, history, reset, mirror, and transfer
      remain atomic across all targets.
- [ ] Tree, Matrix, 3D, overlay, inspector, and gizmo obey the UI behavior
      above without changing the Character layout.
- [ ] `.pose` export/import uses the five matching collections with no
      cross-slot fallback.
- [ ] Slot-blind retained APIs, caches, and pose keys are removed.
- [ ] Canonical documentation matches the final implementation.
- [ ] `dotnet build Poser.slnx -c Debug --no-restore` and Release complete
      with zero errors and warnings.

## In-game acceptance

Using an actor with visible Main Hand and Off Hand, then a Prop and Ornament
when available:

1. Expand the actor. Character rows are unchanged; only present auxiliary
   groups appear, initially collapsed.
2. Select and transform one bone in each slot with animation still running.
   Inspector, world gizmo, Matrix/3D, overlay, undo, redo, cancel, and reset
   affect only that bone's slot.
3. Transform same-named bones from different slots and confirm their pose
   stacks never cross.
4. Perform one multi-slot edit, then undo/redo it as one atomic action.
5. Sheathe, redraw, or replace a weapon during selection and during a drag.
   The stale gesture cancels safely; Character and unrelated slots remain
   usable.
6. Export after editing several slots, Reset All, then import Full. Confirm
   each slot returns to its exported pose and actor placement is applied once.
7. Import Body, Expression, and Selected scopes and confirm no unrelated slot
   changes.
8. Despawn the ornament/prop and actor; confirm rows, overlays, selection, and
   bindings disappear without errors.

No visual or native behavior is accepted from compilation alone.

## Handoff

Claude reports the base/head range, commits, changed paths, slot identity
shape, discovery/lifecycle path, every migrated pose/cache key, UI behavior,
file mapping, deleted compatibility paths, Debug/Release builds, remaining
in-game checks, and any deviation. Do not claim runtime or visual correctness
without the user's walkthrough.
