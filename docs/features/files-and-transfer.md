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

Library scans run in the background and publish one immutable snapshot. A pass
records configured sources (name, path, enabled state, health, and safe
detail), its generation, and a terminal result: initial, success, partial
failure, or failure. Missing, denied, invalid, and traversal-failed sources
are reported independently; a failed source contributes no truncated subtree,
and any entries previously published for that source are omitted from the new
snapshot. Healthy sources publish their fresh entries. Source order and
index-based folder keys remain stable even when roots overlap. A cancelled or
stale pass does not publish.

Publication is limited to 64 configured source records, 32,768 files and 4,096
folders overall. Excess source records have an explicit skipped count; a source
that cannot fit is reported as failed without consuming the remaining capacity.
Later small roots can still publish. Traversal limits also apply to each source.

The library distinguishes no enabled sources, a healthy empty library, and
source failures. Settings > Library owns source management: failed saved paths
have red reasons beside their rows. The bottom Source issues button is disabled
when no issues remain; its in-page detail view lists only enabled failed sources
and skipped-capacity notices, with a resolved state when errors clear.
Refresh and source configuration changes start a new pass; after copying files
externally, press Refresh. Bad, future, and oversized
files stay in the list with an explanation. Search uses name, author, and tags.
Moving a file keeps its favourite status; metadata is never changed for
unreadable or future files.
Before a library export, the selected root is created and checked. A failed
check reports the requested path and stops the write; it never redirects the
file to Documents.

Source edits are drafts until Settings Save. A changed row says Pending Save,
never presenting the saved path's health as validation of its replacement.
Issue details label saved paths and pending edits explicitly. Disable/remove
affect custom drafts only; Cancel discards them. Save refuses a concurrently
changed source list or a root change that the candidate sources cannot resolve;
either refusal leaves saved sources untouched. Explicit checked folder repair is
a filesystem action, not a configuration edit, and is not undone by Cancel.
Scans never create roots.

New sources carry explicit ownership: managed homes, Brio/Anamnesis references,
or custom. Untagged legacy records are recognized only by exact known default
name plus normalized absolute path, or one unambiguous complete four-home layout.
Duplicates, conflicting layouts, invalid paths and unmatched legacy records stay
custom; classification does no filesystem work and never rewrites paths. Settings
preserves those custom records even when their names match built-ins. Poser homes
are read-only derived leaves under one editable root. Root changes affect only
identified managed homes. Third-party references are read-only and never created
by Settings repair or startup; custom sources remain editable.

The Library title-bar Open in Explorer action opens the configured Poser root,
independent of source selection. This explicit action may create a missing root
after a checked request; a failure is reported without redirecting.

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

## Issue reports

"Report an issue" in the burger menu and on the Settings About page
saves one zip in the plugin's own `issues` folder and opens that
folder. It never sends anything. The user attaches the file.

The report holds the last five hundred actions with their values
before and after, the notices the user saw, any exception the UI
caught, the Poser and Dalamud versions, the loaded plugins, the
settings and Poser's own lines from the Dalamud log. The scene is an
option, off by default, and the dialog says what it means: scene data
only, no modified files, no mods.

Names never enter the file. The recorder replaces character names
with "Actor 1", "Actor 2" and so on as it writes, in order of first
sight and stable for the session; the user's profile path and user
name become a tilde. The scene file is scrubbed the same way before it
is packed.

The recorder is a reader of the journal, not a second journal: every
appended entry becomes a record, a folded value step updates its
record, and the recorder can never fail an append.
