# Expression, gaze, and IK

## Expression

The expression catalogs come from Ktisis's testing data at
[847f3673](https://github.com/ktisis-tools/Ktisis/commit/847f3673). They include
blinks made for each face and symmetric brows. `tools/Update-ExpressionCatalogs.ps1`
rebuilds them from that commit. This is the only catalog Poser offers.

If a race has no catalog, expressions are unavailable for it. Poser does not
borrow another race's catalog. A face number the catalog doesn't know uses the
first face in that race's catalog.

An expression is one layer on the face. Poser replaces it when the expression
changes and removes it when the expression is cleared. Face edits you made by hand
are left alone. A part the face doesn't have is hidden, not applied to another
bone. Expression changes move with the head.

Catalog values are changes relative to each bone's parent, read against the live
parent when they are applied. Positions use that parent. Rotations are applied to
the bone in the catalog's priority order. Scale factors multiply the bone's own
scale. They are not the older head-relative values and must not be read as such.

### Controls

- **Combine L/R** only changes the layout.
- **Link L/R** writes the same weight to both sides as one drag. Undo gives each
  side back its own earlier value.
- **Unlocked** lets you drag past the normal range, in either direction.
- The limits belong to the control. Undo and redo never apply them.
- Changing these modes does not change the weights you set.
- **Reset** sets the blends to zero on top of the face as you posed it. It does not
  redraw the actor or reset the reference pose.

## Gaze

The gaze controls for the whole actor are in Pose > Other. Expressions are in
Pose > Expression. A pinned Properties window keeps its own actor, and these
controls share that actor's state.

Gaze has five modes: Off, Forward, Camera, Point and Actor. Eyes, Head and Body can
be set separately, and each can lock its current target.

Point mode has one shared anchor and a point for each part. You can type the
numbers or snap a point to the camera. It uses the world gizmo, not the bone gizmo.

The game's own look-at updates are not edits. Your gaze edits go into undo history,
one step per point drag. History scope and ownership are in
[application-state.md](../architecture/application-state.md).

### Which actor

Gaze is stored per actor under Poser's own actor ID, and Actor mode remembers its
target the same way. A GPose copy shares its source's game object ID, so two actors
made from the same player keep separate gaze and can look at each other. The game
object ID is only the value written into the game's target field. Poser refuses to
write gaze outside object indices 201–439.

### Releasing and turning off

Releasing a part stops Poser writing to it, and the game controls it again.

- With no part selected, the mode, target and points are remembered.
- Turning a part off clears its lock.
- Off remembers the target and points but clears the locks.
- `ResetGaze` clears what was remembered.
- Leaving Actor mode also clears the game's target ID.

### Missing targets

A target that disappears stays recorded and is marked stale. Poser stops enforcing
it, does not resume if the ID is used again, and only clears it when you choose a
live target.

If the game functions gaze needs can't be found, gaze shows as unavailable before
anything is written or announced.

### Pose-aware gaze

Pose-aware gaze is an actor setting you opt into. It is saved with the scene's gaze
and is in undo history. Old scenes load with it off.

With it on, Camera and Point targets, and their locked points, keep meaning the
same place in the world while you pose the head and body. Actor and Forward modes
behave as they did.

Each pose pass measures the head and body frames before and after your pose edits,
then moves the next gaze target back through that change. It never compares an
earlier frame's gaze with the current pose, and it never rewrites stored points.
The game still solves the gaze and applies its angle limits. If a frame is missing,
replaced, stale or can't be inverted, the target is used unchanged. The correction
measures Poser's own pose, not later render-time edits from other plugins.

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

## IK colliders

Colliders are overlay entities placed in the world. They are not game objects. The
shapes are plane, box, cylinder, cone, capsule and sphere. They share the overlay's
lifecycle, selection, undo history and scene-file storage.

The Overlay page sets a collider's shape, whether it collides, its transform lock,
its visibility and how see-through its faces are. The inspector and the world gizmo
move it. Hiding a collider does not stop it colliding.

Planes have a finite size and two sides. Rounded surfaces are drawn from polygons,
but their mesh seams are not outlined. Sharp rims and the edges of boxes and planes
are.

### Body colliders

An actor's **Create collider** action makes a named group of at most 48 capsules
and spheres. It covers the body chains and tails Poser recognises on humanoids.

- Limbs follow their bone's axis. The torso, head, hands and feet may use the long
  axis of the surface. Breasts use the same surface fit.
- A sphere is added at a joint only where the neighbouring end caps don't already
  cover it. It uses the smaller of the two radii.
- Each part can be edited or removed on its own. Creating the group is one undo
  step.
- Each part is parented to its bone and follows posing and animation. The parenting
  is rigid and does not apply the bone's captured scale a second time.
- Sizes are those fitted when the group was made, including Customize+ scaling at
  that time. If the proportions change afterwards, make the group again.
- Use Detach for a part that should stay put. Relationships and missing parents
  follow [transform parenting](scenes.md#transform-parenting).
- Hair, ears and skirt chains do not enlarge the fit. Each recognised tail segment
  has its own bone.

This is a rough body shape, not exact clothing collision. A rig Poser doesn't know,
or one that needs more than 48 parts, is refused. None of the actor's triangles
enter the physics world.

### How the fit is made

Every bone the skin is weighted to counts toward which body part owns a piece of
surface. Helper-bone weights are added up before filtering.

Surface samples are weighted by triangle area and limited in number. They fit the
centre, radius and length in the bone's rotation frame. The fit trades penetration
against extra clearance. It does not use the extreme or the average of the vertices.

Where the axis is free, the spread of the surface and the bone directions give a
starting angle, which is then refined within limits. They do not fix the final
direction. Limb axes stay on the bone.

A free-axis region may use two capsules when splitting it cuts the fitting error of
the whole region by at least 25%. The error is measured against both capsules
together, including extra clearance inside either. Nearly round regions keep one
part. All parts share the region's bone and count toward the 48. This changes how
groups are generated, not colliders that are already saved.

### Reading the actor's models

Racial deformation is looked up by the game's original resource, not by a mod's
file name on disk. Matching ignores case and slash direction, and the resolved file
is kept separately. If a mod's identity can't be worked out, or two conflict, the
capture is refused rather than skipping the deformation.

The actor's racial deformer is applied to each model before posed skinning, so gear
shared between races lines up with the skeleton. Models on sibling branches of the
deformer tree are converted through the common ancestor into the actor's race. A
missing ancestor, or a deformation that can't be inverted, refuses the capture.

The extra human `b0003_top` models are not skin and are left out. Body skin hidden
by a material still counts.

Visible triangles whose skin bones can't be resolved are left out. They are never
fitted with part of their weights or with guessed bones. Regions that are valid
still get colliders. The capture reports the triangles and bones it left out and
the regions with too few samples. With no usable surface, no group is made.

The model reader supports V5 and V6 bone tables, using Penumbra's documented V6
layout. Unmapped bones or unsupported vertex formats refuse the capture instead of
using an unposed model. The game is read first, the files are parsed and skinned in
the background, and creation returns to the framework thread.

### Capsule size and editing

A capsule's scale Y is its full tip-to-tip length. Its radius uses the smallest
scale dimension, as a sphere's does. Physics uses the exact round shape, not the
polygons that are drawn.

Physics shapes do not build display meshes. Changing a size rebuilds only the shapes
it affects, not every collider in the chain's world.

Capsule editing shows a radius and the spacing between the end-cap centres, not
separate X, Y and Z scales.

- Changing the radius keeps the spacing.
- The handle along the capsule changes the spacing and keeps the radius. It stops
  when the capsule becomes a sphere.
- Uniform scaling (the Scale centre, or the white outer circle in Universal)
  multiplies both.
- Moving and rotating in the world still work. The size handles are local.
- The inspector uses the same sizes.

Storage stays as total length in Y and diameter in X and Z, with the same history
and collision geometry, so old files are unchanged.

Triangle-mesh colliders saved by older versions still work and are not rewritten.
They draw only outline and open-edge lines, with no fill or inner wireframe, and
still collide on both sides.

How smoothly a collider is drawn is separate from its collision shape. Shared edges
of see-through triangles are not smoothed one by one, because that would show the
mesh seams.

### Collisions for FABRIK and Rope

A chain's Colliders switch makes it collide with every enabled collider. Bone width
is the diameter of every segment, in world yalms, whatever the actor's scale. The
width overlay shows only while you drag that slider.

Each chain with collisions has its own physics world: rigid capsule links, ball
joints and position targets, with fixed substeps and continuous collision
detection. Link lengths come from the chain as captured, not from Rope's sampled
curve.

- The world keeps the last route while you drag. It is not rebuilt each frame from
  a solve without obstacles.
- Only the scene's colliders take part, not other chains or the game's geometry.
- Rope settles downward. FABRIK has no gravity. Bodies at rest sleep.
- The physics state belongs to that one live chain. Changing the solver, depth or
  swivel, or turning collisions off, resets it. Removing the chain, leaving GPose
  and unloading free its buffers. Another actor or a preview never shares it.
- A snapshot stores the route you see as the receiving skeleton's starting shape.
  It does not store velocities.

Bone frames share one roll reference at the root and are carried along the solved
chain, keeping the roll each bone was given. A link never builds up roll from how
it moved, and never picks up a round capsule's free spin. The root frame stays
smooth when the chain folds back.

The handle pulls with limited force, so a taut chain wrapped around something can
stop short of its target. A drag is limited to the chain's reach, and travel past
it is thrown away, so dragging back responds at once. Lag caused by collision does
not feed back into the target.

A drag starts from where the gizmo is, which after contact can differ from the held
target. The reach limit uses that start position for the whole drag, and the
accepted point is stored as an offset. Movement from the solve during the drag does
not move the start.

Conflicting anchors, or pushing geometry through a chain that is already pinned,
can stay unresolved. Collision does not promise that every arrangement is possible.
