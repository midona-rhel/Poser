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

A bone is eligible when it has a non-hidden parent; FABRIK and Rope also allow
a bone with same-partial children. Two Joint uses its slot-local limb; other
eligible bones default to FABRIK. Chain settings cannot change during a gesture.

Two Joint/CCD relative targets follow animation. Fixed targets keep the captured target and
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

FABRIK and Rope use Parent depth and Child depth around the selected bone;
that bone remains the only transform handle and target. Each far end is anchored
in actor-model space. Zero disables a side; the two depths total at most 50
links. Defaults are Parent 3 / Child 0, preserving the old ancestor traversal.
There are no direction or endpoint modes.
Child traversal stops at a fork rather than choosing a branch; both walks stop
at hidden bones or a partial boundary. Active chains cannot overlap another
active IK chain and never implicitly connect. Two Joint and CCD are unchanged.

Depth edits capture the visible span and anchors before entering history.
The seed is authored state, not a measurement of the previous solve. The handle
is constrained to the reach of both spans; taut spans cannot be stretched by a
drag. FABRIK bends each side iteratively; Rope retains its hanging-curve solve
on each side. Swivel rotates the bend of each span without moving its endpoints.
Actor targets are model-space points; World targets are world points;
bone/entity targets are world offsets without inherited scale. Missing references
suspend solving. The ordinary bone controls edit the selected handle—there are
no separate root/tip position fields. One drag is one history step.
Scenes store the span, anchors and portable handle reference, restoring the
reference after scene entities exist. Previews snapshot it into their own model
frame. Baking writes the solved pose and disables the chains.

### IK colliders

Colliders are world-space overlay entities, not native game objects or dialogue
UI nodes. Plane, box, cylinder and cone share the overlay lifecycle, selection,
history and scene-file storage. Their Overlay page controls shape, collision
participation, transform locking, visibility and face opacity; the inspector and
world gizmo edit their transform. Hiding a collider does not disable collision.
Planes are finite and two-sided. Rounded surfaces are polygonal but their mesh
seams are not outlined; sharp rims and box/plane edges are.

FABRIK and Rope opt into all enabled colliders through the chain's Colliders
switch. Bone width is the diameter of every segment, in world yalms, independent
of actor scale; only dragging that slider shows the temporary width overlay.
Contacts test segment interiors as well as endpoints, after swivel, and project
whole links outside a face after each forward/backward length sweep. A face
shared by the anchors guides the span instead of neighbouring links choosing
opposite sides. Rope settles downward onto surfaces; FABRIK does not add gravity.
This is a bounded static solve, not accumulated frame-to-frame physics. Endpoint pins remain
fixed. Conflicting pins/obstacles cannot promise clearance; no actor movement or
dynamics is used to hide an impossible arrangement. Two Joint and CCD are unchanged.
If collision passes cannot retain link lengths, they leave the ordinary solved
pose intact instead of publishing stretched bones.
