# Pose operations

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
