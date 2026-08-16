# Expression, gaze, and IK

## Expression

Expression catalogs are race-specific action-unit data. If a catalog is
missing, the feature is unavailable; Poser does not substitute another race.
Expression changes move with the head. Poser replaces its expression layer when
the expression changes and removes it when cleared. Manual face layers are left
alone. Missing parts are hidden rather than applied to another bone.

## Gaze

Gaze has Off, Forward, Camera, Point, and Actor modes. Eyes, Head, and Body can
be controlled separately, and each can lock its current target. Point mode has
one shared anchor plus per-part points, numeric editing, and camera snap. It
uses the world gizmo, not the bone gizmo. Gaze writes never enter transform
history.

Actor mode needs a live scene target. Poser finds that actor in the GPose range
immediately before writing. Finding a matching game object id alone does not
prove it is the right target. Writes outside indices 201–439 are refused.

Releasing a part stops Poser writes and lets the game control that part again.
Mode, target, points, and locks remain after an empty mask or Off;
`ResetGaze` clears them. Leaving Actor mode also clears the game's target id.

A missing target stays recorded and is marked stale. Poser stops enforcing it,
does not resume on id reuse, and clears it only when a live target is chosen.
Missing gaze signatures or hooks produce an unavailable state before native or
event side effects.

## IK

IK calls the game's Havok solvers during pose application. Only translation
deltas start a solve. The solve itself is not stored, so undo and export remain
ordinary pose deltas. Configuration belongs to one skeleton instance.

A bone is eligible when it has a non-hidden parent. Two Joint uses its
slot-local chain; other eligible endpoints use CCD. Chain settings cannot
change during a gesture.

Relative targets follow animation. Fixed targets keep the captured target and
authored translation, so changing mode does not jump. IK bake disables the
chain, waits for the pose to settle, and writes affected bones as one
raw-baseline history entry. Disabling keeps tuning and clears only fixed
capture. Reset Defaults keeps Enabled, Reset Bone keeps IK, and Reset All
disables and clears every chain.
