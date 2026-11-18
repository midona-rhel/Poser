# Selection and transforms

## Selecting

In the sidebar:

- Click a row to select it.
- Ctrl-click adds or removes a row.
- Shift-click selects every row between the last one you clicked and this one.
- Clicking a group's row selects all of its members.

On the bone overlay in the game view, a click selects the bone under the pointer. How you add more bones depends on **Wheel over stacked bones** (Settings › Skeleton):

- **Ktisis** (the default): Ctrl-click or Shift-click adds or removes a bone.
- **Brio**: Shift-click adds or removes a bone. Clicking where several bones overlap also opens a list of them.

On the **Body**, **Face** and **Bones** maps of the Pose tab, click a dot to select it, and Ctrl-click or Shift-click to add or remove one. Drag on an empty part of the map to draw a box: the dots inside it are selected. Hold Ctrl or Shift while you drag to add them to what is already selected.

**Block game mouse targeting** (Settings › General, on by default) stops your clicks from changing the game's target in GPose. Poser's own selection still works. Turn it off to get the game's targeting back.

## The toolbar

The toolbar sets how the gizmo works. The tools are icons; hover one to see what it does.

| Control | Choices |
|---|---|
| Tool | Move, rotate, scale, or the universal tool that does all three. |
| Axes | **Local** follows the selection's own axes. **World** uses the world's. |
| Pivot | **Self** rotates a bone in place. **Parent** rotates it around its parent bone. |
| Symmetry | **Off** edits only what you selected. **Link** applies the same edit to the bone on the other side. **Mirror** applies a mirrored edit to it. |

Things to know:

- The scale tool always uses local axes.
- In World, the universal tool has no scale arrows. It scales evenly from the centre.
- The gizmo stays the same size on screen however far away it is. **Gizmo size** (Settings › Gizmo) changes that size.

## Dragging and typing values

One drag of the gizmo is one undo step. So is one typed value.

- Press Escape during a drag to cancel it. What you dragged goes back to where it was.
- Changing the tool, the axes or the pivot during a drag also cancels it.

Number fields in the inspector:

- Drag sideways to change the value. Hold Ctrl for finer steps, or Shift for bigger ones.
- Double-click to type a value. Enter or clicking away keeps it. Escape cancels.
- Alt-click puts a field back to its default, where it has one.
- The mouse wheel scrolls the page. It never changes a field.

## Hiding handles

An entity or group in the sidebar has a handle button on its row. **Hide handle** is also in its right-click menu. Hiding the handle hides its marker and its gizmo in the game view. It does not hide the entity itself, and you can still edit it in the inspector.

The choice is not saved. It lasts while Poser is running. **Hide handles by default** (Settings › UI) starts every handle hidden.

## Locks

Cameras, colliders and groups can be locked from the sidebar or the right-click menu.

- A locked collider can't be moved. Its values still show in the inspector.
- A locked group moves only as a whole. Select the whole group to move it. Its members can't be moved one at a time.
- **Move to camera** never moves a member of a locked group.

## Move to camera

**Move to camera** brings what you selected to the camera. It is one undo step.

- For a light (in the light's inspector or right-click menu), the light moves one yalm in front of the camera and points where the camera looks. Its scale stays.
- For anything else, and for several things or a group, the centre moves 2.5 yalms in front of the camera. Rotation and scale stay, and several things keep their spacing.

## Groups

Selecting two or more things in the sidebar opens the **Selection** tab. It counts what you selected by kind and has these buttons:

- **Group…** names the selection and makes it a group.
- **Move to camera** (see above).
- On a group: **Save to library** and **Ungroup**.

The gizmo sits at the centre of the selection and moves, rotates and scales everything together.

- A new group's axes face the way the camera was facing when you made it, level with the ground.
- **Scaling a group** (Settings › Gizmo) decides what scaling does. **Sizes and spacing** grows the members and the space between them. **Spacing only** moves the members apart or together and keeps their size.
- Adding members to a group, or removing them, moves nothing.

## The camera

- **Center camera on actor** (in the actor's right-click menu) turns the camera to look at the middle of the actor. It keeps the zoom and the angle. It does not work on a free camera, a locked camera, a camera with a pinned position, or an actor that is hidden or not yet drawn.
- **Recenter** in the camera inspector centres on the actor the camera follows. If it follows no one, it uses the game's target, then the selected bone or actor, then a bone the camera tracks.
- **Update camera orbit with actor position** (Settings › Camera, on by default) moves the point the camera orbits around when you move an actor. It happens when you let go of the mouse. A locked camera is not changed.

Moving the camera with the game's controls, or flying a free camera, is recorded too. Once the camera has stood still for five seconds, the move becomes one **Move camera** undo step. If you make another edit before then, the camera move is recorded first. If you press undo before then, the camera goes back to where it last stood still.

## Undo and redo

Undo is **Ctrl+Z**. Redo is **Ctrl+Y** or **Ctrl+Shift+Z**. The keys can be changed in Settings › Keybinds.

- History keeps 500 steps. **Undo steps** (Settings › General) changes that; 0 turns undo off.
- A slider or field you drag is one step when you let go. Ending on the value you started from records nothing.
- Selecting is never a step.
- Pausing a world effect, changing its speed and pausing scenery animation are not steps. Playing, pausing and scrubbing an actor's animation are.
- Redo of a pose import or a scene load needs the file. If it is gone, redo says so.

If an undo can't be done, Poser says why and the step stays. Most steps that fail twice in a row are then dropped from history, with a notice.

### Undo for the selected entity

**Alt+Z** undoes the latest change to the selected entity only. **Alt+Y** or **Alt+Shift+Z** redoes it. A bone counts as its actor.

It skips changes to other entities. It stops at a change it can't separate from others, such as one shared by several entities or a whole scene load, and says so. Creating and removing entities can only be undone with the normal undo.
