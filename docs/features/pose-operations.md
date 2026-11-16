# Pose operations

The pose actions are in the **Pose** section on the **Other** page of the Pose tab.
Each change below is one undo step.

## Mirror and Flip

- **Mirror edits** swaps your edits between the left and right side of the actor.
  Bones in the middle, such as the spine, are mirrored onto themselves. The
  actor's facing is mirrored too. Bones you never edited keep following the
  animation.
- **Flip bone** mirrors only the selected bone's own edit. It is also in the
  bone's right-click menu. It does nothing on a bone you have not edited.

## Reset

The **Reset** row clears your edits:

- **Bone** resets the selected bones.
- **Body**, **Face** and **Hair** reset only that part of the actor.
- **All** puts the whole actor back, as described below.

The bone's right-click menu also has **Reset › Bone** and **Reset › Bone and
descendants**.

### Reset All

**All** resets, in this order: the expression, the gaze, the pose, IK, the
animation, the appearance changes Poser made, a changed model, and the appearance
applied through other plugins.

- If one part can't be reset, Poser names it and still resets the rest.
- The actor's place in the world and the stashed pose are kept.

## Stash

**Stash** remembers the actor's pose. **Apply stash** puts it on another actor (or
the same one).

- Stashing again replaces what was stashed before.
- Hover Apply stash to see which actor the pose came from and when.
- Bones the other actor doesn't have are skipped and named in the message.
- If no bone matches, Apply stash says so and changes nothing.

## Duplicate with pose

Right-click an actor and choose **Duplicate › Duplicate with pose**. The copy
appears with the pose the actor has on screen, including the face and any IK.

- The copy is frozen in that pose. It does not play the actor's animation and does
  not copy where it looks.
- Hair and clothing you have not posed keep moving with physics. The ones you
  posed keep your pose.
- The copy gets the same Customize+ body shape as the original.
- The original actor is not changed.
