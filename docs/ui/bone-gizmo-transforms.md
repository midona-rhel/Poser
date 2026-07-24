# Bone gizmo transforms

## Purpose

`GizmoOverlayWindow` presents ImGuizmo against a bone's Havok model-space transform. The skeleton's actor model matrix is folded into the view matrix, matching Brio's convention, so the matrix passed to ImGuizmo remains bone-model-space while appearing at the correct world-space location.

Rotation has one visible pivot choice — the toolbar selector documented in
`orbit-rotation-design.md`. **Self** is an in-place edit: it changes a bone's
quaternion and does not change that bone's model-space position or scale.
**Parent** and **Selection** are the only rotation paths allowed to change
the bone position, orbiting it around the frozen pivot.

The gizmo is drawn at the point it rotates around. With Self it sits on the
effective transform primary; with Parent it visibly sits at the parent's
model-space position; with Selection it sits at the effective-root centroid.
At rest the pivot position tracks the live scene through the viewport
projection; at pointer-down it freezes for the whole gesture, and the
manipulation matrix combines that pivot position with the primary's rotation
so Local axes stay meaningful.

Bone manipulation remains enabled while the actor's animation is running.
Animation freeze is an optional precision convenience, not an editing
precondition. Poser follows Brio's runtime model: the game produces the current
animated baseline, then Poser reapplies persistent bone deltas from the stable
gesture snapshot.

## Stable drag baseline

At pointer-down the window dispatches `TransformGestureService.Begin` with the
selection's `TransformTargetId` list; the service captures every target once.
Each subsequent frame converts the manipulated matrix into a **total**
`TransformDelta` from the frozen pointer-down baseline and dispatches
`Update`. Rest-state placement reads model transforms through the runtime
viewport projection (`docs/game/viewport-projection.md`).

The drag loop must not reconstruct its next input from live bone data. Havok
propagation and cache refreshes can alter live model-space data between
frames; feeding that back through matrix decomposition makes an in-place
rotation accumulate position changes and visually orbit an unrelated pivot.
Brio avoids this feedback with its tracking transform; Poser's equivalent is
the gesture's frozen baseline — the window retains no per-bone native
baseline dictionary of its own.

`ImGuizmo.IsUsing()` is sampled after `ImGuizmo.Manipulate(...)`. This is required because the first call that begins a drag can change the matrix while the pre-call `IsUsing` value still describes the previous frame. Poser publishes `TransformDragStartedEvent` after that detection but before applying the first result, allowing history to capture the true pre-drag state.

## Component constraints

`PoseMath.ConstrainToComponents(baseline, manipulated, components)` restores components that the active single-purpose tool does not own:

- Move preserves rotation and scale.
- Rotate preserves position and scale.
- Scale preserves position and rotation.
- Universal accepts position, rotation, and scale.

This is a correctness boundary, not merely an epsilon cleanup. It prevents matrix decomposition or transformed-skeleton view conventions from converting a rotation-only gesture into translation.

## Multi-selection

The primary bone's constrained change becomes the gesture's single
`TransformDelta`; `TransformGestureService.Update` applies it to every frozen
secondary baseline. Every selected bone is a target — including same-chain
pairs like knee + calf (user-requested reversal of the PBI-001 descendant
filter): each target recomputes absolutely from its own frozen baseline, so
an ancestor's propagation cannot feedback-compound. The shared
`TransformTargetResolver` produces the effective transform selection from the
ordered session and the snapshot: the first selected bone is the effective
primary that anchors gizmo placement and supplies the gesture baseline — the
inspector consumes the identical resolution, so the two surfaces can never
disagree.

Actor gizmos use the same component constraint and relative-delta contract. A
multi-actor world-gizmo gesture edits every selected actor and publishes the
complete group to history.

## References

- Brio `PosingOverlayWindow`: uses a tracking transform during a bone gizmo drag instead of re-reading the live bone every frame.
- Ktisis overlay transform targets: isolate editor manipulation from the underlying target application.
- Poser `orbit-rotation-design.md`: defines the toolbar pivot model (Self/Parent/Selection; frozen clean-gesture pivot; the inspector Orbit switch, Custom pivot editor, and strategy machinery are deleted). Changing pivot, tool, orientation, or selection mid-drag cancels the gesture exactly once, restores the frozen baseline, and suppresses restart until the pointer releases.
- IK arming is session state configured through the stable-id `CleanPoseFacade.ConfigureIk` at gesture start; no entity leaves the runtime.

## Verification

- With the Self pivot, rotate one non-root bone for several seconds. Its displayed model-space position and scale must remain unchanged while its quaternion changes.
- Repeat at a non-default actor rotation and scale.
- Repeat in Local and Global orientation.
- Repeat while a visible looping animation is running. The bone must follow the
  animation while retaining the additional pose offset.
- Rotate a multi-selection and confirm no selected root translates.
- Choose Parent and Selection explicitly and confirm the gizmo visibly moves to each pivot, that position changes only in those modes, and that the orbit radius does not drift over a long drag.
- Undo once after each gesture and confirm the exact pre-drag transform is restored.

The rotation runtime slice of live-animation editing was accepted on 2026-07-23
by `posing.animation-interference`: eight iterations, twelve distinct native
evaluations per iteration, no invariant failures. Component isolation is
covered separately by `posing.bone-components`. The UI verification above was
also verified in game on
2026-07-23: the bone gizmo remained interactive while animation continued and
the additional pose offset remained applied.
