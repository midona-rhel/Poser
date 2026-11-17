# Expression, gaze, and IK

## Expression

The **Expression** page of the Pose tab has two parts.

The **Expression** row at the top plays one of the game's expressions. Choose one,
press **Preview** to hold it on the face, then **Reset** to take it off or **Bake
expression** to turn it into bone edits. The [Animation page](animation.md#expressions)
explains it in full.

Below it are sliders for single face movements: blinks, brows, jaw, lips and so on.

- Each slider drag is one undo step.
- **Combine L/R** puts each left and right pair on one row.
- **Link L/R** makes either side of a pair set both sides to the same value.
- **Unlocked** swaps the sliders for number fields with no limits.
- **Reset** sets every slider back to zero. Your own edits to face bones stay.

The sliders follow the head as it moves. A slider that has no matching bone on
this face is not shown.

## Gaze

Gaze makes the actor look at something. It is in the **Gaze** section on the
**Other** page of the Pose tab.

**Mode** chooses what the actor looks at:

| Mode | The actor looks |
|---|---|
| Off | Wherever the game makes it look. |
| Forward | Straight ahead. |
| Camera | At the camera, and keeps following it. |
| Point | At points you place in the world. |
| Actor | At another actor, chosen under **At**. |

**Eyes**, **Head** and **Body** switch each part on or off. A part that is off is
left to the game.

The lock icon beside a part freezes it where it is looking now. Press it again to
let it follow the target. Choosing Off unlocks every part.

### Point mode

Choosing Point places the point between the actor and the camera and selects it.
Drag it with the gizmo.

- A **Gaze control** row appears under the actor in the sidebar. Eyes, head and
  body each have their own point under it.
- Each part has a row with its point as numbers. The camera icon moves that point
  to the camera.

### Things to know

- **Pose-aware** (beside Mode, off by default) makes Camera and Point gaze allow
  for bones you have posed, so the eyes still land on the target. The game's own
  limits on how far the eyes and head can turn still apply.
- Mode, part and point changes are undo steps. A point drag is one step.
- If the actor it looks at in Actor mode leaves the scene, the gaze stops and the
  Gaze section says so. Choose another actor.

## IK

IK moves a chain of bones so that the end of the chain reaches a target. Select a
bone and open the **IK** section of its inspector. **Live IK** turns IK on for that
bone.

- **Reset** puts the bone's IK settings back to their defaults. It does not turn IK
  off.
- **Bake** turns what IK did into ordinary bone edits and turns IK off for that
  chain. It is one undo step. Undo puts the bones back and turns IK on again.
- Reset All on the actor (see [Pose operations](pose-operations.md#reset-all))
  turns off IK on every chain.

Turning IK on, turning it off or changing its settings does not move the limb by
itself:

- Turning IK on starts from the limb as it stands. Turning it off keeps the
  settings for next time.
- Changing the solver keeps the end of the chain where it is. The joints in
  between follow the new solver.
- With **Constraints** on, the handle stops where the limb is fully stretched. A
  limb can always be pulled straight, also on a body shaped with Customize+.

The actor's own **IK** section (select the actor) shows how many chains are on.
**Enable all** and **Disable all** switch every limb at once, **Bake all** bakes
each chain in turn, and **Show bones** shows the bones that IK moves.

### Solvers

| Solver | Use it for |
|---|---|
| Two Joint | An arm or a leg. Only offered on a hand or foot. |
| CCD | Bending a chain of bones. |
| FABRIK | Bending a chain of bones, in both directions from the selected bone. |
| Rope | A chain that hangs, like a tail or a rope. |

A hand or foot starts on Two Joint. Any other bone starts on FABRIK.

**Swivel** turns the bend around the line from the start of the chain to its end,
without moving either end.

### Targets

**Target** chooses what the end of the chain follows:

- **Actor**: it moves with the actor.
- **World**: it stays at a point in the world.
- **Bone**: it follows a bone you pick, on this actor or another one.
- **Scene entity**: it follows an object, scenery, a light or an effect. **Detach**
  keeps it where it is now.

Changing the target does not move the bone. Only what it follows changes. If the
bone or entity it follows leaves the scene, IK stops until you choose another
target.

**Keep rotation** makes the end of the chain follow the target's rotation too, not
only its position. Two Joint has no Keep rotation, and it is greyed out with the
Actor target.

### Two Joint settings

- **Constraints** keeps the limb within its natural reach. With it off, the hand or
  foot goes exactly to the target.
- **End rotation** off (the default): the hand or foot keeps its rotation while the
  limb moves. On: it turns with the limb.
- **Shoulder**, **Elbow** and **Hand** (or **Hip**, **Knee** and **Foot**) set how
  much each joint helps reach the target.
- **Hinge min** and **Hinge max** limit how far the elbow or knee bends.

### CCD settings

**Constraints** as above. **Depth** is how many parent bones may move, up to 20.
**Iterations** and **Gain** set how hard the solver works each frame.

### FABRIK and Rope settings

- **Parent depth** is how many bones toward the body the chain reaches. **Child
  depth** is how many toward the tip. Zero switches that side off. Together they
  can't be more than 50. The defaults are 3 and 0.
- The far end of each side stays where it is. The selected bone is the handle you
  drag.
- A child chain stops where the bones branch.
- **Iterations** (FABRIK only) as for CCD.

### IK colliders

FABRIK and Rope chains can bend around colliders. Turn on **Colliders** in the
chain's settings. **Bone width** sets how thick each link of the chain is, in
yalms. Only colliders whose **Collision** switch is on take part.

Make a collider from the **Overlays** tab of the spawn browser: **IK collider:
Plane**, **Box**, **Cylinder**, **Cone**, **Capsule** or **Sphere**. Its inspector has:

- **Shape**, **Collision**, **Lock transform**, **Visible** and **Opacity**.
  Hiding a collider does not stop it colliding.
- For a capsule, **Radius** and **Endcap spacing** instead of a scale.

**Create collider** in an actor's right-click menu builds a set of capsules and
spheres that fit the actor's body, up to 48 parts. They are put in one group and
each part follows its bone as the actor moves. It is one undo step.

- The parts keep the size they were made with. If you change the actor's body
  shape later, make them again.
- Each part can be edited or removed on its own. Detach one to make it stay still.
- It is a rough fit of the body, not of the clothes. Hair, ears and skirts are left
  out.
- An actor whose skeleton Poser doesn't recognise can't get colliders.

A collision is not a guarantee. A chain pinned at both ends may still pass through
a collider if there is no other way.
