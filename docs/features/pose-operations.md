# Pose operations

The pose actions are in the **Pose** section on the **Other** page of the Pose tab. Each change below is one undo step.

## Mirror and Flip

- **Mirror edits** swaps your edits between the left and right side of the actor. Bones in the middle, such as the spine, are mirrored onto themselves. The actor's facing is mirrored too. Bones you never edited keep following the animation.
- **Flip bone** mirrors only the selected bone's own edit. It is also in the bone's right-click menu. It does nothing on a bone you have not edited.

## Reset

The **Reset** row clears your edits:

- **Bone** resets the selected bones.
- **Body**, **Face** and **Hair** reset only that part of the actor.
- **All** puts the whole actor back, as described below.

The bone's right-click menu also has **Reset › Bone** and **Reset › Bone and descendants**.

### Reset All

**All** resets, in this order: the expression, the gaze, the pose, IK, the animation, the appearance changes Poser made, a changed model, and the appearance applied through other plugins.

- If one part can't be reset, Poser names it and still resets the rest.
- The actor's place in the world and the stashed pose are kept.
- Undo brings all of it back in one step, including the animation the actor was playing.

## Stash

**Stash** remembers the actor's pose as it looks on screen: the body, the face and the weapons. **Apply stash** puts that pose on another actor (or the same one).

- There is one stash. **To stash** in the Export menu fills it too, and **From stash** in the Import menu reads it.
- Apply stash puts on the whole pose as one undo step. It sets the position and rotation of every bone. It does not change scale and does not move the actor.
- From stash puts on the same pose with the import options you chose (see [Importing a pose](files-and-transfer.md#importing-a-pose)).
- Stashing again replaces what was stashed before.
- Hover Apply stash to see which actor the pose came from and when.
- The stash is forgotten when Poser closes.

## Duplicate with pose

Right-click an actor and choose **Duplicate › Duplicate with pose**. The copy appears with the pose the actor has on screen, including the face and any IK.

- The copy is frozen in that pose. It does not play the actor's animation and does not copy where it looks.
- Hair and clothing you have not posed keep moving with physics. The ones you posed keep your pose.
- The copy gets the same Customize+ body shape as the original.
- The original actor is not changed.
- The copy stands exactly where the original stands, with the same rotation. A plain **Duplicate** of an actor, a light or anything else does the same.
