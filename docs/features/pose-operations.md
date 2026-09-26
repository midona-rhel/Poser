# Pose operations

Duplicate with pose captures the current rendered skeleton, including the
visible head/face and solved IK, without changing the source's raw posing
cache. The copy is a frozen pose, not a copy of live animation, gaze or
Customize+ drivers. Unedited physics sway remains simulated; authored
physics-bone edits are retained. Partial-root scales are restored separately
and must not be reapplied as a second inherited scale during reconciliation.
The live copy keeps slot/partial/name identity instead of passing through a
name-only file. A still-paused posed duplicate restored by history retains
its frozen import basis; this does not recover animation playback.

Each discrete edit captures its targets, computes the change, and writes it to
the game. On failure it tries every captured baseline. If
rollback cannot finish, recovery information and ownership remain available;
the failed edit is not added to success history. A successful edit adds one
history patch. Edits are refused during a live gesture and use the same
restore path as undo and redo.

Mirror and Flip use the YZ plane. Mirror transfers authored layers through the
two frozen animated baselines, exchanges paired bones atomically, and leaves
center bones self-mirrored. Symmetry Mirror uses the same reflection;
Symmetry Link moves the world delta into the partner's local frame.

Reset clears the interactive `BonePose` but keeps named layers. Reset All also
restores expression and gaze, resets every pose region, and disarms IK.
Placement, stash, tools, and disclosure survive. Copy, stash, and apply use
`PortablePose`, are atomic and history-integrated, clear empty destination
overrides, and report zero matches as an explicit refusal.
