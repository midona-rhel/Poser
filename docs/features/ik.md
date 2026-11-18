# IK

IK lets you pose a whole limb or chain of bones by dragging its end. Select a bone, open the **IK** section in the inspector and turn on **Live IK**. Dragging the bone then bends the bones above it to follow.

Turning IK on or off never moves anything. When you turn it off, the limb stays where IK put it, and you can keep editing the bones by hand. Your IK settings are kept for the next time you turn it on. **Bake** does the same as turning IK off, and **Reset** puts the bone's IK settings back to their defaults without turning IK off.

Selecting the actor itself shows its own **IK** section, where **Enable all**, **Disable all** and **Bake all** work on every limb at once and **Show bones** highlights the bones IK moves. **All** in the Pose section turns IK off on every chain.

## Solvers

- **Two Joint** is for arms and legs, and is only offered on a hand or foot. Hands and feet start on it.
- **CCD** bends a chain of bones towards the target.
- **FABRIK** bends a chain both ways from the bone you drag. Every other bone starts on it.
- **Rope** is for chains that hang, like a tail.

Switching solver keeps the end of the chain where it is. **Swivel** turns the bend around the line from the start of the chain to its end, without moving either end.

## Targets

**Target** sets what the end of the chain follows. **Actor** makes it move with the actor, and **World** keeps it at one spot in the world. **Bone** makes it follow a bone of this actor or another one, and **Scene entity** makes it follow an object, a light or an effect. The end then moves and turns with that bone or thing, as if it were attached to it. **Detach** leaves it where it is.

Changing the target doesn't move the bone. If the bone or thing it follows leaves the scene, IK stops until you pick another target.

**Keep rotation** makes the end of the chain turn with the target as well. Two Joint doesn't have it.

## Settings

For **Two Joint**:

- **Constraints** keeps the limb within its natural reach. With it off, the hand or foot always reaches the target exactly.
- **End rotation** makes the hand or foot turn with the limb. It's off by default, so the hand or foot keeps its rotation.
- **Shoulder**, **Elbow** and **Hand**, or **Hip**, **Knee** and **Foot**, set how much each joint bends to reach the target.
- **Hinge min** and **Hinge max** limit how far the elbow or knee bends.

For **CCD**, **Depth** sets how many bones above the end can move, up to 20. **Iterations** and **Gain** set how hard it works each frame. It also has **Constraints**.

For **FABRIK** and **Rope**, **Parent depth** sets how many bones towards the body the chain reaches, and **Child depth** how many towards the tip. They start at 3 and 0 and can add up to 50. The bone you drag sits between the two, and the far end of each side stays put. FABRIK also has **Iterations**.

## Colliders

FABRIK and Rope chains can bend around colliders. Turn on **Colliders** in the chain's settings and set how thick the chain is with **Bone width**.

You add colliders from the **Overlays** tab of the spawn browser. There are planes, boxes, cylinders, cones, capsules and spheres. A collider only counts while its **Collision** switch is on, even if you hide it.

**Create collider** in an actor's right-click menu makes a set of capsules and spheres that fit the actor's body and follow its bones. It fits the body, not the clothes or hair. If you change the actor's body shape later, make them again.

A chain that's held at both ends can still go through a collider if there's no other way.
