# PBI-002 — Pose workspace correctness and interaction stabilization

## Control

| Field | Value |
|---|---|
| Status | Runtime fix round required |
| Size | Large |
| Priority | Immediate runtime and retained-UI stabilization |
| Implementation owner | Claude |
| Review owner | Codex |
| Acceptance owner | User, in game |
| Base ref | `pbi-002-base` (immutable annotated Git tag) |
| Feature branch | `feature/pbi-002-pose-stabilization` |
| Predecessor | PBI-001 implementation plus startup activation fix |
| Accepted head | Not yet accepted |

The implementation and review process is
`docs/process/external-implementation-review-loop.md`.

## User outcome

The retained Pose workspace behaves like one deliberate posing tool rather than
a collection of individually implemented controls:

- Body, Face, Matrix, and 3D use a stable padded viewport without phantom
  scrollbars;
- the scene starts compact, with actor roots collapsed;
- the rotation control has predictable X/Y/Z hit testing;
- expression labels describe the facial result they actually produce;
- gaze modes and target selection work without invalid self-targets or stale
  actor references;
- normal and orbit rotation use one visible pivot choice beside Local/World,
  and the gizmo is drawn at the point it rotates around;
- pose actions share spacing, wrapping, button styling, and disabled treatment;
- reproducing these interactions produces no Poser errors in the Dalamud log.

This PBI stabilizes the existing retained main window. It does not add another
window, workflow, framework, or test harness.

## Source observations

The user observed the following in game on the PBI-001 branch:

1. The 3D surface touches its viewport edges and shows a scrollbar
   continuously.
2. Actor roots start expanded; the desired initial state is collapsed.
3. The inspector rotation ball does not give the X/Y/Z controls consistent
   selection priority.
4. **Blink** at 100% opens the eyes instead of closing them.
5. **Pucker** moves the lips sideways instead of producing a centered pucker.
6. Gaze controls do not reliably produce their selected result. Actor mode is
   also incoherent without another actor selected as its target.
7. IK's scope and control wording are unclear.
8. Parent orbit does not visibly place the gizmo at the parent pivot. The pivot
   segmented control overflows the inspector, and Custom exposes unexplained
   X/Y/Z values.
9. Pose action buttons lack consistent gaps/wrapping. Transfer actions appear
   styled differently.
10. Disabled **Apply stash** blocks activation but its text does not appear
    disabled.
11. The runtime log contains repeated errors during these interactions.

These are observed defects, not permission to redesign unrelated Poser
surfaces.

## Runtime acceptance round 1 clarification

The first in-game walkthrough at implementation head `df09d66` found eight
blocking mismatches. These are corrections to PBI-002, not a new feature PBI.
They supersede any earlier implementation or concept-document wording that
describes the rejected behavior.

### Actor disclosure affordance

Actor roots remain initially collapsed, as requested before implementation,
but a collapsed actor with a discoverable skeleton must always render a visible
and clickable disclosure affordance.

- Use the registered Tabler Chevron Right/Down icons rather than a private
  hand-drawn triangle.
- The icon, hover state, and 18 logical-pixel hit target must remain visible in
  both collapsed and expanded states at every supported UI scale.
- Clicking the disclosure affordance toggles expansion without selecting the
  actor. Clicking the actor row selects it without changing expansion.
- A temporarily unavailable skeleton may disable the affordance, but must not
  permanently erase it once the scene snapshot exposes skeleton children.
- Actor and category disclosure use the same icon and interaction primitive.

### Rotation control is an oriented 3D gizmo

The accepted control is not a flat custom diagram with one vertical line, one
flattened ellipse, and a partial arc. It is the compact camera-relative
rotation gizmo used conceptually by Ktisis `Gizmo2D` and Brio
`ImBrioGizmo.DrawRotation`.

- Draw three complete X/Y/Z circles in 3D.
- Project the circles through the active game camera rotation.
- In Local mode, orient them from the selected actor/bone's current model
  rotation. In World mode, use world axes viewed through the camera.
- The selected transform therefore changes the visible arcs and circle
  foreshortening. The control is not a fixed screen-space symbol.
- Render front-facing portions with the normal axis color and rear-facing
  portions with a restrained low-alpha axis color so every ring remains
  legible as a complete circle.
- Hit-test the nearest visible projected ring segment. Hover, active axis,
  tooltip, and applied quaternion must agree.
- Drag along the selected ring's projected tangent and apply the resulting
  quaternion in the selected Local/World frame. Do not map raw screen X/Y
  deltas directly to Euler components.
- Use the same immutable clean gesture, effective target resolution,
  cancellation, rollback, and history path as the in-world gizmo.
- Do not add another transform state machine merely to embed the gizmo.

Reference implementations:

- `../Ktisis/Ktisis/Interface/Components/Transforms/Gizmo2D.cs`
- `../Ktisis/Ktisis/Interface/Overlay/Gizmo.cs`
- `../Brio/Brio/UI/Controls/Stateless/ImBrio.Gizmo.cs`
- `../Brio/Brio/UI/Windows/Specialized/PosingTransformWindow.cs`

### Transform-field wheel and modifiers

Mouse wheel is navigation while the inspector rail is scrollable. Hovering a
numeric axis field or the rotation gizmo must not change a transform.

- Remove wheel-to-edit from Position, Rotation, Scale, and the compact rotation
  gizmo.
- Do not consume the wheel in those controls; it must continue scrolling the
  inspector in both directions.
- Horizontal drag remains the pointer-edit interaction.
- Ctrl gives fine movement at `0.1×` normal drag sensitivity.
- Shift gives coarse movement at `10×` normal drag sensitivity.
- Ctrl+Shift resolves to normal `1×` sensitivity.
- Apply the policy consistently to the inspector's Position, Rotation, and
  Scale drag fields. Tooltips must state the same behavior.
- The modifier scales pointer deltas accumulated from the frozen gesture; no
  frame may feed a native result back as the next frame's baseline.

This supersedes the earlier precision-field requirement for wheel commits and
the rejected flat rotation ball's Shift-to-Z free-drag shortcut.

### Skeleton overlay default

The in-world skeleton overlay starts Off on a new GPose/UI session. Opening the
main window must not force `SkeletonOverlayWindow.IsOpen = true`.

- The toolbar Armature action remains the explicit on/off control.
- Its active state reflects the actual window state.
- User toggles persist while the current GPose session remains active.
- The gizmo overlay remains independently available; disabling skeleton dots
  must not disable transform manipulation.

### Jaw Open expression

**Jaw Open** at a positive weight must affect the actor's actual face/jaw bones.
The first walkthrough produced no visible bone change.

- Diagnose the concrete actor catalog and record which of `j_f_dago`,
  `j_f_hagukidn`, and `j_f_ago` resolve, including their partial ids.
- Expression lookup must target the face-partial instances that the game
  evaluates. A first-name-only lookup must not silently bind a duplicate bone
  from the wrong partial.
- If multiple valid partial instances intentionally participate, represent
  each with its complete bone identity; do not collapse them by canonical name.
- A catalog unit with zero resolvable target bones is unavailable/diagnostic,
  not an apparently functional slider that performs no work.
- The fix must preserve Blink, Pucker, simultaneous-unit composition, and
  manual face-pose layering.

### Gaze release and actor discovery

Turning off Head, Eyes, or Body means Poser immediately relinquishes that
part. Merely removing the part from the per-frame write mask is insufficient:
the native look-at controller retains the last target and the visible pose.

- Capture the native pre-Poser `LookAtTarget` for each part when Poser first
  takes authority over that part.
- When a part is removed, restore that part's captured target/mode exactly once
  and allow the original game look-at update to recompute it immediately.
- Mode Off and `ResetGaze` restore every controlled part and clear locks.
- Switching Camera ↔ Forward ↔ Actor while a part remains participating changes
  its source without recapturing the Poser-authored output as a new baseline.
- Locked parts retain their frozen source only while they remain participating;
  disabling a locked part still restores its pre-Poser baseline.
- Restoration must occur on the framework/native thread and may use a
  transition command or one-shot pending restore consumed by the detour. It
  must not wait for the inspector to redraw.

Actor targeting is scene membership, not a social feature:

- every valid other actor represented by the current GPose scene is eligible;
- friend-list status is irrelevant;
- the source actor is excluded;
- if the sidebar/`SceneSession` can see another actor but the gaze picker
  cannot, fix the discovery/read boundary rather than displaying “no other
  actors”;
- use stable actor identity for the selection and resolve to the live native
  object only when applying.

### Reset All means all posing state

The Pose section's **Reset All** is an actor-level reset use case, not an alias
for clearing only manual skeleton transforms.

One activation must:

1. clear manual pose transforms for all skeleton regions;
2. clear expression weights and remove the expression layer;
3. restore and clear all Poser gaze modes, participating parts, targets, and
   locks;
4. disarm actor-local IK chains and clear the Live IK state where it would
   otherwise remain active.

It deliberately preserves:

- actor world/model placement;
- the pose stash/clipboard;
- UI tool choice, Local/World choice, and tree disclosure.

Route this through one documented actor-level reset operation. The UI must not
fire several unrelated callbacks with no failure contract. A partial failure
is reported and must not leave managed expression/gaze state claiming that a
layer still exists after its native pose was cleared.

## Decision and clarification rule

Use this precedence when requirements appear to conflict:

1. this PBI's explicit behavior and visual contracts;
2. the stable selection/gesture architecture completed by PBI-001;
3. Brio for native expression, look-at, IK, and pose-application behavior;
4. Ktisis for gizmo, axis, gaze-target, and selection interaction;
5. Picto and the retained Poser/Crystarium primitives for visual grammar.

PBI-002 deliberately supersedes two PBI-001 presentation decisions:

- actor roots now begin **collapsed**, not expanded;
- the inspector Orbit switch and Custom XYZ editor are replaced by a toolbar
  pivot selector as described below.

If reference behavior conflicts with a named facial action's ordinary meaning,
Claude must report the exact catalog/unit/bone evidence rather than adding a
global sign flip or relabeling a broken result.

## Reference decisions

### Brio: runtime authority

Inspect at minimum:

- `../Brio/Brio/Game/Actor/ActorLookAtService.cs`
- `../Brio/Brio/Game/Posing/IKService.cs`
- `../Brio/Brio/Game/Posing/PoseInfo.cs`
- `../Brio/Brio/Game/Posing/SkeletonService.cs`
- `../Brio/Brio/UI/Controls/Editors/PosingTransformEditor.cs`

Retain Brio's native ordering, look-at structure handling, per-part target-lock
semantics, and IK setup rules. Do not copy Brio's window composition.

### Ktisis: interaction authority

Inspect at minimum:

- `../Ktisis/Ktisis/Interface/Components/Transforms/Gizmo2D.cs`
- `../Ktisis/Ktisis/Interface/Overlay/Gizmo.cs`
- `../Ktisis/Ktisis/Interface/Editor/Properties/ActorPropertyList.cs`
- `../Ktisis/Ktisis/Interface/Editor/Popup/ActorGazeTargetPopup.cs`
- `../Ktisis/Ktisis/Structs/Actors/ActorGaze.cs`

Retain Ktisis's direct axis feedback, explicit gaze-target choice, and
screen-space gizmo interaction. Do not restore Ktisis's animation-freeze
requirement.

The expression catalogs under `PosingCore/Data/Expressions` are attributed to
Ktisis v0.4.0.0, but their transforms are not self-validating. The
implementation must verify Poser's conversion and application convention
against the source convention and the live facial result.

### Poser/Picto: visual authority

Reuse:

- `Crystarium.Button`, `SegmentedControl`, `Switch`, `Dropdown`, and existing
  text/axis primitives;
- `InspectorLayout`;
- the existing toolbar and Pose fixed-header/fixed-footer composition;
- Picto's compact spacing and restrained disabled/active treatment.

Do not draw a second private button, switch, segmented control, scrollbar, or
axis-color system inside a pane.

## Scope

### Included

- 3D Pose surface viewport geometry and scrolling ownership;
- initial and externally revealed scene-tree expansion state;
- inspector rotation-ball hit testing and axis feedback;
- expression catalog resolution, transform conversion, blending, reset, and
  retained manual face-pose composition;
- gaze mode, affected-part, target, redraw, and native look-at behavior;
- validation and clarification of the existing IK controls;
- rotation pivot selection, gizmo placement, orbit application, cancellation,
  and undo/redo;
- shared Pose action layout and shared disabled button styling;
- diagnosis and removal of Poser errors caused by the included interactions;
- concept-document updates for every materially changed class/service/control.

### Explicitly excluded

- animation timeline, keyframes, or animation authoring;
- appearance editing;
- new actor-spawn, prop, camera, lighting, environment, or reference workflows;
- pose-file format or import redesign, except fixing a directly reproduced
  error in the existing retained import action;
- a general-purpose custom pivot object or arbitrary XYZ custom-pivot editor;
- new windows or pop-outs;
- a new UI framework, event bus, mediator, history, expression engine, or gaze
  framework;
- npm, DevHost, HTML, browser automation, IPC clicking, screenshots, pixel
  tests, generic unit-test expansion, or a new test harness;
- unrelated warning cleanup or broad exception suppression.

## Architecture contract

### Stable identity

PBI-001's clean selection boundary remains authoritative. New or changed
commands use `ActorId`, `BoneId`, `SelectionId`, or `TransformTargetId`.
Application/UI state must not use `IActor` object reference equality, native
addresses, or `IBone` instances as durable identity.

This is especially important for gaze. The current managed gaze state and
selected actor target must survive an ordinary actor-list refresh by actor
lineage/generation reconciliation, or clear explicitly if the target no longer
exists. They must not silently reset because `ActorManager` produced a new
wrapper instance.

### Native runtime

Expression, gaze, IK, and transform writes remain framework-thread operations.
Native structures are resolved and validated at operation time. A partial or
invalid apply must report one actionable error and restore the previous valid
state where a reversible gesture is active.

Do not make runtime correctness depend on the inspector being open. Continuous
expression/look-at behavior belongs to the runtime service or native update
path, not the draw method.

### UI ownership

The UI may retain:

- selected Pose surface;
- tree disclosure state;
- rotation-ball hover/active axis;
- visible mode/pivot choices;
- active widget gesture state.

It must not retain:

- another actor/bone selection;
- native expression baselines;
- native gaze pointers;
- a second transform gesture;
- a second undo stack;
- hidden Custom-pivot coordinates with no manipulable product concept.

### Documentation first

Before changing production behavior, update the applicable concept documents:

- `docs/ui/pose-surface-layout.md`
- `docs/ui/scene-tree.md`
- `docs/ui/rotation-ball.md`
- `docs/ui/precision-transform-input.md`
- `docs/services/expression-service.md`
- `docs/services/gaze-service.md`
- `docs/ui/gaze-selection.md`
- `docs/architecture/orbit-rotation-design.md`
- `docs/ui/bone-gizmo-transforms.md`
- `docs/ui/pose-action-wrapping.md`

Document the actor-level Reset All operation as its own application concept;
do not hide its multi-service contract inside a pane document.

Add a separate IK concept document if the final implementation materially
changes IK ownership or eligibility. Documentation must describe the final
code, not the originally intended code.

## UI and behavior contract

### 1. 3D Pose surface

The 3D mode is a bounded selection canvas, not a scroll document.

- Use the same 12 logical-pixel horizontal inset as the Pose mode header and
  footer, and a 12 logical-pixel top/bottom canvas inset.
- Apply the inset once. Drawing, clipping, orbiting, and bone-dot hit testing
  all use the same resulting content rectangle.
- The 3D surface consumes the available middle viewport and never creates an
  outer or inner scrollbar merely because of a one-pixel sentinel, rounding,
  border, or child-window padding mismatch.
- At very small supported sizes, the projection scales to the available
  content rectangle; it does not request a larger scroll extent.
- Matrix retains its intentional internal scrolling. Body, Face, and 3D do not
  gain scrollbars when their canvases fit.
- The fixed mode header and Pose footer do not move while switching modes.

### 2. Scene-tree disclosure

- Every actor root first appears collapsed in a new `MainWindow` session.
- Bone categories also first appear collapsed.
- Selecting a bone from Body, Face, Matrix, 3D, overlay, or another retained
  surface temporarily reveals its actor and category so the selected row is
  visible.
- A user collapse/expand action persists for the lifetime of the window.
- Filtering may force matching ancestors open while the filter is active, but
  clearing the filter restores the user's prior disclosure state.
- Adding or refreshing another actor does not expand all existing actors.
- Actor selection itself does not expand the actor.

### 3. Rotation ball

- Red X, green Y, and blue Z are each independently reachable across supported
  UI scales.
- Hit testing is calculated in screen pixels from the same geometry that is
  painted. It compares all eligible axes and chooses the nearest visible
  stroke; no fixed `if` order may steal an overlap from a closer axis.
- Exact-distance ties have one documented deterministic order, used only for
  ties.
- Hover, pointer-down, active drag, retained selection emphasis, tooltip, and
  numeric axis result all name the same axis.
- Starting on empty sphere space performs free X/Y rotation; Shift performs
  the documented Z constraint.
- Axis colors and labels reuse the shared transform-axis palette.
- One drag produces one clean gesture/history item; Escape, selection change,
  and pointer cancellation restore the frozen baseline.

### 4. Expressions

The label is the acceptance contract:

- **Blink**: weight 0 is neutral; increasing toward 1 progressively closes both
  eyelids; 1 is the catalog's fully closed result. It must never open the eyes.
- **Pucker**: weight 0 is neutral; increasing toward 1 produces a centered,
  bilaterally coherent lip pucker. It must not translate the mouth to one side.

Claude must audit every shipped action unit using the same conversion path, not
special-case only Blink and Pucker. The audit must establish:

1. the exact catalog selected for the actor's race/tribe/sex;
2. quaternion component order and multiplication direction;
3. local/model and handedness conventions;
4. position units and axes;
5. left/right bone-name mapping;
6. positive and negative bidirectional weighting;
7. aggregation order for simultaneous units.

Requirements:

- no blanket transform/sign inversion without per-convention evidence;
- unsupported customize/catalog combinations surface a quiet unavailable state
  instead of applying an unrelated face catalog destructively;
- setting a unit 0 → 1 → 0 restores its expression layer to identity;
- repeated changes do not drift or accumulate;
- simultaneous units compose deterministically;
- Reset removes only the expression layer and preserves manual face posing;
- missing catalog bones are skipped without aborting valid bones;
- non-finite transforms are rejected before a native write.

### 5. Gaze

The inspector exposes one shared mode: **Off**, **Forward**, **Camera**, or
**Actor**, plus affected-part switches for Eyes, Head, and Body.

- Off performs no Poser look-at override. Part switches and lock actions are
  visibly disabled while Off.
- Entering a non-Off mode with no affected parts defaults to all three parts.
- Turning off the final active part returns the whole gaze mode to Off.
- A part switch changes only that part's participation.
- A lock action is enabled only for a participating part and freezes/unfreezes
  the actual current target for that part.
- Camera and Forward produce observably different documented sources.
- Actor mode requires an explicit valid target actor different from the source
  actor.
- The target list excludes the source actor and stale/invalid actors, uses the
  configured display-name API, and does not auto-write a target every draw.
- If no other actor is available, Actor mode is disabled or rejected with a
  quiet inline explanation; it does not target self, index zero, or null.
- If the target despawns/redraws, state re-resolves by stable identity or
  safely changes to Off. It never follows an unrelated reused address.
- Changing modes or targets performs one state transition, not a per-frame
  native allocation/write loop.

### 6. IK

This PBI does not invent a new IK model. Claude must compare the current
`Live IK`, `Arm hands + feet`, and `Disarm all` behavior with Brio's
`IKService` and the clean gesture path, then document:

- which bones/chains are eligible;
- whether the global switch changes translation-gesture behavior or native
  chain arming;
- what the arm/disarm actions change;
- what occurs when the selected bone is not an IK effector.

Required minimum behavior:

- translation uses IK only when the selected target belongs to an armed,
  supported chain and Live IK is enabled;
- rotation and scale do not accidentally invoke an IK translation solve;
- unsupported selections show a disabled/unavailable state rather than a
  control that claims success;
- arm/disarm is idempotent, actor-local, and does not alter unrelated pose
  stacks;
- any UI wording must describe the behavior actually implemented.

If current IK already satisfies this contract, prefer documentation and narrow
UI eligibility fixes over a rewrite.

### 7. Pivot and orbit toolbar

Replace the inspector's separate **Orbit** switch, overflowing
Parent/Selection/Custom segmented control, and Custom X/Y/Z rows with one
compact pivot selector in the top transform toolbar immediately after
Local/World.

For Rotate with a bone selection, expose:

| Choice | Behavior |
|---|---|
| Self | Normal in-place rotation; equivalent to Orbit off. |
| Parent | Rotate around the primary bone parent's frozen model-space position. |
| Selection | Rotate around the frozen centroid of the effective transform roots. |

- The selector is visible only where pivot choice changes the active transform
  meaning; it does not crowd unrelated actor/non-rotate states.
- Parent is unavailable for a root with no valid parent.
- Custom is not exposed in this PBI. Remove dead Custom UI/state if no retained
  runtime consumer needs it; otherwise keep only a clearly documented internal
  compatibility value.
- The in-world gizmo's visible center and manipulation matrix are placed at the
  selected pivot. Choosing Parent must visibly move the gizmo to the parent;
  choosing Selection must visibly move it to the centroid.
- The pivot point freezes at gesture begin.
- Every update derives from the frozen baseline. The radius must not compound.
- Changing pivot, tool, Local/World, or selection during a gesture cancels once,
  restores once, and does not restart until the pointer is released.
- Commit creates one history item; Escape creates none.

### 8. Pose and transfer actions

- All action clusters use `Crystarium.Button` with the shared compact class.
- Buttons have a 6 logical-pixel horizontal and vertical gap.
- Wrapping uses the actual rendered size/style of each button, not a separate
  guessed text-width formula that can diverge from the component.
- No action overflows the inspector at supported widths/UI scales.
- Flip/Mirror, Reset, and Transfer share the same height, border, radius,
  typography, and active/hover/disabled treatment.
- Section labels and following sections consume the layout's actual wrapped
  height; there are no hand-authored line breaks.
- `Disabled = true` prevents activation and applies the shared disabled opacity
  to text, icon, border, and fill. **Apply stash** therefore looks disabled
  when no stash exists.
- Fix disabled rendering in the shared button primitive or stylesheet if it is
  systemic; do not special-case the Apply stash label.

### 9. Runtime errors

Before changing a failing runtime path, capture the complete Poser exception
and stack trace for a minimal reproduction. Classify it against the included
interactions above.

- Fix the originating state/validation/lifetime defect.
- Do not catch `Exception` merely to suppress the log.
- Do not downgrade an error to debug without proving the state is expected.
- Repeating one input must not emit an error every frame.
- Invalid user state should produce a quiet disabled control or one concise,
  actionable warning, as appropriate.
- Errors outside this PBI are reported with their exact stack and left
  untouched.

If Claude cannot access the user's current Dalamud log, implementation of the
deterministic items may proceed, but the handoff must list the log-clean
criterion as awaiting the user's reproduction rather than claiming success.

## Implementation sequence

1. **Record current behavior and logs.** Identify exact reproduction paths and
   attributable stack traces available before editing.
2. **Update concept documentation.** Write the final UI/runtime contracts
   before production changes.
3. **Fix shared presentation primitives.** Correct disabled button rendering,
   component measurement, and common spacing without pane-local forks.
4. **Fix bounded viewport and disclosure state.** 3D canvas ownership/padding
   and default-collapsed actor roots.
5. **Fix rotation interaction.** Nearest-stroke axis hit testing and the
   toolbar pivot model, then make gizmo presentation use the frozen pivot.
6. **Correct expression semantics.** Establish the catalog/native conversion
   and repair the shared blend path.
7. **Correct gaze identity and transitions.** Stable target identity, valid
   actor choice, per-part enable/lock behavior, and native application.
8. **Audit IK.** Make only the documented eligibility/behavior corrections.
9. **Remove replaced compatibility.** Delete the inspector Orbit/Custom UI and
   any dead private styling/state after `rg` proves no retained consumer.
10. **Build and hand off.** Build the actual Debug binary loaded by Dalamud and
    the Release solution; report user-only in-game checks separately.

Keep commits narrow enough that runtime fixes are reviewable independently from
layout changes.

## Suggested commit plan

1. `Document pose workspace stabilization contract`
2. `Unify pose action and disabled button styling`
3. `Correct pose viewport and tree disclosure`
4. `Make rotation axis selection deterministic`
5. `Move orbit pivot selection into the toolbar`
6. `Correct expression transform conversion`
7. `Stabilize gaze state and actor targeting`
8. `Clarify IK eligibility and controls`
9. `Address in-game stabilization findings`

Exact commit count may differ. Do not amend or rebase after Codex begins a
review round.

## Acceptance criteria

### Viewport and tree

- [ ] 3D has 12 logical px of padding on every side.
- [ ] 3D, Body, and Face show no scrollbar when their canvas fits.
- [ ] Matrix still scrolls when its document exceeds the middle viewport.
- [ ] Header/footer remain fixed across all four modes.
- [ ] Actor roots initially appear collapsed.
- [ ] Every actor/category with children shows the shared disclosure chevron in
      both collapsed and expanded states.
- [ ] Disclosure toggles without changing actor/bone selection.
- [ ] Filtering and external bone selection reveal required ancestors without
      destroying prior user disclosure state.

### Rotation and pivot

- [ ] The compact rotation control renders three complete, camera-projected
      X/Y/Z rings oriented from the selected transform and Local/World mode.
- [ ] Front and rear ring segments remain legible with distinct opacity.
- [ ] X, Y, and Z are each independently selectable at the user's UI scale.
- [ ] Hovered, active, displayed, and applied axis always agree.
- [ ] Ring-tangent drag applies the correct quaternion without raw
      screen-delta-to-Euler mapping.
- [ ] A compact-gizmo drag creates one undoable clean gesture.
- [ ] Mouse wheel over transform fields/gizmo scrolls the inspector and never
      edits a value.
- [ ] Ctrl drag is `0.1×`, Shift drag is `10×`, and Ctrl+Shift is `1×` for
      Position, Rotation, and Scale fields.
- [ ] Self rotates in place.
- [ ] Parent visibly places the gizmo at the parent and preserves radius.
- [ ] Selection visibly places the gizmo at the effective-root centroid.
- [ ] Pivot changes mid-drag cancel and restore exactly once.
- [ ] No Custom XYZ editor or inspector pivot overflow remains.

### Expressions

- [ ] Blink 0 → 1 progressively closes both eyes.
- [ ] Pucker 0 → 1 produces a centered result without lateral mouth drift.
- [ ] Jaw Open resolves the evaluated face-partial bones and visibly opens the
      jaw.
- [ ] Every shipped unit has been checked through the same catalog conversion
      path for both positive and, where supported, negative weights.
- [ ] 0 → 1 → 0 and repeated adjustments do not drift.
- [ ] Reset preserves manually posed facial transforms.
- [ ] Simultaneous expression units compose deterministically.

### Gaze and IK

- [ ] Off, Forward, Camera, and Actor each produce their documented behavior.
- [ ] Off visibly disables parts/locks and performs no override.
- [ ] Removing Eyes, Head, or Body immediately restores that part's captured
      pre-Poser look-at target/mode.
- [ ] Actor mode cannot target the same actor and requires a valid other actor.
- [ ] Every other actor in the current GPose scene is offered regardless of
      friend-list status.
- [ ] Target redraw/despawn safely re-resolves or disables gaze.
- [ ] Part toggles and locks affect only the intended part.
- [ ] IK runs only for eligible armed chains and only on translation.
- [ ] Arm/disarm is actor-local and idempotent.
- [ ] Unsupported IK selection is represented honestly in the UI.

### Actions and diagnostics

- [ ] Pose and Transfer buttons share one compact style and consistent gaps.
- [ ] Actions wrap without overflow or invented fixed breaks.
- [ ] Disabled Apply stash fades text as well as its surface and cannot click.
- [ ] Reset All clears manual pose, expression, gaze, and actor-local IK state
      while preserving placement and stash.
- [ ] Skeleton overlay starts Off and remains controlled by the Armature action.
- [ ] Reproducing every included interaction emits no Poser error.
- [ ] No exception was hidden by broad catch/log suppression.

### Architecture

- [ ] Gaze durable state/target identity is not keyed by `IActor` reference.
- [ ] Expression and gaze native work remains framework-thread bound.
- [ ] Pivot/orbit uses the existing `TransformGestureService` and history.
- [ ] No duplicate selection, gesture, history, or retained native identity was
      added.
- [ ] Replaced inspector Orbit/Custom UI and dead state are removed or have an
      explicit retained consumer.
- [ ] Applicable concept documentation matches final code.
- [ ] `dotnet build Poser.slnx -c Debug --no-restore` succeeds with zero errors.
- [ ] `dotnet build Poser.slnx -c Release --no-restore` succeeds with zero
      errors.

## User in-game walkthrough

Claude and Codex do not automate this walkthrough. After code review, the user:

1. Reloads the Debug plugin from `Poser/bin/Debug/Poser.dll`.
2. Opens Poser and confirms every actor begins collapsed.
3. Selects a bone externally and confirms only its required tree path reveals.
4. Switches Body → Face → Matrix → 3D at two window heights; confirms fixed
   header/footer, padded 3D, and no phantom canvas scrollbar.
5. Rotates the game camera and the selected bone, then confirms the compact
   rotation gizmo's three full rings reproject accordingly in Local and World.
   Drags X, Y, and Z; verifies matching rotation and one-step undo/redo.
6. Hovers Position/Rotation/Scale fields and the compact gizmo, wheels up and
   down, and confirms only the inspector scrolls. Drags fields normally, with
   Ctrl, with Shift, and with both.
7. Chooses Self, Parent, and Selection in the toolbar; verifies the gizmo
   visibly moves and the bone follows the displayed pivot without drift.
8. Moves Blink from 0 → 1 → 0 eight times; verifies close/restore and no drift.
9. Moves Pucker from 0 → 1 → 0 eight times; verifies centered lips and no
   drift.
10. Moves Jaw Open from 0 → 1 → 0 and confirms the actual evaluated jaw bones
    move and restore.
11. Combines two expression units, resets expression, and confirms a manual
   face-bone edit remains.
12. Tries gaze Off, Forward, Camera, then Actor with a second actor regardless
    of friend-list status. Toggles and locks Eyes, Head, and Body independently;
    each disabled part immediately returns to its pre-Poser state.
13. Despawns/redraws the gaze target and confirms safe disable/rebind.
14. Enables Live IK on an eligible hand/foot chain, translates it, then tries
    rotation and an ineligible bone.
15. Enables an expression, gaze, manual pose, and IK, presses Reset All, and
    confirms all four clear while actor placement and stash remain.
16. Checks Flip/Mirror, Reset, Stash, and disabled/enabled Apply stash at narrow
    inspector width.
17. Confirms the skeleton overlay starts Off, toggles it twice from the
    Armature action, and verifies gizmo manipulation remains available.
18. Reviews the Dalamud log for Poser errors produced by these steps.

The user reports failures with active actor/bone, selected mode/tool/space/
pivot, exact input sequence, expected result, observed result, and the complete
Poser exception when one exists.

## Claude handoff requirements

Claude reports:

```text
PBI:
Base commit:
Head commit:
Commits:
Changed paths:
Observed errors and root causes:
Behavior implemented:
Architecture/docs added or changed:
Debug build:
Release build:
In-game checks still required:
Known deviations or open questions:
```

The handoff must additionally state:

- the expression transform convention found and why Blink/Pucker were wrong;
- the resolved catalog, partial ids, and matched targets for Jaw Open;
- how gaze state and target identity survive/reconcile actor refresh;
- how each gaze part captures and restores its pre-Poser native baseline;
- the discovery source used by Actor gaze mode and proof it has no friend-list
  dependency;
- whether IK required a behavior change or only eligibility/documentation;
- which old Orbit/Custom fields and draw paths were deleted;
- the actor-level Reset All operation and the state it deliberately preserves;
- whether the current user log was available.

Compilation does not prove visual or native correctness. Claude must not claim
that the UI looks correct, expressions look correct, gaze works in game, or the
log is clean unless the user explicitly confirmed it.

## Review log

| Round | Reviewed range | Blocking findings | Non-blocking findings | Result |
|---|---|---:|---:|---|
| 1 | `3426b5b..df09d66` | 8 | 0 | Runtime fix round required |

## Definition of done

This PBI is complete only when:

1. Claude implements only this scope from `pbi-002-base`;
2. Codex has no unresolved blocking findings against the complete range;
3. Debug and Release builds succeed;
4. the user completes the in-game walkthrough and accepts the interaction and
   visual result;
5. included reproductions no longer emit Poser errors;
6. the accepted head is recorded above;
7. any deferred custom-pivot product design or unrelated runtime error becomes
   a separate PBI instead of silently expanding this one.
