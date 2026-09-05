# Selection and transforms

All surfaces share one ordered selection list. Changing mode does not clear it.
Ctrl toggles compatible targets, Shift selects the visible range, and symmetry,
linked lookup, ancestry, and parent traversal stay within a slot.

One drag or typed edit is one gesture and one history patch. Baselines freeze at
pointer-down; each frame applies the total delta from those baselines. Escape,
tool/space/pivot changes, and selection changes cancel the gesture.
Multi-selection applies each target's delta to its own baseline. Ring drags
keep pointer ownership through release, so ending a drag cannot pick a bone.

Frame wells edit model-space values. World and Local gizmo spaces keep their
different axis meanings. Self rotates in place; Parent orbits around the
frozen parent position. The world overlay places its pivot in perspective and
draws nothing for an unprojectable pivot. Inspector rotation stays in place.

World-gizmo size calibration uses the camera image plane, keeping its reference
pixel span stable across the viewport. Rotation rings are an oriented ball at
fixed pivot depth: they tilt with the axes but do not perspective-warp with
screen position. Ring drawing, picking, positive tangents and drag sweeps use
that same projection; linear handles and world translation retain perspective.
The white roll circle uses the requested pixel radius, never the furthest
projected axis-ring sample; drawing, picking and drag sweep share that radius.
Linear handles face the camera's position relative to the pivot, not its look
direction; their signs remain frozen during a drag.
Scene markers take priority over idle gizmo handles. Once a drag begins it
retains the pointer through release; crossing a marker cannot select it or
start another action. Marker picking resolves before idle gizmo hover.

Rotation arcs grow continuously from half circles at 20 degrees off face-on
to full circles at 5 degrees, staying full closer in, on both gizmos.
Drawing and picking share the same clipped arc
endpoints; the inspector's remaining rear arc stays faint and non-interactive.
Each projection uses its own viewing direction. Roll and gesture axes do not
change as the arc grows.

New lights and **Move to camera** share a one-yalm camera-forward placement,
with local +Z aligned to the look ray. Brio and Ktisis place lights exactly
at the eye; Poser deliberately retains a nonzero offset so its world gizmo
pivot can be projected. Move preserves scale and records one undo step.
The light inspector and light context menu invoke the same move command.
Clones and imported lights retain their supplied transforms rather than using
this camera placement.

The overlay visibility mask is the only writer for bone visibility. A selected
anchor stays visible when the mask hides other bones. Presets are sets of
canonical names whose applied state comes from that mask.

Precision wells support modifiers, numeric entry, Escape cancellation, and
wheel steps. A wheel notch belongs to the hovered well, uses that well's drag
modifiers, and commits one undo step; unclaimed wheel input scrolls the surface.

## Group transform read model

An entity multi-selection, including an exact named-group selection, has one
group transform surface. A group owns a frozen creation-camera frame, a complete
initial member snapshot, the exact expected member transforms from its last
accepted gesture, and authored controls. Reads return those authored controls
only after validating every live member against the expected snapshot; an
external or unrepresentable edit disables the surface instead of producing a
plausible geometric fit. The record is keyed by effective membership, never
selection order, binding generation, or the primary member. Position is the
world centroid; authored rotation is relative to the retained creation frame.
New frames (including legacy imports without a saved frame) capture only the
camera's ground-plane heading: world Y stays up, with no camera pitch or roll.
Near a vertical view, projected camera right supplies a stable heading.
Origin/centroid is unchanged. Explicit saved
frames and authored rotations are preserved, not flattened or migrated; later
user-authored X/Z rotations can deliberately tilt the group.
World rotation deltas are conjugated through that frame. The group's world
orientation is `creationFrame.Rotation * authoredRotation`,
never a member's rotation. Local overlay handles use that orientation; World
translation and rotation handles remain world-aligned. Scale handles always
use the group orientation. Spacing scale converts centroid offsets into those
axes frozen at gesture start, multiplies components, then converts back to
world space. Numeric scale uses the same axes, independent of the World/Local
toggle. Member own-size scaling remains native component scaling, without
affine decomposition. Active previews derive from the frozen gesture baseline.
Both surfaces read the transaction's proposed snapshot during a group gesture,
validated against all live members; committed metadata and history remain
unchanged until commit. Presentation reads refuse during native writes or
recovery, and for stale or unrelated transactions. They never admit a second
gesture or repair state.

`SpacingOnly` and `SizesAndSpacing` keep separate authored spacing and own-size
factors. The displayed scale is the factor for the selected mode; changing
mode alone changes no members and cancels a held gesture. Sizes-and-spacing applies one gesture ratio to
both current offsets and current member sizes, while spacing-only changes
offsets alone. Group writes still use the existing frozen-baseline gesture and
journal recovery path. Native snapshots, metadata and the history cursor finish
together, including delayed recovery and whole-actor snapshot fallback.
Authored factors need only be finite and nonzero; member poses separately obey
native scale limits. Every output is validated before the first native write.

Named state is captured after a command's final effective membership is assembled;
nesting and duplicate assembly include descendants in that same history step.
For an existing group, adding/removing/nesting members updates only membership,
member snapshots and the average world position. Authored rotation, both scale
factors and the exact creation frame persist; no member moves, rotates or scales
as a side effect. The updated centroid is the next gesture's pivot. Membership
undo/redo restores that membership and centroid together with the preserved
controls. Genuinely new groups initialize rotation once and scale at 1;
reordering or temporary editability refusal does not reinitialize group state.
If membership capture was deferred, history restoration completes it explicitly
and freezes the result. While still unavailable, restoration changes no structure
or native members and remains retryable; ordinary reads never complete it.
Anonymous state initializes at selection boundaries, retains at most 128 logical
memberships, and clears with the scene session. Reads never initialize state.
Saves capture metadata with scene state on the framework thread. Document keys
bind both snapshots; origin and yaw placement move them and the frame together.
Legacy groups initialize after binding. Invalid present metadata is refused,
never replaced with a plausible identity baseline.

## The journal

Every change made through the UI is one step with an inverse. Undo runs
the inverse; redo runs the step again. Drags are one step on release;
typed fields are one step on commit. Selection is never a step.

Each step remembers the state of every actor it touched: the exact actor
and skeleton generations, the timeline and loop choices, and the
disruption epoch that a redraw, a character file or an appearance apply
bumps. When an actor's state no longer matches, the step is invalid: undo
does not apply its delta to a body that is not the one it was recorded
on. It restores the actor's whole pose from the snapshot the step kept,
and says so in one notice. A restore is a pose import, so an animating
actor pauses for it.

A step that came from a file (a pose import, a scene load) checks the
file before redo and refuses with one notice when it is gone.

Baking IK is one step: undoing it puts the bones back and re-arms the
chains the bake disarmed.

Transport (play, pause, scrub, speed) is never a step. Choosing a
timeline and toggling loop are. A locked camera never journals.

The depth is 500 steps by default (Settings › Undo steps).
