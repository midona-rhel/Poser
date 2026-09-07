# Expression, gaze, and IK

## Expression

Expression catalogs use Ktisis testing data at
[847f3673](https://github.com/ktisis-tools/Ktisis/commit/847f3673), including
face-specific blinks and symmetric brows. If a race catalog is
missing, the feature is unavailable; Poser does not substitute another race.
Unknown face numbers use the first face in that same catalog, as Ktisis does.
Expression changes move with the head. Poser replaces its expression layer when
the expression changes and removes it when cleared. Manual face layers are left
alone. Missing parts are hidden rather than applied to another bone.
The new catalogs are parent-local deltas, resolved against the live Havok parent
during application; they must not be interpreted as the old head-relative data.
As in Ktisis, translations use that parent, rotations post-multiply the bone
in catalog priority order, and scale factors multiply the authored baseline.
Only the new catalog is offered; legacy Sneer definitions are not retained.
`tools/Update-ExpressionCatalogs.ps1` reproduces the normalized data from that ref.

Combine L/R controls layout; Link L/R writes equal weights to both sides as one
gesture, with each original value restored on undo. Unlocked uses signed,
unbounded numeric drags. Bounds belong to the control, never history replay.
Changing these UI modes does not change the authored weights. Reset zeroes
blends against the authored base face, without redraw or reference-pose reset.

## Gaze

Gaze has Off, Forward, Camera, Point, and Actor modes. Eyes, Head, and Body can
be controlled separately, and each can lock its current target. Point mode has
one shared anchor plus per-part points, numeric editing, and camera snap. It
uses the world gizmo, not the bone gizmo. Gaze writes never enter transform
history.

Actor mode needs a live scene target. Poser finds that actor in the GPose range
immediately before writing. Finding a matching game object id alone does not
prove it is the right target. Writes outside indices 201–439 are refused.

Releasing a part stops Poser writes and lets the game control that part again.
Mode, target, points, and locks remain after an empty mask or Off;
`ResetGaze` clears them. Leaving Actor mode also clears the game's target id.

A missing target stays recorded and is marked stale. Poser stops enforcing it,
does not resume on id reuse, and clears it only when a live target is chosen.
Missing gaze signatures or hooks produce an unavailable state before native or
event side effects.

## IK

Two Joint and CCD use the game's Havok solvers; FABRIK and Rope use managed
solvers. Relative translation edits or held targets drive solving during pose
application. Solver iterations are not history: authored deltas and configuration
are. Configuration belongs to one skeleton instance.

Imported pose transforms do not edit IK targets (Brio likewise marks imported
stacks IK-disabled). Held chains solve against the combined explicit handle
edits, not each imported stack. Reset first clears the pose while retaining a
held handle's offset in the same stack snapshot used by undo/redo.
The importer suspends solving for its actor while it measures and applies the
file, then resumes the existing constraints. Descendants are imported against
the unconstrained parent pose, so they remain relative when IK resumes.
Held handle translations are solver targets, never direct pre-solve tip writes.

A bone is eligible when it has a non-hidden parent. Two Joint uses its
slot-local chain; other eligible endpoints use CCD. Chain settings cannot
change during a gesture.

Relative targets follow animation. Fixed targets keep the captured target and
authored translation, so changing mode does not jump. IK bake disables the
chain, waits for the pose to settle, and writes affected bones as one
raw-baseline history entry. Disabling keeps tuning and clears only fixed
capture. Reset Defaults keeps Enabled, Reset Bone keeps IK, and Reset All
disables and clears every chain.

Scene entity targets follow props, scenery/world objects, lights and VFX through
their exact stable scene IDs; they do not need a skeleton. Attachment captures
the tip's current world-space position offset and relative rotation, matching
Bone mode. Moving the tip edits that offset; target scale is not inherited.
Keep rotation controls orientation following. Missing targets remain recorded
and unavailable, never rebound to a replacement; choose another target or
Detach to hold the current world point. Inspector targeting uses the existing
IK configuration control, while the runtime resolves the live transform.

FABRIK retains Depth (at most 50 links). Forward pins the root, Reverse pins
the tip, and Bidirectional exposes both endpoint targets. Direction changes
capture the visible chain and both endpoints without reparenting native bones
or moving the actor. The authored chain seed and endpoint descriptors belong
to configuration/history; repeated evaluation never measures new link lengths
from its own previous output. Two Joint, CCD and Rope keep their existing paths.
In unreachable configurations, Forward/Bidirectional prioritize the root;
Reverse prioritizes the tip. Links remain their captured lengths. Actor targets
are model-space points; World targets are world points; bone/entity positions
are world offsets without inherited scale. Missing references suspend the solve.
Scenes store portable references and restore targets after all entities exist;
previews instead snapshot both targets into their own model frame. An endpoint
drag is one history step. Baking writes the solved pose and disables the chain.
Controlled FABRIK chains cannot overlap another active IK chain; they never
implicitly connect. Inspector and bone context menus share direction controls.
