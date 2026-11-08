# Library resize check (#692)

Candidate: `b94f57ec`.

Removed the pane's 32-pixel resize stepping and separate toolbar width.
The existing row cache still rebuilds only when entries or column geometry change.
Reviewed the affected layout comments and guards. Removed the null-view-model
check: the sole caller passes its initialized, readonly field. Kept size,
selection and empty-result checks needed while drawing and filtering.

Release build and all existing tests passed. No unit tests were added or removed.
The six deployed Poser assemblies matched the candidate build.
Live screenshots at 860, 930, 1040, 1150 and 1320 pixels showed whole right-edge
cards. A 50-frame injected resize changed the window from 1060 to 863 pixels.
No new errors appeared in the checked log tail.

User acceptance remains pending: slowly resize the open Library in both
directions, pause near a column boundary, then continue. Cards should move to
the next row cleanly, without a cut-off right edge or repeated column flicker.
Screenshots establish the sampled layouts, not flicker-free motion between them.
