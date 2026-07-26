# PBI-008 — Runtime appearance effects and Glamourer handoff

## Control

| Field | Value |
|---|---|
| Status | Ready |
| Size | Medium |
| Implementation owner | Claude |
| Review owner | Codex |
| Acceptance owner | User, in game |
| Base ref | `pbi-008-base` |
| Feature branch | `feature/pbi-008-runtime-appearance` |
| Accepted head | Not accepted |

## Outcome

Add a focused **Appearance** tab for visual runtime effects that Glamourer does
not own, plus one actor-targeted handoff into Glamourer. This is not a second
equipment/customization editor.

Brio is authoritative for opacity and whole-model tint reads/writes. Ktisis is
authoritative for the continuously enforced three-value wetness override.
Glamourer remains authoritative for persistent appearance.

## Product boundary

Poser owns only:

- actor opacity `0..1`;
- RGB tint multipliers for the character, main-hand model, and off-hand model;
- an optional wetness override containing weather `0..1`, swimming `0..1`,
  and depth `0..3`;
- reset of those Poser-owned runtime effects;
- an **Open in Glamourer** navigation action for the selected actor.

Do not add gear, weapons, dyes, customize data, model/NPC swaps, materials,
shader/customize colors, muscle tone, face paint, hat/visor/weapon/ear state,
design browsing, character files, MCDF, Penumbra collections, or Customize+
profiles. Brio's **Invisible Clothes** is equipment replacement, not “hide
skin”, and is deliberately delegated to Glamourer. Existing actor visibility
remains a lifetime action; opacity zero must not mutate or mirror that flag.

Glamourer's wetness is binary. Poser's wetness section exists only for the
granular Ktisis values and must not add a competing binary wet/dry control.

## Stable runtime ownership

Add one `ActorPresentationSession`, keyed by exact-generation `ActorId`, with
an immutable snapshot and commands through an application runtime port.
Pointers, addresses, draw objects, framework ticks, and IPC subscribers remain
in `Poser.Game`; UI retains no actor or draw-object entity.

Ownership is per field. The first successful Poser edit captures that field's
incoming value; Reset Appearance, Reset All, GPose exit, plugin disposal, and
actor removal while still resolvable restore only captured fields. A failed
restore keeps ownership for retry. Actor replacement invalidates the old
generation without writing its capture into the replacement.

Wetness is re-applied on the framework tick while enabled because the game
updates it. Enabling captures the complete incoming three-float state before
the first write; disabling restores that complete state. Opacity and tint are
written on change. A draw-object replacement/redraw must rebind and reapply
owned tint to the new exact model instance without treating its temporary
defaults as a new capture. Missing weapon models are unavailable, never
redirected to the character model.

Presentation edits are session overrides, not pose deltas, pose-file fields,
named layers, transform gestures, or transform-history entries. Do not add a
second undo journal.

## Glamourer handoff

Use Glamourer's supported IPC navigation API to open the actor corresponding
to the current stable selection. Resolve its current object index only at the
click boundary; a selected bone resolves to its owning actor. If Glamourer is
missing, incompatible, or the actor cannot be resolved, disable the button and
explain why through `HoverHelp`.

This is a narrow outbound integration. Do not query, cache, apply, revert, or
mirror Glamourer designs or state, and do not add a general IPC framework.

## UI

Add **Appearance** beside Pose and Animation in the retained main window. The
outer window keeps its current width when tabs change. Appearance has no pose
inspector rail; its content consumes the released rail width. Sidebar
selection, disclosure, and filter state remain untouched.

The page is a compact actor-scoped form:

- header: selected actor name, **Open in Glamourer**, and **Reset appearance**;
- **Presentation**: Opacity slider and Character/Main hand/Off hand color
  wells, using the existing color-well primitive; absent weapon models show an
  unavailable row rather than disappearing and shifting the form;
- **Wet surface**: Override switch followed by Weather, Swimming, and Depth
  rows, disabled while the override is off.

Use the shared inspector form geometry, white-thumb/blue-fill sliders, Picto
buttons, switches, glass color popovers, and `HoverHelp`. No instructional
paragraphs, developer addresses, raw ImGui widgets, separate window, permanent
scrollbar, or pane-specific styling. The full form must fit at the retained
minimum height and supported UI scales.

## Implementation order

1. Add the stable presentation session, runtime port, field-level captures,
   restoration, and lifecycle reconciliation.
2. Implement verified Brio opacity/tint access and Ktisis-style wetness
   enforcement; handle draw-object replacement.
3. Add the narrow Glamourer open-actor bridge with truthful availability.
4. Build the Appearance tab from retained primitives and integrate Reset All.
5. Remove dead/duplicate paths and update the product boundary plus one concise
   runtime-appearance contract under `docs/features/`.

Use new reviewable commits without amend or rebase after review starts. Claude
runs only the game-loaded Debug build; Codex runs Release once after live
acceptance.

## Acceptance

- Appearance opens for an actor or its selected bone without changing the
  outer window width, sidebar state, or selection; the pose rail is absent.
- Opacity is live from 0 to 1 and Reset restores its exact incoming value;
  opacity zero does not alter the actor-visibility action.
- Character, main-hand, and off-hand tints affect only their corresponding
  models, survive a redraw while owned, and restore exactly. Missing or
  replaced weapon models never receive another model's tint.
- Wetness override holds the three chosen values against game updates.
  Disabling or resetting restores weather, swimming, and depth exactly.
- Open in Glamourer targets the selected actor. Missing/incompatible
  Glamourer and stale selections fail visibly without crashing or applying
  anything.
- Reset Appearance touches no Glamourer-owned state. Reset All and every
  lifecycle exit leave no Poser-owned opacity, tint, or wetness override.
- Pose, animation, expression, gaze, IK, auxiliary skeletons, gizmos, and
  actor lifetime actions continue to behave unchanged.
- The tab has aligned rows, correct hover help, no clipping, overflow, or
  permanent scrollbar at supported scales.

## Handoff

Report base/head, commit map, stable ownership and restoration rules, verified
native fields, draw-object replacement behavior, Glamourer API/availability
behavior, UI structure, removed duplicate paths, Debug build result, and the
remaining in-game walkthrough. Compilation does not prove native writes,
restoration, redraw survival, IPC targeting, or visual correctness.
