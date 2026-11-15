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

Actor-wide gaze controls live in Pose > Other; expressions live in Pose >
Expression. The selection inspector contains neither section. Pinned Properties
windows retain their own actor target while these controls share actor state.

Gaze has Off, Forward, Camera, Point, and Actor modes. Eyes, Head, and Body can
be controlled separately, and each can lock its current target. Point mode has
one shared anchor plus per-part points, numeric editing, and camera snap. It
uses the world gizmo, not the bone gizmo. Native look-at updates are not edits;
user gaze edits use the shared value journal, with one step per point drag.
History scope and ownership are in [application-state.md](../architecture/application-state.md).

Gaze state is keyed by the actor's stable binding id, and Actor mode remembers
its target the same way. A GPose clone shares its source's game object id, so
two player-seeded actors hold independent gaze and may look at each other; the
game object id is only the value written into the native target. Writes outside
indices 201–439 are refused.

Releasing a part stops Poser writes and lets the game control that part again.
An empty mask remembers the configured mode, target and points; disabling a
part clears its lock. Off remembers the target and points but clears locks;
`ResetGaze` clears the remembered state. Leaving Actor mode also clears the game's target id.

A missing target stays recorded and is marked stale. Poser stops enforcing it,
does not resume on id reuse, and clears it only when a live target is chosen.
Missing gaze signatures or hooks produce an unavailable state before native or
event side effects.

Pose-aware gaze is an opt-in actor setting, saved with scene gaze and journaled.
Old scenes default off. Camera and Point targets, including their locked points,
retain world-space intent; Actor and Forward modes keep their existing behavior.
The pose pass measures the same native head/body frames before and after authored
transforms, then inverse-maps the next native target through that change. It never
compares yesterday's gaze output with today's pose or rewrites stored target
points. Native gaze remains responsible for solving and its angular limits.
Missing, replaced, stale or singular frames fall back to the unmodified target.
The correction measures Poser's update-phase pose, not later render-only edits
from other plugins.

## IK

### Solvers

Poser has four IK solvers. Two Joint and CCD are the game's own. FABRIK and Rope
are Poser's.

A bone can use IK when it has a visible parent in the same part of its skeleton.
FABRIK and Rope can also start from a bone that has children in that part. Hands
and feet default to Two Joint. Every other bone that can use IK defaults to
FABRIK.

IK settings belong to one skeleton. They can't be changed in the middle of a
drag. Two active chains can't share a bone, and chains never join up on their own.

CCD reports the bones it moves from its own chain. It does not follow the display
links between skeleton parts, and it includes hidden root bones inside the part.

### What is saved and undone

A chain is solved again every frame. Undo records the handle edits and the
settings, never the solver's result. One drag is one undo step.

Dragging the handle moves the solver's target. The end bone itself is never moved
first, because that would change the limb's length.

### Targets

A chain holds its end bone on a target. The target mode decides what the target
follows.

- **Actor.** For Two Joint and CCD the target follows the first bone above the
  chain, everything above that bone, and the actor itself. Bending a joint inside
  the chain does not move it. For FABRIK and Rope it is a fixed point in the
  actor's model.
- **World.** The target stays at a point in the world while the actor moves.
- **Bone** and **Entity.** The target follows another bone, or a prop, scenery
  object, light or effect. It keeps the offset and relative rotation the end bone
  had when the target was picked. Moving the end bone changes that offset. The
  target's scale is not inherited. Keep rotation decides whether the end bone
  turns with the target.

A target that goes missing stays recorded and is never swapped for another one.
The chain stops solving until you pick a new target or choose Detach, which holds
the current world point.

Changing the target of an active chain keeps the end bone where it is. Only what
it follows changes.

### Enabling, disabling and switching solver

Enabling a chain moves nothing. The chain captures the limb in the pose the solver
works on, not the pose on screen, which Customize+ has edited afterwards (see
[Customize+ and the displayed pose](../architecture/posing-runtime.md#customize-and-the-displayed-pose)).
Two Joint and CCD capture on their first solve. FABRIK and Rope leave the limb
alone for one frame and capture from the pose that frame produced.

Disabling a chain keeps its settings and drops what it captured. The limb can be
posed while the chain is off, so enabling it again captures the limb as it stands
then. Undoing the switch-off restores the chain as it was.

Switching the solver keeps the end bone where it is. FABRIK and Rope start from
the pose Two Joint or CCD left, never from an older capture. Two Joint and CCD aim
at the end bone where FABRIK or Rope left it. A bone only the old solver owned,
such as the clavicle under FABRIK's default depth, goes back to its pose, and the
middle joints follow the new solver.

Undo and redo restore the captured target together with the setting.

### Reach and drags

With Constraints on, a Two Joint or CCD handle can't be dragged past the limb's
reach. Two Joint also keeps to its hinge limits. Travel past the limit is thrown
away, so dragging back moves the limb at once. With Constraints off the handle
moves freely. Rotating or scaling the end bone never moves the handle.

The reach is measured in the pose the solver produced, so the limb can always be
pulled straight. A drag is measured on screen and converted to that pose first.
The conversion is measured, not read from any bone's scale: Poser compares how the
limb's bones sit relative to each other on screen and in the solved pose, from the
bone above the limb down to its end. That follows a scale Customize+ passes down
from any bone above the limb, including a different scale per axis, and ignores a
scale that isn't passed down.

### End rotation

Two Joint has one switch for how the end bone turns. Off, the default, keeps the
end bone's rotation while the limb is solved or swivelled. With a World, Bone or
Entity target it follows the target's rotation. On lets the end bone turn with the
limb.

Flipping the switch moves nothing. Saved chains behave as saved: the stored value
`EnforceEndRotation` means "hold", so the switch shows its opposite. The other
solvers use Keep rotation, which never overrides Two Joint's switch.

### FABRIK and Rope

FABRIK and Rope take a number of bones above the selected bone (Parent depth) and
below it (Child depth). The selected bone is the only handle. Each far end stays
where it was in the actor's model. Zero turns a side off. The two depths add up to
at most 50 bones. The default is 3 above and 0 below.

The walk downward stops at a fork instead of picking a branch. Both walks stop at
a hidden bone or at the end of the skeleton part.

FABRIK bends each side. Rope lets each side hang. Swivel turns the bend of each
side without moving its ends. The handle can't be dragged past the reach of both
sides, and a taut side can't be stretched.

Changing the depth picks from the bones captured when the chain was enabled, up to
50 on each side. Bones you take out keep their captured shape, so lowering the
depth and raising it again gives the same limb back. A depth change never captures
the solved limb again. If the skeleton was rebuilt and now has a bone outside what
was captured, the depth change is refused and nothing changes. Turn the chain off
and on to capture the new skeleton. Copies and previews capture their own.

The ordinary bone controls edit the handle. There are no separate fields for the
far ends.

### Importing, resetting and baking

Importing a pose does not move IK targets. Solving is paused for that actor while
the file is applied, then the existing chains carry on. The bones below an IK limb
are imported against the pose without IK, so they keep their place relative to it.

Resetting the pose keeps the offset of a handle a chain is holding. Undo and redo
restore both together. Reset Defaults keeps Enabled, Reset Bone keeps IK, and Reset
All turns off and clears every chain.

Bake writes the selected chain's solved bones into the pose and turns that chain
off. It waits for the pose to settle first and is one undo step. Other chains keep
their targets and settings. Undo brings back the chain and its earlier edits. Redo
bakes it again.

Previews copy the chains into their own model. Older scenes may store a FABRIK
chain's bones, far ends and handle; they still load, after the scene's entities
exist. New saves follow the
[baked snapshot contract](files-and-transfer.md#storage-and-library).

### IK colliders

Colliders are world-space overlay entities, not native game objects or dialogue
UI nodes. Plane, box, cylinder, cone, capsule and sphere share the overlay lifecycle, selection,
history and scene-file storage. Their Overlay page controls shape, collision
participation, transform locking, visibility and face opacity; the inspector and
world gizmo edit their transform. Hiding a collider does not disable collision.
Planes are finite and two-sided. Rounded surfaces are polygonal but their mesh
seams are not outlined; sharp rims and box/plane edges are.
An actor's **Create collider** action creates a named group
of at most 48 ordinary capsules/spheres for recognized humanoid body chains and tails.
Limbs follow their bone axes; torso, head, hands and feet may use the surface's
long axis. Breast regions use the same surface fit. Joint spheres are added only where endcaps
do not already cover the joint, using the smaller adjacent radius.
Each part can be edited or removed
individually; creating the group is one undo step. Each part is automatically
parented to its corresponding bone and follows posing/animation. Dimensions
remain those fitted at capture time, including the final native Customize+ scaling;
changing proportions afterward requires regenerating the group. Parenting is
rigid and must not apply the captured bone scale a second time.
Use Detach for a static part; relationships
and missing-parent behavior follow [transform parenting](scenes.md#transform-parenting).
All skin influences contribute to anatomical ownership, summing helper-bone
weights before filtering. Triangle-area-weighted, bounded surface samples fit
center, radius and stem in the bone's rotation frame, balancing penetration
against excess clearance rather than using extreme or mean vertex bounds.
For free-axis sections, covariance/bone directions seed a bounded angular
refinement; they do not lock the final orientation. Limb axes remain bone-aligned.
Free-axis regions may use two capsules when splitting the measured surface
reduces whole-region squared fitting error by at least 25%. Error is measured
against the union, including excess clearance inside either capsule. Nearly
round regions retain one part; all parts share the region's bone owner and the
existing 48-part actor budget. This changes generation, not saved colliders.
Racial deformation uses the original game resource identity, not a mod's disk filename.
Resource matching normalizes separators and ignores case while retaining the
resolved file separately. Unidentified or conflicting mod identities refuse
capture rather than silently skipping deformation. Auxiliary human `b0003_top`
models are not skin surfaces and are excluded (also excluded by Meddle's
character exporter); material-hidden body skin remains eligible.
Visible triangles with unresolved skin bones are omitted, never fitted with
partial weights or guessed replacement bones. Valid regions still generate;
the capture reports omitted triangles/bones and regions without enough samples.
No usable surface means no collider group is created.
Hair, ears and skirt chains do not inflate the fit; each recognized tail segment
has its own bone owner. This is a coarse body approximation, not exact clothing
collision; unknown or over-budget rigs refuse capture. No actor triangles enter
the physics world. Capsule scale Y is total tip-to-tip length; its round radius
uses the smallest scale dimension, also used by spheres, never a polygonal hull.
Analytic physics inputs do not build display meshes; dimension changes rebuild
only affected obstacle shapes rather than every collider in the chain's world.
Capsule editing exposes radius and endcap-center spacing instead of independent
XYZ scale. Radius edits preserve spacing; the local longitudinal handle changes
spacing while preserving radius, stopping at a sphere. Uniform scaling (Scale
center or Universal white outer circle) multiplies radius and spacing equally.
World translation/rotation remain available; capsule dimension handles are local.
The inspector uses the same dimensions. Edits retain the existing total-length
Y / diameter XZ storage, history and collision geometry; old files are unchanged.
Human capture applies the actor's resolved racial deformer per model before
posed skinning, so shared-race equipment lines up with its actor skeleton.
Existing saved triangle-mesh captures remain supported and are not silently
rewritten. They draw silhouette and open-boundary lines only, without triangle
fill or internal wireframe, and retain their two-sided surface contacts.
The reader supports V5 and V6 bone tables using Penumbra's documented V6 layout;
unmapped bones or unsupported vertex formats refuse capture instead of silently
substituting an unposed model. Native reads precede background file parsing and
skinning; creation returns to the framework thread.
Racial deformation supports models on sibling branches of the active PBD tree:
convert from the model through the common ancestor into the actor's race before
posed skinning. Missing ancestry or non-invertible source deformation refuses
capture rather than silently dropping a model or substituting identity.
Overlay tessellation is independent of collision geometry. Shared translucent
triangle edges are not anti-aliased individually, which would expose mesh seams.

FABRIK and Rope opt into all enabled colliders through the chain's Colliders
switch. Bone width is the diameter of every segment, in world yalms, independent
of actor scale; only dragging that slider shows the temporary width overlay.
Bepu runs a headless world per collision-enabled chain: rigid capsule links,
ball-socket joints and positional targets, with fixed substeps and continuous
collision detection. Link lengths come from the authored span, not Rope's sampled
curve. The physics world retains the last route while dragging; it is not rebuilt
from an obstacle-free FABRIK/catenary result each frame. Only the authored scene
colliders participate, not other chains or native game geometry. Rope settles
downward; FABRIK adds no gravity. Settled bodies sleep. Two Joint and CCD are unchanged.
Continuation belongs to the exact live chain; solver/span/swivel changes and
disabling collisions reset it. Removal, GPose exit and unload dispose its native
buffers. Another actor or preview never shares it. Snapshots capture the visible
route as the receiving skeleton's authored seed, not physics velocities.
Bone frames share a root roll reference and are transported along the solved
span, preserving authored relative roll. Links must not independently accumulate
roll from their motion history or adopt a round capsule's free spin. The root
frame continues smoothly when folding backwards.
The selected handle has limited pulling force so a taut wrapped span can stop
short of its target. FABRIK/Rope translation edits clamp their authored target to
the span's distance limits; rejected drag travel is discarded so reversing responds
immediately. Collision lag does not feed back into that target.
Dragging starts from the visible gizmo position, which can differ from the held
target after contact. Reach limiting uses that frozen input position, and the
accepted point is converted back to an authored offset; solved motion during the
gesture does not move its input baseline.
Conflicting anchors or inserting geometry through an already
pinned chain can remain unresolved; collision is not a guarantee that every
requested arrangement is physically possible.
