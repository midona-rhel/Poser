# Pose files and transfer

## Pose format and import

`.pose` uses the same JSON structure as Brio v3. Each slot has its own data:
Character uses `Bones`; MainHand, OffHand, Prop, and Ornament use their own
collections. A missing slot never falls back to Character or a same-named bone.

Imports reject invalid numbers, rotations, oversized files, excessive JSON,
and bad names or tags. Unknown top-level fields are kept when the file is read
and saved again, but Poser does not use them. Model transforms apply only when
requested; `.cmp` files never change position. When an Anamnesis alias maps to
one game name, Poser always chooses that name.

The import dialog keeps the selected actor and bones it showed the user. Poser
plans the whole import before changing anything. If a write fails, it tries to
restore every changed value. If restore cannot finish, it keeps recovery
information and does not add the import to undo history. A successful import is
one undo step. Copy, stash, and in-memory apply follow the same rule.

## Storage and library

Poser validates a pose or scene before writing it. It writes a temporary file
beside the destination, checks it again, then replaces the old file. The old
file stays backed up until the new bytes are confirmed. If it is unclear which
version reached disk, Poser keeps both paths as recovery information.

Poses, Scenes, and MCDFs default under `Documents/Poser` and appear in their
matching libraries. Auto-saves default under the plugin configuration folder.
They are written and cleaned up, not shown in a library. File browsers open in
the matching folder; light and camera files use Documents.

Library scans run in the background. If a scan fails or is cancelled, the
previous list stays visible. Bad, future, and oversized files stay in the list
with an explanation. Search uses name, author, and tags. Moving a file keeps
its favourite status. File actions ask for a fresh scan; metadata is never
changed for unreadable or future files.

## Autosave

In GPose, actors with edits are saved as snapshots under
`<Auto-save home>/<yyyy-MM-dd>/<HH-mm-ss> <actor>.pose`. The first periodic save
waits one full interval, and no actor means no folder. When GPose closes, Poser
can request one final autosave before cleanup. The health file records the
paths and whether work finished or needs recovery. Taking or queuing a snapshot
does not prove it was saved. Retention keeps at least one save event. Scene
autosave has separate rules in [scenes.md](scenes.md).

## Character files (MCDF)

MCDF v1 stores appearance resources and temporary file payloads. It does not
store pose, animation, selection, camera, or scene data. Poser checks the file
header, size, paths, extensions, duplicate entries, and SHA-1 data before it
changes an actor.

Only one MCDF import runs for an actor at a time. Poser applies temporary
Penumbra, Glamourer, and Customize+ changes after redraw. Failure or
cancellation removes them in reverse order. Incomplete cleanup stays available
through Reset MCDF. Export only reads data, reports skipped resources, and
writes the destination safely. Appearance rules are in
[runtime-appearance.md](runtime-appearance.md).
