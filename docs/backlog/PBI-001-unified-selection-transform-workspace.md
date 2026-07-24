# PBI-001 — Unified stable selection and live transform workspace

## Control

| Field | Value |
|---|---|
| Status | Ready |
| Size | Large |
| Priority | First clean-core UI vertical slice |
| Implementation owner | Claude |
| Review owner | Codex |
| Acceptance owner | User, in game |
| Base ref | `pbi-001-base` (immutable annotated Git tag) |
| Feature branch | `feature/pbi-001-stable-selection-transform` |
| Accepted head | Not yet accepted |

The implementation and review process is
`docs/process/external-implementation-review-loop.md`.

## User outcome

An actor or character bone selected anywhere in Poser becomes the same stable
selection everywhere. The sidebar, Body, Face, Matrix, 3D canvas, skeleton
overlay, inspector, precision fields, and gizmo all act on that one selection.
Dragging or typing a transform creates one predictable gesture and one
undo/redo entry while animation and physics may continue running.

The user never needs to understand whether a control is still backed by a
legacy entity projection. Selection survives ordinary UI redraws, rejects stale
native generations safely, and never changes merely because a filtered row is
hidden.

## Why this PBI exists

`SelectionSession`, `SceneSession`, and `TransformGestureService` already define
the clean application behavior, but the main UI still consumes
`ISelectionService`, `IEntity`, `IActor`, `IBone`, and
`CleanTransformFacade` methods that translate legacy objects back into stable
ids. This leaves two representations active at the most important product
boundary.

This PBI finishes the character actor/bone selection and transform path
end-to-end. It does not redesign Poser or add another abstraction framework.

## Decision and clarification rule

Use this precedence when requirements appear to conflict:

1. this PBI's explicit scope, interaction contract, and exclusions;
2. Poser's active architecture and UI concept documents;
3. Brio for native posing behavior;
4. Ktisis for selection/manipulation interaction;
5. Picto for visual grammar only.

Claude must not silently choose between conflicting behaviors, broaden the
scope, or reinterpret an acceptance criterion to fit the implementation. Add
the question to the handoff and stop only the affected slice. The user/Codex
decision is written into this PBI before implementation continues.

## Reference decisions

### Brio: native behavior

Use Brio as the authority for live pose evaluation:

- `../Brio/Brio/Game/Posing/SkeletonService.cs`
- `../Brio/Brio/Capabilities/Posing/SkeletonPosingCapability.cs`

Required behavior:

1. the game runs animation, IK, and physics first;
2. Poser reapplies persistent authored pose data during the native skeleton
   update;
3. cached transforms are refreshed, partials are reparented, and caches are
   refreshed again;
4. the final state is observed after the engine finishes;
5. bone editing does not require animation freeze.

Do not copy Brio's window hierarchy or animation-authoring UI.

### Ktisis: interaction behavior

Use Ktisis as the authority for selection and manipulation semantics:

- `../Ktisis/Ktisis/Editor/Selection/SelectManager.cs`
- `../Ktisis/Ktisis/Editor/Transforms/TransformHandler.cs`
- `../Ktisis/Ktisis/Editor/Transforms/TransformTarget.cs`
- `../Ktisis/Ktisis/Editor/Transforms/TransformResolver.cs`

Retain these ideas:

- one ordered selection with a primary target;
- explicit replace and multi-select behavior;
- selection changes rebuild the transform target;
- a transform begins from a saved state and dispatches one history item;
- multiple targets receive a delta derived from the primary/frozen baseline.

Do not copy Ktisis's animation-freeze requirement, raw Havok writes in editor
objects, or independent window collection.

### Picto: visual grammar only

Use the existing Poser controls that were derived from:

- `../picto/src/shared/styles/tokens.css`
- `../picto/src/shared/ui/SidebarRow/SidebarRow.module.css`
- `../picto/src/app/AppShell.tsx`

Picto supplies density, surface hierarchy, tree-row treatment, typography,
glass borders, restrained active states, and panel composition. Do not port
React state, browser behavior, CSS runtime, or Picto product features.

## Scope

### Included

- character actors and `PoseSlot.Character` bones;
- stable scene and selection ids in every retained selection surface;
- one primary selection and homogeneous multi-selection;
- actor/bone selection from the sidebar;
- bone selection from Body, Face, Matrix, 3D, and skeleton overlay;
- transform manipulation from the inspector and gizmo;
- position, rotation, and scale;
- local and world space;
- per-target, primary, selection-center, and custom pivots where the existing
  UI exposes them;
- linked-bone and copy/mirror target expansion before a gesture begins;
- IK configuration applied through the retained runtime;
- one transform history journal;
- selection and gesture reconciliation on actor/skeleton invalidation;
- removal of the legacy UI selection projection when its final consumer is
  migrated.

### Explicitly excluded

- Main Hand, Off Hand, Prop, and Ornament skeleton-slot discovery and UI;
- animation browser, timeline, keyframes, or animation authoring;
- appearance editing or Glamourer UI;
- cameras, lights, environment, objects, references, library, or projects;
- pose-file redesign;
- a new product window or pop-out window;
- a new UI framework, generic mediator/event system, or second history;
- DevHost, HTML UI, npm, browser automation, screenshots, or pixel tests;
- broad unit-test generation.

The excluded Brio skeleton slots require a later PBI. This PBI must not claim
complete Brio skeleton-slot parity.

## Architecture contract

### Domain

`Poser.Domain` owns identifiers and transform values only. It must not reference
Dalamud, ImGui, legacy `IEntity`, or native pointers.

`SelectionId` and `TransformTargetId` remain the identities crossing the
application boundary. Do not introduce another id type for UI rows.

### Application

`SceneSession` remains the current scene snapshot and lifecycle authority.
`SelectionSession` remains the only ordered selection and range anchor.
`TransformGestureService` remains the only interactive transform transaction
authority. `TransformHistory` remains the only transform undo/redo journal.

If the UI needs an application read model, add the narrowest immutable query
projection required by the retained workspace and document it before adding
the class. It may derive state from the sessions, but it must not store another
selection, gesture, or history cursor.

### Runtime

`Poser.Game` resolves stable ids to the current native binding inside each
operation. It owns framework-thread enforcement, generation validation,
capture, apply, rollback, and live skeleton evaluation.

No `IActor`, `IBone`, `IEntity`, address, or Havok structure may be retained by
the application or UI as command identity. A viewport adapter may resolve a
stable id for one frame to produce an immutable screen/model-space projection;
the pointer or legacy entity does not leave the runtime boundary.

`CleanTransformFacade` must stop accepting `IEntity` collections. Prefer the
existing application commands directly; if a temporary plugin facade remains,
it accepts stable ids/target ids and owns no state.

### UI

The UI projects session state and dispatches selection or transform commands.
It may own:

- filter text;
- category expansion state;
- active Body/Face/Matrix/3D mode;
- hover state;
- typed-field and pointer-drag widget state;
- display formatting.

It must not own:

- another selected-entity collection;
- native transform baselines;
- incremental pose accumulation;
- undo/redo state;
- a cached native entity used as command identity.

Before implementation, update the concept documents for every class,
interface, service, or entity added or materially changed.

## UI and interaction contract

### Shell and sizing

Keep the existing Poser shell:

- 48 px title bar;
- 220–400 px resizable sidebar, initially 280 px;
- 280 px inspector rail;
- 830 px minimum without inspector;
- 1110 px minimum with inspector;
- 1160 px initial Pose width;
- inspector appearance adds/removes exactly 280 px while preserving the main
  workspace width.

Do not restyle the shell during this PBI.

### Sidebar tree

- Actor root rows are transformable selections.
- Category rows are navigation only. Clicking the caret or label toggles
  expansion and never becomes the transform target.
- Bone leaf rows are transformable selections.
- Actor roots initially expand; bone categories initially collapse.
- A selected bone chosen from another surface reveals its actor and category.
- Filtering changes visibility only. It never clears or replaces selection.
- Filter matching and clear-button behavior remain as documented in
  `docs/ui/scene-tree.md` and `docs/ui/search-fields.md`.
- All icons use the existing Tabler/Poser icon registry. No hand-drawn plus,
  clear, undo, redo, or branch icons.

### Pointer and modifier behavior

| Input | Required result |
|---|---|
| Click actor/bone | Replace selection and make it primary. |
| Ctrl + click | Toggle membership in the compatible selection. |
| Shift + click | Select the visible compatible range from the anchor. |
| Click category | Expand/collapse only. |
| Click empty selection canvas | Clear selection only where the existing surface already defines empty-space clear. |
| Right click | Open context actions without silently changing transform semantics. |
| Escape during transform | Cancel the active gesture and restore its frozen baseline. |

Actors never coexist with bones. Bones from different actor lineages never
coexist. Incompatible Ctrl/Shift input replaces the selection with the clicked
target.

### Cross-surface synchronization

The tree, Body, Face, Matrix, 3D canvas, and skeleton overlay read the same
`SelectionSession`. A mutation from any surface is visible to every other
surface on the next frame without an event-bus mirror list.

Changing Body/Face/Matrix/3D mode does not clear selection. If the selected bone
has no visual point in the current map, selection remains valid and the tree
and inspector remain selected; the map does not invent a replacement.

### Inspector

- Actor primary selection shows actor-level transform controls.
- Bone primary selection shows bone-level transform and retained pose controls.
- With multiple compatible targets, the primary target supplies displayed
  absolute values and the header shows the selection count.
- Editing a displayed value applies the resulting delta to the complete
  selection; it does not assign the primary's absolute value to every target.
- No selection shows the existing neutral/empty state and does not retain stale
  values.
- A selection change cancels an unfinished typed edit and any active transform
  gesture before the new target is shown.

Use the existing `InspectorLayout`, `SegmentedControl`, `Switch`,
`ScrubRowDrag`, search, button, and context-menu primitives. New controls must
reuse the stylesheet and shared input/icon paths.

### Precision fields

Keep `docs/ui/precision-transform-input.md`:

- horizontal drag is continuous;
- wheel commits one step;
- Shift and Ctrl adjust wheel precision;
- double-click opens exact numeric input;
- Enter/focus loss commits;
- Escape cancels;
- one field interaction produces one history patch;
- X/Y/Z use the shared mono baseline and existing axis colors.

### Gizmo

- The selected primary determines gizmo placement.
- Every affected target is captured once at pointer-down.
- Each frame derives a total delta from the frozen gesture, not from
  `LastTransform` or the prior rendered frame.
- Move preserves rotation/scale, rotate preserves position/scale, and scale
  preserves position/rotation.
- Normal bone rotation is in place. Position changes during rotation only when
  the explicit Orbit mode is enabled.
- Local/World and tool changes outside a gesture affect the next gesture.
  Changing either during a gesture cancels the gesture rather than changing its
  meaning mid-drag.

## Functional rules

1. `SelectionSession.Primary` is the inspector/gizmo primary.
2. Range order is the visible compatible row order at the time of the click.
3. A gesture target list is frozen at begin.
4. Linked and symmetry partners expand into explicit targets before capture.
5. Selected descendants are removed when a selected ancestor already
   propagates the same edit.
6. Partial capture/apply failure rolls every target back.
7. Commit writes one history patch; cancel writes none.
8. Undo/redo use the same restore path as gesture rollback.
9. Actor or skeleton generation change cancels the gesture before selection is
   reconciled.
10. Non-finite values, invalid quaternions, and invalid scale are rejected
    without a native write.
11. Animation freeze remains optional.

### Clarification: selection primary vs effective transform primary

`SelectionSession.Primary` remains the selection primary used for selection
display (rail header, tree highlight). Transform surfaces derive an
**effective transform selection** from the ordered selection and the scene
snapshot through one shared resolver (`TransformTargetResolver`): selected
descendants of selected ancestors are removed, and the first surviving root
in **original selection order** becomes the effective transform primary.
Inspector displayed values, gesture baselines, ordered target lists, and
gizmo placement all consume this one resolution. The resolver never selects
an unrelated globally shallowest bone and never re-adds a filtered
descendant — including a filtered selection primary.

## Implementation sequence

Claude should implement in this order:

1. **Document the changed concepts.** Update selection, scene, transform,
   viewport projection, and UI ownership documents before adding types.
2. **Complete the application read boundary.** Make the stable scene snapshot
   sufficient for character actor/bone rows and selection display without
   exposing legacy entities.
3. **Migrate selection surfaces.** Main tree, Body, Face, Matrix, 3D, and
   skeleton overlay mutate `SelectionSession` directly through stable ids.
4. **Migrate inspector transforms.** Display and gestures use stable target ids
   and `TransformGestureService`.
5. **Migrate gizmo transforms.** Use frozen clean gestures and a stable-id
   viewport projection.
6. **Migrate retained consumers.** Pose-file selection filtering and the live
   harness read the clean session rather than `ISelectionService`.
7. **Remove compatibility.** Delete `CleanSelectionServiceAdapter`,
   `ISelectionService`, selection event mirroring, and entity-accepting
   transform facade methods only after `rg` proves no consumer remains.
8. **Clean documentation and composition.** Remove obsolete registrations and
   describe the final dependency path.

Do not combine unrelated visual redesign, feature additions, or project
renames with this PBI.

## Suggested commit plan

1. `Document stable selection workspace contract`
2. `Expose stable pose workspace state`
3. `Migrate Poser selection surfaces`
4. `Route inspector transforms through clean gestures`
5. `Route gizmo transforms through clean gestures`
6. `Remove legacy selection projection`
7. `Address in-game interaction findings`

Exact commit count may differ, but documentation, behavior migration, and
compatibility deletion must remain reviewable.

## Acceptance criteria

### Selection

- [ ] Actor click selects exactly that actor and shows actor inspector state.
- [ ] Bone click selects exactly that character bone and shows bone state.
- [ ] Ctrl toggles compatible actor or same-actor bone membership.
- [ ] Shift selects the visible compatible range using the current anchor.
- [ ] Actor/bone and cross-actor bone mixtures are rejected by replacement.
- [ ] Category rows never enter selection.
- [ ] Filter, collapse, and mode changes do not mutate selection.
- [ ] External bone selection reveals the corresponding tree branch.
- [ ] A redraw/rebind either preserves logical selection at the new generation
      or removes it; it never binds to an unrelated reused address.

### Cross-surface behavior

- [ ] Tree, Body, Face, Matrix, 3D, and overlay display the same selection.
- [ ] Each surface applies identical Ctrl behavior.
- [ ] Matrix and map multi-selection show every selected compatible bone.
- [ ] Selection changes immediately update inspector header, values, and gizmo.
- [ ] Empty selection leaves no stale inspector/gizmo target.

### Transform behavior

- [ ] Actor move/rotate/scale works in Local and World space.
- [ ] Bone move/rotate/scale works in Local and World space.
- [ ] Multi-target changes preserve each secondary target's relative baseline.
- [ ] Normal rotation never moves the selected bone.
- [ ] Orbit changes position only when explicitly enabled.
- [ ] Precision drag, wheel, typed commit, and Escape follow their documented
      contract.
- [ ] Selection/tool/space/invalidation changes cancel an active gesture.
- [ ] One completed gesture produces one undo step.
- [ ] Undo and redo restore exact before/after state for every target.
- [ ] Linked/symmetry targets participate in the same atomic patch.
- [ ] IK-enabled translation remains atomic with the gesture.

### Live runtime

- [ ] A visible looping animation continues while the authored bone offset
      remains applied.
- [ ] Physics may continue without revoking bone edit authority.
- [ ] Stale generation produces an explicit failure and no native write.
- [ ] Partial multi-target failure restores all targets.
- [ ] All native capture/apply/restore work remains on the framework thread.

### Architecture and cleanup

- [ ] Retained UI selection consumers no longer depend on
      `ISelectionService`.
- [ ] Selection and transform command identity contains no `IEntity`, `IActor`,
      or `IBone`.
- [ ] There is one selection, one active gesture, and one transform history.
- [ ] `CleanSelectionServiceAdapter` and its registration are deleted.
- [ ] Entity-accepting `CleanTransformFacade` entry points are deleted or
      replaced by stable-id entry points.
- [ ] No new generic event bus, mediator, UI framework, or test framework was
      added.
- [ ] Concept documentation matches the final code.
- [ ] Production `Poser/Poser.csproj` builds with zero errors.

## User in-game walkthrough

The user performs this after Claude's implementation and Codex's code review:

1. Enter GPose with a visible looping animation.
2. Select the actor root; move, rotate, and scale it, then undo/redo.
3. Select a torso or limb bone in the tree; rotate for several seconds and
   confirm position does not orbit.
4. Select the same bone from Body or Face, then from the 3D/overlay surface;
   confirm all surfaces and inspector agree.
5. Ctrl-select two sibling bones; transform them and confirm relative placement
   is preserved.
6. Shift-select a visible tree range, then filter the tree; confirm selection
   remains and returns when the filter clears.
7. Double-click an axis value, type a value, commit, undo, and redo.
8. Begin a drag and press Escape; confirm exact restoration and no new undo
   item.
9. Begin a drag and change selection; confirm cancellation before the new
   target becomes active.
10. Toggle Local/World, linked bones, symmetry, IK, and explicit Orbit one at a
    time; confirm each affects only its documented behavior.
11. Trigger an actor redraw if available; confirm selection safely rebinds or
    clears and never jumps to another actor.
12. Run `/poser test` once. Run `/poser test full` only before removing the
    final proven legacy native path.

The user reports any failure with the selected actor/bone, active tool/space,
input sequence, expected result, and observed result.

## Claude handoff requirements

Claude's final response must include:

- base and head commit hashes;
- ordered commit list;
- changed-path summary;
- which acceptance criteria are complete;
- production build result;
- compatibility types/registrations deleted;
- any criteria that require user in-game confirmation;
- known deviations, assumptions, or questions.

Claude must not claim user acceptance or visual correctness.

## Review log

| Round | Reviewed range | Blocking findings | Non-blocking findings | Result |
|---|---|---:|---:|---|
| 1 | Pending | — | — | Pending |

## Definition of done

This PBI is complete only when:

1. Claude has implemented the scoped feature on the recorded branch;
2. Codex has no unresolved blocking findings against the complete range;
3. the production plugin builds;
4. the user has completed the in-game walkthrough and accepted the UI and
   interactions;
5. the accepted head commit is recorded above;
6. remaining Brio slot parity or other deferred work has its own PBI.
