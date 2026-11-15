# Pose operations

## Duplicate with pose

Duplicate with pose copies the skeleton as it is drawn, including the visible head
and face and any solved IK. It does not change the source actor's own pose data.

The copy is a frozen pose. It does not carry over the running animation, the gaze
or what Customize+ is driving.

- The copy freezes itself when it is restored, whatever the ordinary import setting
  says.
- Physics sway you haven't edited keeps simulating. Your edits to physics bones are
  kept.
- Scales on the root bones of skeleton parts are restored separately. They are not
  applied a second time as an inherited scale.
- The live copy identifies bones by slot, skeleton part and name, not through a
  file that only has names. The first restore waits for those slots and their
  bindings, then detaches gaze before the import reads its settled starting pose.
- A paused posed duplicate that undo brings back keeps its frozen starting pose.
  That does not bring animation playback back.

## Reading the pose

Poser reads the game's pose through the read-only path. The game's writable bone
accessors are for writes only. Even one that doesn't pass a change down marks the
pose as changed, so they are never used for display, capture or solver reads.

## One-off edits

Each one-off edit captures its targets, works out the change and writes it to the
game.

- If it fails, it tries to put back every value it captured.
- If that can't finish, the recovery information and ownership stay available, and
  the failed edit is not added to history.
- A successful edit adds one history step.
- Edits are refused during a live drag. They use the same restore path as undo and
  redo.

## Mirror and Flip

Mirror and Flip use the YZ plane.

Mirror moves your edits across through the two sides' frozen animated poses, swaps
paired bones in one step, and mirrors centre bones onto themselves. Symmetry Mirror
uses the same reflection. Symmetry Link moves the world-space change into the
partner bone's own frame.

## Reset, copy and stash

Reset clears the bones you posed (the interactive `BonePose`) but keeps named
layers. Reset All also restores expression and gaze, resets every pose region and
turns IK off. Placement, the stash, tools and which sections are open all survive.

Copy, stash and apply use `PortablePose`. They happen completely or not at all, and
they are in undo history. They clear empty overrides on the destination. When
nothing matched, they say so instead of doing nothing.
