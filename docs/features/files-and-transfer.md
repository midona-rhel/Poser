# Pose files and transfer

## File names

One place decides which characters a file name may contain: `FileNamePolicy` in
Documents.

- Text you type has invalid characters stripped.
- Names Poser generates for the library and for auto-saves have them replaced.
- An explicit rename with invalid characters is refused.

Each workflow still decides its own extension, fallback name, what to do when a
name is taken, overwriting and how long files are kept. Those rules differ between
workflows and are not interchangeable.

## Pose format and import

`.pose` uses the same JSON structure as Brio v3. Each slot has its own data:
Character uses `Bones`, and MainHand, OffHand, Prop and Ornament each have their own
collection. A missing slot never falls back to Character or to a bone with the same
name.

### What import checks

Imports reject numbers that aren't finite, invalid model rotations, oversized
files, too much JSON, and bad names or tags. The quick metadata read and the full
read use the same checks. Modern exports and legacy `.cmp` files are both supported.

- Top-level fields Poser doesn't know are kept when the file is read and saved
  again, but Poser does not use them.
- Model transforms are applied only when you ask for them. `.cmp` files never
  change position.
- When several Anamnesis names map to one game bone, the last entry in the file
  wins. Old captures often contain both Head and RootHead.
- A bone rotation that is finite but nearly zero is kept by the reader and skipped
  when the pose is planned. It is never turned into a made-up rotation. This keeps
  old helper entries from rejecting a pose that is otherwise usable.

### How import applies

A bone position in the file stays an absolute target all the way through planning.
That includes duplicate with pose and reset followed by reapply. The change for
each bone is worked out after its parents have moved, so a position that happens to
match an earlier local offset is still applied.

The import dialog keeps the actor and bones it showed you. Poser plans the whole
import before changing anything.

- If a write fails, it tries to restore every value it changed.
- If the restore can't finish, it keeps the recovery information and does not add
  the import to undo history.
- A successful import is one undo step.
- Copy, stash and apply follow the same rule.

### The pose file browser

By default you select a file to preview it, then press Load or double-click. The
optional Single-click mode opens folders and loads files with one click, through
the same import and options. Turning it on does not import whatever is highlighted.
The browser closes after a file is confirmed, so a second Load can't import twice.

### Previews

A pose preview starts from a snapshot of the destination actor's own pose, then
applies the selected file and options. Enabled IK chains are copied onto the preview
actor's skeleton. Their targets are read in the source's model and held in the
preview's model. Orbiting and panning the preview moves only the preview, not the
live scene. A preview chain never keeps hold of a live entity or bone.

## Anamnesis character files

The character-file library and the actor import picker read `.chara` alongside
`.mcdf`. A `.chara` file is appearance data, not a mod package. It needs Glamourer.
It does not need extraction or a temporary Penumbra collection.

- Importing is one undo step. Redo uses the values that were imported and does not
  read the file again.
- Poser reads human body customisation, the packed feature switches, equipment,
  both dyes and facewear, following Brio's reader.
- Facewear accepts both the old numeric `Glasses` and `{ "GlassesId": ... }`.
- Switches that share a byte keep the game's bit masks in the Glamourer request,
  as the appearance controls do.
- Importing onto a new actor checks and keeps the file first, spawns the actor, and
  applies those same bytes when the body is ready. A file that can't be read does
  not spawn a copy.
- Fields that are missing or null are left alone.

Poser does not import nonhuman model IDs, extended shader colours, transparency or
body-scale overrides from `.chara`. Nonhuman files are refused. Poser does not
write `.chara`.

## Storage and library

### Sorting

File dialogs and the scanned library tabs share one sort order for the session:
Name, Newest or Oldest. Name is the default. Folders and library sections keep
their place. Files with the same time fall back to name. Auto-saves always list
newest first.

### What a save contains

A save that includes a pose stores the bones as they are evaluated, solved IK
included, as a baked snapshot in the file. That covers poses, scenes, saved actors
and groups, attached companions and auto-saves.

Saving never turns live IK off, rewrites your edits or adds a bake to undo history.
New files do not carry IK settings that would solve the saved pose again on load.

A pose or scene capture needs every skeleton it asked for to refresh first. If that
is interrupted or times out, the capture is refused and no file is written.

### Writing safely

Poser checks a pose or scene before writing it. It writes a temporary file beside
the destination, checks that, then replaces the old file. The old file stays as a
backup until the new one is confirmed. If it is unclear which version reached the
disk, Poser keeps both paths as recovery information.

### Where files go

Poses, Scenes, MCDFs and Objects default to folders under `Documents/Poser` and show in
the Library. Auto-saves default to the plugin's configuration folder and
show in the Library's Auto-saves tab. File browsers open in the matching folder.
Light and camera files use Documents.

### Library scans

The library is scanned in the background and published as one snapshot.

- A scan records each configured source (name, path, whether it is enabled, its
  health and a safe description), and how it ended: first scan, success, partly
  failed or failed.
- Missing, denied, invalid and unreadable sources are reported separately. A failed
  source contributes nothing, and entries it published before are dropped. Healthy
  sources publish their new entries.
- Source order and folder keys stay stable even when roots overlap.
- A scan that was cancelled or overtaken does not publish.

World-entry (`.xivw`) files are read during the scan only to tell whether they are
scenery, furniture or VFX. Other file details are read later, on demand. The scan
does not unpack appearance packages or make thumbnails.

A snapshot holds at most 64 sources, 32,768 files and 4,096 folders. Sources beyond
the limit are counted as skipped. A source that doesn't fit is reported as failed
and does not use up the remaining room, so later small roots can still publish. The
same limits apply inside each source.

### Library problems

The library tells apart three states: no enabled sources, a healthy library that is
empty, and sources that failed.

- Settings > Library manages the sources. A saved path that failed shows its reason
  in red beside its row.
- The Source issues button is disabled when there are none. Its detail view lists
  only enabled sources that failed and skipped-capacity notices, and says so when
  they clear.
- Refresh and changing the sources start a new scan. After copying files in from
  outside, press Refresh.
- Bad, too-new and oversized files stay in the list with an explanation.
- Search uses name, author and tags.
- Moving a file keeps its favourite. Details are never changed for files that are
  unreadable or too new.
- Before a library export, the chosen root is created and checked. If the check
  fails, Poser reports the path and stops. It never redirects the file to Documents.

### Editing sources

Edits to sources are drafts until you press Save in Settings.

- A changed row says Pending Save. It never shows the saved path's health as if it
  applied to the replacement.
- Issue details label saved paths and pending edits.
- Disable and Remove affect custom drafts only. Cancel discards the drafts.
- Save is refused if the source list changed meanwhile, or if a root change leaves
  the new sources unresolved. Either refusal leaves the saved sources untouched.
- Repairing a folder is a change on disk, not a settings edit, and Cancel does not
  undo it.
- Scans never create folders.

### Source types

A new source is one of three types: a Poser home, a Brio or Anamnesis reference, or
custom.

- Poser homes are read-only folders under one root you can edit. Changing the root
  affects only those homes.
- Brio and Anamnesis references are read-only. Settings repair and startup never
  create them.
- Custom sources stay editable.

Older records without a type are recognised only by an exact known default name
with its absolute path, or by one complete, unambiguous set of four homes.
Duplicates, conflicting layouts, invalid paths and unmatched older records stay
custom. Working this out touches no files and never rewrites a path. Settings keeps
those custom records even when their names match the built-in ones.

The Library title bar's Open in Explorer opens the Poser root, whichever source is
selected. It may create a missing root after checking, and reports a failure
without redirecting.

## Autosave

Autosave writes whole scenes. How often, how many are kept and what happens on exit
are in [scenes.md](scenes.md#autosave). The Auto-saves tab also reads older
per-actor `.pose` backups, which stay available for recovery. New automatic backups
do not write per-actor files.

## Character files (MCDF)

MCDF v1 stores appearance resources and temporary file payloads. It does not store
pose, animation, selection, camera or scene data. The v1 layout is unchanged, and
Customize+ data stays base64-encoded JSON.

Before changing an actor, Poser checks the file's header, size, paths, extensions,
duplicate entries and payload hashes. It verifies both the older 40-digit SHA-1
hashes and the current 64-digit BLAKE3 ones. New exports use BLAKE3, as current
Lightless and Brio do.

Only one MCDF import runs for an actor at a time. Poser applies the temporary
Penumbra, Glamourer and Customize+ changes after the redraw. A failure or a cancel
removes them in reverse order. If the cleanup can't finish, Reset MCDF can still
finish it.

Export only reads data, reports resources it skipped, and writes the destination
safely. The appearance rules are in [runtime-appearance.md](runtime-appearance.md).

## Issue reports

"Report an issue" in the burger menu and on the Settings About page saves one zip
in the plugin's `issues` folder and opens that folder. It never sends anything. You
attach the file yourself.

The report holds:

- the last five hundred actions, with their values before and after;
- the notices you saw and any exception the UI caught;
- the Poser and Dalamud versions and the loaded plugins;
- the settings;
- Poser's own lines from the Dalamud log.

The scene is optional and off by default. The dialog says what it means: scene data
only, no modified files and no mods. What gets attached is the redacted JSON from a
native `.xivs` scene, with actor data and entity relationships intact. It is not a
Stagehand export.

### What is removed

- Known character names become "Actor 1", "Actor 2" and so on as the report is
  written, in the order they first appear. The numbers stay the same for the
  session.
- Before packing, both JSON files go through the same redaction, covering settings,
  dictionary keys, actions, log lines and exception text.
- Absolute drive and network paths become tokens that keep only the file
  extension.
- Known private path segments, host and share names and your user name are removed
  from diagnostic text.
- Relative game asset paths stay, because they are useful for diagnosis.

Reports stay on your machine. Review one before sharing it.

The recorder reads the undo journal. It is not a second journal. Each committed
entry becomes one record with its final before and after values, and the recorder
can never make an edit fail.
