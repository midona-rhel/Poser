# Posing

Posing in Poser means selecting an actor or one of its bones and changing it with the gizmo in the game view. [Getting started](getting-started.md) explains how to select a bone, either from the sidebar or from the Pose tab. This page goes into the gizmo, the Pose tab's pages, and the tools for mirroring, resetting and copying a pose.

## The gizmo

When you select something, the gizmo appears on it in the game view. Which handles it shows depends on the tool you've picked on the toolbar below the main window:

- With **rotate**, which Poser starts on, the gizmo is a set of coloured rings. Drag a ring to turn the selection around that axis.
- With **move**, it has arrows. Drag an arrow to slide the selection along it.
- With **scale**, it has handles that make the selection bigger or smaller along one axis.
- The fourth tool shows all of them at once, so you can move, rotate and scale without switching tools.

Press Escape while you're dragging to cancel the drag. Every drag is one step in the undo history, so Ctrl+Z takes back exactly one drag.

The rest of the toolbar changes how the gizmo behaves. **Local** lines the gizmo up with the selected bone, so its rings follow the way the bone is already turned, while **World** lines it up with the game world. **Self** turns a bone around its own joint and **Parent** turns it around the joint above it, which is useful for swinging a whole forearm from the elbow. **Link** and **Mirror** make the matching bone on the other side of the body copy your edits, either as they are or mirrored.

At the bottom of the Pose tab, the **Parenting** checkboxes decide whether the bones below the selected bone follow it. With **Pos** and **Rot** on, rotating a shoulder carries the whole arm along. Turn them off to change one bone without moving the ones attached to it.

You can change the size of the gizmo under **Gizmo size** in Settings › Gizmo.

## Selecting several bones

Ctrl-click bones, in the sidebar, in the game view or on the Pose tab's maps, to select more than one. The gizmo then changes all of them together. On the **Body** and **Face** maps you can also drag a box around several bones to select them at once.

When bones sit on top of each other in the game view, scroll the mouse wheel over them to choose between them. **Wheel over stacked bones** in Settings › Skeleton decides how that works. With **Ktisis**, the wheel moves a highlight from bone to bone and a click picks the highlighted one. With **Brio**, the wheel selects each bone as it reaches it.

## The pages of the Pose tab

The row of pages just under the **Pose** tab gives you different ways to reach the actor's bones.

**Body** and **Face** show a map of the body and of the face, with a circle for each bone. The **Mirror** switch to the right of the pages swaps left and right on the maps. Above the map, the list next to **Edit** chooses which map to show. The built-in maps can't be changed, but you can make your own copy and move its circles around with **Edit**, for example to lay out the bones of a modded skeleton.

Actors that aren't humanoid, such as many minions and mounts, have a single **Bones** page instead of Body and Face.

**Matrix** lists the bones in groups, such as **Head**, **Eye** and **Mouth**, with a small button for each one. Where a bone comes in pairs or chains, the buttons are labelled **L** and **R**, or numbered along the chain.

**3D** shows the actor's skeleton in a separate 3D view inside the window, away from everything else in the game. You can turn, zoom and pan that view, and **Reset View** puts it back.

**Expression** holds facial expressions and face sliders, which are described in [Expressions and gaze](expressions-and-gaze.md).

**Other** has the **IK**, **Gaze** and **Pose** sections. IK and gaze have pages of their own: [IK](ik.md) and [Expressions and gaze](expressions-and-gaze.md).

## Mirroring and resetting a pose

The **Pose** section, on the **Other** page and again lower down in the inspector, has the buttons that work on the pose as a whole.

**Mirror edits** swaps all your edits between the left and right side of the actor, so a pose with the right hand raised becomes the same pose with the left hand raised. Bones in the middle of the body, like the spine, are mirrored onto themselves. **Flip bone** only mirrors the selected bone's own edit, and does nothing on a bone you haven't changed.

The **Reset** buttons clear your edits. **Bone** resets the selected bones, while **Body**, **Face** and **Hair** reset that whole part of the actor. **All** resets everything Poser changed on the actor: the pose, expression, gaze, IK, animation and appearance. The actor stays where it is in the world, and your stashed pose is kept. Resetting is one undo step, so Ctrl+Z brings everything back.

## Copying a pose to another actor

**Stash**, in the same section, keeps a copy of the actor's pose as it looks right now. Select another actor and press **Apply stash** to give it the same pose. Hovering **Apply stash** shows which actor the pose came from and when.

There's only one stash, and stashing again replaces it. **To stash** in the pose export menu and **From stash** in the import menu use the same stash, and it's cleared when Poser closes. To keep a pose for longer, save it to a file or the Library, as described in [Poses and the Library](poses-and-library.md).

To copy a whole actor in its current pose, right-click it in the sidebar and choose **Duplicate › Duplicate with pose**. The copy appears in the same spot, frozen in the same pose, face and IK included.

## Moving several things together

When you select two or more things in the sidebar, the middle of the main window shows the **Selection** tab, and the gizmo moves, rotates and scales all of them around their shared centre. Press **Group…** on the Selection tab to keep them together as a group, which then shows up in the sidebar like any other thing. **Scaling a group** in Settings › Gizmo decides whether scaling a group makes its members bigger or only spreads them further apart.

**Move to camera**, on the Selection tab and in each thing's right-click menu, puts the selection 2.5 yalms in front of the camera. A light goes 1 yalm in front and points where the camera is looking.
