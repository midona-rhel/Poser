# Selection and transforms

## Selection

Every surface shares one ordered selection list. Changing the mode does not clear
it.

- Ctrl toggles a compatible target. Shift selects the visible range.
- Symmetry, linked bones, ancestors and parents are looked up inside one slot.
- A plain click replaces the selection.

Bone dots on the overlay have no list, so there is no range. Shift-click toggles
single bones in both picking modes, and Ktisis mode also keeps Ctrl-click. The
gizmo takes the pointer before bone picking does.

The Body, Face and generated Bones maps select on click, and Ctrl-click or
Shift-click toggles more bones. Dragging on empty map draws a box and selects the
dots inside it (Ctrl or Shift adds). The box stays inside the map and the drag
never moves the window. Dragging a point in the preset editor only changes the
layout.

`Block game mouse targeting` is on by default in General settings. It blocks the
game's own mouse targeting, in GPose only. Poser's selection, explicit target
commands and the two target-sync preferences are separate from it. Turning it off
brings game targeting back at once.

## Drags and typed edits

One drag or one typed edit is one undo step.

- The starting values are frozen when the pointer goes down. Each frame applies the
  whole change from those starting values.
- With several targets, each gets the change applied to its own starting value.
- Escape cancels the drag. So does changing the tool, the space, the pivot or the
  selection.
- A ring drag keeps the pointer until release, so ending a drag can't pick a bone.

### World and Local

The number fields edit values in the model's space.

- **World**: the gizmo's axes stay lined up with the world, also for bones on
  attached entities. The drag is converted back into the skeleton's model space
  before it is applied.
- **Local**: the axes follow the bone's orientation in the world.
- The Scale tool always uses local handles, whatever World/Local says.
- Universal in World mode hides the directional scale handles, also for groups and
  capsules. It keeps uniform scaling at the centre (unless the centre is set to
  move) and the capsule's uniform-scale ring. Hidden handles don't draw and don't
  take clicks. Typing a scale in the inspector is unchanged.

Directional scaling along world axes would need shear, which the game's transforms
can't hold.

### Locks and pivots

A transform lock disables editing in the inspector but still shows the real values,
not identity. That covers colliders, single locked members of a group and group
transforms you can read. Group membership and stale-state checks still apply.

Self rotates in place. Parent orbits around where the parent was when the drag
began. Rotation in the inspector stays in place. The world overlay places its pivot
in perspective and draws nothing when the pivot can't be projected.

### Precision fields

Number fields support modifier keys, typing, Escape to cancel and wheel steps. A
wheel notch belongs to the field under the pointer, uses that field's drag
modifiers and is one undo step. Wheel input no field claims scrolls the surface.

## The gizmo on screen

### Size and the rotation ball

The world gizmo's size is measured on the camera's image plane, so it covers the
same number of pixels anywhere in the view.

The rotation ball faces along the line from the eye to the pivot and keeps the
camera's roll. The pivot is placed in perspective, but the ball and the white roll
circle keep their set pixel radius everywhere on screen. They don't stretch with
perspective, shrink, or grow to fit. Drawing, picking and dragging all use that
same frame. Poser's ball has a fixed screen size on purpose, unlike
[ImGuizmo's fitted outer circle](https://github.com/CedricGuillemet/ImGuizmo/blob/master/src/ImGuizmo.cpp).
The inspector keeps its own projection, which only uses direction.

Straight handles keep world perspective. They face where the camera is relative to
the pivot, not where it looks, and their direction stays fixed during a drag.

### Rotation arcs

On both gizmos an arc grows smoothly from a half circle at 20 degrees off face-on
to a full circle at 5 degrees, and stays full closer than that. Drawing and picking
use the same clipped ends. In the inspector the rest of the rear arc stays faint
and can't be clicked. Each projection uses its own viewing direction. Roll and drag
axes don't change as an arc grows.

### What a click hits

Drawn gizmo handles, with a two-pixel margin, are checked before the padded hit
areas of scene markers. An arrowhead uses its triangle, not a larger circle.
Outside the drawn handles, markers can still be selected, and a hover from the
previous frame can't block them. Once a drag starts it keeps the pointer until
release, so crossing a marker can't select it or start something else.

### Hiding handles

Entities and named groups have a handle button on their sidebar row and the same
action in the context menu. Hiding the handle hides both the selection marker in
the world and the selected world gizmo, also when an actor's root or a bone is
selected. Selecting never overrides it.

- Bone highlights, skeleton and IK overlays, inspector editing and object
  visibility are separate from this.
- Hidden controls can't take clicks. A drag already under way finishes first.
- The choice lasts for the session. It is not a global setting.
- A named group's choice is stored under its own ID, apart from its members'.
- An unnamed selection hides its shared gizmo if any selected entity's handle is
  hidden. Which entities it transforms does not change.
- Showing the handles again uses the current transforms and centre.

### Bone visibility

Only the overlay's visibility mask decides which bones show. A selected anchor bone
stays visible when the mask hides the others. A preset is a set of bone names, and
whether it is applied is read from that mask.

## Placing at the camera

New lights and **Move to camera** both place the entity one yalm in front of the
camera, with its local +Z along the view. Brio and Ktisis put lights exactly at the
eye. Poser keeps the offset on purpose so the world gizmo's pivot can be drawn.

Move keeps the scale and is one undo step. The light inspector and the light's
context menu run the same command. Copies and imported lights keep the transform
they came with.

## Group transforms

A selection of several entities has one group transform. So does a named group
when exactly its members are selected.

### What a group stores

- the camera frame at the moment the group was made, which never changes;
- a full snapshot of its members at that moment;
- the exact transforms its members had after the last accepted drag;
- the controls you set: position, rotation and scale.

Reading the group returns those controls only after checking every live member
against the snapshot it expects. If something outside changed a member, or changed
it in a way the group can't express, the group surface is disabled. Poser does not
fit a plausible-looking transform to the members.

The record belongs to the set of members. It does not depend on selection order,
on which member is primary, or on a member being replaced by a redraw.

### Position, rotation and scale

- Position is the centre of the members in the world.
- Rotation is relative to the stored creation frame. The group's orientation in the
  world is `creationFrame.Rotation * authoredRotation`, never a member's rotation.
- A new frame takes only the camera's heading on the ground. World Y stays up, with
  no camera pitch or roll. Looking almost straight down, the camera's right
  direction gives a stable heading. Older files without a saved frame get one the
  same way. The centre is not changed.
- A frame or rotation saved in a file is kept as it is. You can still tilt the
  group later by rotating it around X or Z.
- Local handles use the group's orientation. World move and rotate handles stay
  lined up with the world. Directional scale handles use the group's orientation.
- Scaling the spacing turns the offsets from the centre into the group's axes as
  they were when the drag began, multiplies them, and turns them back. Typed scale
  uses the same axes, whatever World/Local says.
- Scaling a member's own size uses the game's per-axis scale, with nothing split
  out of a matrix.

`SpacingOnly` and `SizesAndSpacing` keep separate factors. The scale shown is the
one for the selected mode. Changing the mode moves nothing and cancels a drag in
progress. Sizes-and-spacing applies one ratio to both the offsets and the members'
sizes. Spacing-only changes the offsets alone. A factor only has to be finite and
not zero. Each member still obeys the game's scale limits, and every result is
checked before the first write.

### During a drag

Previews come from the values frozen when the drag began. Both surfaces read the
drag's proposed result, checked against every live member. The stored group and the
undo history don't change until the drag is committed. Reads are refused while a
write or a recovery is running, and for a drag that is stale or unrelated. A read
never starts a second drag and never repairs anything.

Group writes use the same frozen-start drag and the same recovery as other
transforms. The members, the group's stored values and the undo position finish
together, also when recovery is delayed.

### Membership

Named state is captured once a command has put together its final set of members.
Nesting and duplicating include the descendants in that same undo step. Generated
collider groups do the same after their parts are parented to bones. Their history
keeps both the membership and the transform data, and Redo keeps the creation frame
without capturing the camera again.

Adding, removing or nesting members in an existing group updates only the
membership, the member snapshots and the centre. The rotation, both scale factors
and the creation frame stay. No member moves, turns or scales as a side effect. The
new centre is the pivot for the next drag. Undo and redo of a membership change put
back the membership and centre together with those kept controls.

A truly new group starts with its rotation set once and scale 1. Reordering, or a
moment when a group can't be edited, does not start it over.

If capturing the members was put off, restoring from history finishes it and
freezes the result. While that still isn't possible, the restore changes nothing
and can be tried again. Ordinary reads never finish it.

Unnamed selections get their state when the selection changes. Poser keeps at most
128 of them, and they are cleared with the scene session. Reads never create state.

### Saving

A save captures the group data with the scene on the framework thread. The file
ties both snapshots to their document keys, and placing the scene by origin or
heading moves them and the frame together. Older groups get their state after they
are bound. Group data that is present but invalid is refused. It is never replaced
by something plausible.

## Camera movement

**Center camera on actor** moves the live orbit view to the actor's mid-body point
as drawn, which is half its height. It does not change zoom, field of view,
direction or what the camera follows, and it does not refit the actor to the frame.
It never creates a camera and never changes a parent. Close framing on a bone is a
separate action.

It is refused before any camera write for a free, locked or pinned camera, and for
an actor that is unavailable, hidden or not drawn. It is in the actor menu and in
the Inspector under Actor → Camera.

A new free camera takes its position, direction and roll from the view as it is
drawn. That keeps the view when you spawn from another free camera or from a Main
Camera that was panned or rolled.

`Update camera orbit with actor position` is on by default, as in
[Ktisis 650d1bb9](https://github.com/ktisis-tools/Ktisis/commit/650d1bb9).

- When you move an actor, the camera's pivot is updated when the left mouse button
  is released. While the live camera is locked the update waits, and unlocking
  applies only the latest position.
- Turning the setting off drops any waiting update. Turning it on affects later
  edits.
- Following an actor or bone explicitly is separate.
- The actor's original position and default position are restored when its
  override is cleared, on leaving GPose and when the plugin unloads.

### Camera moves in undo history

Moving the live camera with the game's controls, or a free camera with its keys,
records nothing while it moves. Once the framing has stayed still for five seconds
it becomes one `Move camera` step from where it last rested. Moves less than five
seconds apart are one step.

The framing is a free camera's position and rotation, the game camera's angle, pan,
zoom and offset, and roll and field of view.

- If another step is added while a move is still settling, the move is recorded
  first, up to the last framing seen.
- Poser's own camera edits, undo, redo and switching cameras reset the resting
  framing and are not recorded as a move.
- A locked camera never records anything.

### Starting up and switching

Camera startup checks the current GPose session on its first framework tick, also
when the plugin reloads inside GPose. It waits until the game's camera is ready and
creates the default camera once. You do not need to enter GPose again.

Undo and redo of creating or removing a camera restore the camera's data without
switching to it. Spawning a camera and Look through still switch the view as usual.
Removing the live camera falls back to Main Camera. Replaying a Look through step
still changes the view.

A scene's cameras are set up and given their targets while inactive, and then the
saved view is switched to once. When Poser sets an orbit angle itself, as on scene
load or a camera switch, the game's pull toward the previous view is skipped for
one update, so the saved angle is not dragged off. Normal camera input carries on
afterwards with nothing held.

Free-camera movement reads held keys through Dalamud's `IKeyState`. The game's
keyboard state is used only to block game input, never to drive the movement.

## The journal

Every change made through the UI is one step that can be undone. Undo runs the
step's inverse and redo runs the step again. A drag is one step on release. A typed
field is one step when it is committed. Selection is never a step.

The history holds 500 steps by default (Settings › Undo steps).

### Sliders and fields

Sliders and draggable number fields update the value live, then record one
before-and-after entry on release or when the field loses focus. Going back to the
original value records nothing. Linked values are recorded as one entry. Closing a
control also records its edit once you stop interacting.

Gaze drags work the same way. Switches and other one-off actions are recorded at
once, and separate clicks are never merged.

### What is never a step

- Selection.
- Transport: play, pause, scrub and speed, for actors and world effects alike.
  Choosing a timeline and toggling loop are steps.
- Anything on a locked camera.

### Locks

The lock toggles in the sidebar and the context menu read the entity again and
record through the camera, collider or group it belongs to. The inspector, gizmo,
pivot and placement all respect the same lock:

- a locked collider can't be edited;
- a locked member of a group can move only when the whole group is selected;
- one-off placement still refuses locked group members, even with the whole group
  selected.

### When a step can't be undone

Every write reports whether it worked.

- A first write that is refused records nothing and leaves redo as it was.
- An undo that is refused does not move the history position, and reports why.

What happens to a refused step is decided per step, the same way for global undo,
undo for the selected entity and each part of a removal:

- **kept for another try**: a value write that reports a refusal, a restore that
  redraws, a group restore waiting for its members, and by default creating and
  removing entities;
- **dropped on the second refusal in a row**: a value write that throws, and other
  steps;
- **dropped at once**: when it can never work, such as a borrowed world object that
  can't be restored, or a removal whose parts were all dropped.

These rules apply only when the step actually ran. An active drag, a call made from
inside another call, or a recovery in progress simply blocks undo. That uses up no
attempt and drops nothing.

Undo and redo run what each step recorded. A change to the actor or its animation
does not make them fall back to restoring a whole-pose snapshot. A restore that
takes several frames is waited for before history moves on. Each write still checks
that its target is the right one and is available.

A step that came from a file, such as a pose import or a scene load, checks the file
before redo. If the file is gone it refuses with one notice.

Baking IK is one step. Undoing it puts the bones back and turns the baked chains on
again.

### Entity properties

Each property of a light, overlay, camera, prop or world object is declared once
for its kind and written by the entity's stable ID through one path. The path finds
the current entity, applies that kind's rules for refusing, and records under the
entity and property, so a continuous edit becomes one step.

- A stale ID writes nothing and records nothing.
- Transport properties (pausing and speeding an effect, pausing scenery animation)
  are written and reported but never recorded.
- Undo and redo find the entity again through history's own lookup, so an entity
  that was recreated still gets its edits. Public IDs are never changed to make
  that work.

### History across redraws

When an actor is redrawn, transform history and kept group data find the new body
through the committed scene. A bone keeps its actor, slot, skeleton part, index and
exact name. Only the actor and skeleton may have been replaced. A bone that is
missing or doesn't match never turns into a transform on the actor. Ordinary
selection and editing keep their own stricter rules.

- An actor that is still there but has no skeleton is in the middle of a redraw.
  That is not proof its bones are gone. Undo waits, writing nothing and starting no
  recovery.
- When the body returns, all the step's targets are matched to it together. A
  missing slot, a mismatched bone or a missing owner drops the whole step.
- A redraw cancels a drag in progress. It does not commit half of it.
- Recovering the drag's starting values follows the same rules. While the actor is
  being republished it keeps the starting values and blocks edits. When the exact
  bone returns it restores them. It never writes to a different bone just to clear
  the block.

### Show and Hide

Show and Hide report a result for each target. A target that refuses or is stale
writes nothing and records nothing. The sidebar eye, the context menu and the
selection section all show the same notice. One target is its own step. Several
targets are one entry holding only the ones that worked, so undo and redo touch
only those.

### Undo for the selected entity

Alt+Z undoes and Alt+Y or Alt+Shift+Z redoes for the selected entity, using the
same history. The global Ctrl shortcuts are unchanged. The keys can be changed.

- Bones and gaze points count as their actor. Several bones on one actor share it.
- An empty, stale, grouped or multi-entity selection never falls back to global
  undo.
- A step known to be about something else is skipped. A step shared with the actor,
  or one whose reach is unknown or scene-wide, blocks it. Steps are never split.
- Redo for the selected entity makes that step the newest applied one, and a new
  edit clears redo as usual.
- It waits on recovery and on steps still finishing, like global undo.
- Creating, removing and changing relationships stay global. Otherwise deleting the
  selected entity would leave its redo with nothing to act on. Such steps can still
  be skipped when undoing other entities' edits.
- A removal counts as touching everything its parts touch. If any part's reach is
  unknown, the whole removal blocks.
- Any new step cancels a replay that was waiting, even one for another entity.

### Actor placement

Writing an actor's placement keeps the scale it was given. It is not clamped a
second time. `PosingService` reports whether the actor and its drawn model were
there and the write was sent. Parenting, scene restore, position lock and transform
history use that answer. They do not treat an existing override as proof of a new
write. If the drawn model isn't there, no new override is stored.
