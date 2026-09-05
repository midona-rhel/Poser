# Scenes

An `.xivs` scene is versioned JSON with a stable `SceneId`. It contains actors
with embedded poses, objects, lights, cameras, environment, overlays, adopted
world objects, relationships, and optional world toggles. An actor can store
model id, companion attachment and pose, visibility, absolute transform, gaze,
and an appearance payload. Other appearance remains external.

An `.xivs` is a CONTAINER, not a JSON file. `scene.json` inside it is the
document; each appearance payload is its own stored entry under `appearance/`,
named by its content hash so two actors wearing the same package share one
copy. Payload entries are written and read as streams, so a scene carrying
hundreds of megabytes of appearance still has a small document and never puts
that payload in memory.

The extension and the file version are one identity: `.xivs` is format version
2, and the reader accepts that version alone. `.xivs` is the only scene format
Poser has; anything else is not a scene and is not listed, opened or migrated.
The file viewer states the format, the version and the size before a load, and
the size includes the appearance payloads.

The only size refusal is the MCDF importer's own per-package ceiling: a package
Poser could not import back is one there is no point saving. There is no
whole-document appearance budget. Past a threshold the outcome WARNS how large
the scene became; it never refuses and never silently saves without the payload
the user asked for. A save that could not build a requested payload reports a
partial result, not a success.

Placements in the file are absolute. An optional origin records a capture
anchor for relative loading; it is not needed to read the stored numbers.
Territory id and capture-time place name are optional metadata. Missing place
data is never guessed. Unsupported versions, malformed structure, oversized
input, and invalid numbers fail before Poser changes the game.

Capture does not change the scene. It refreshes pose data, takes the document
on the framework thread, then validates and writes it in the background. A
capture waits while pose import uses the shared refresh slot. Scene autosave
uses its own root and retention and skips a name while a scene operation runs.
Camera targets retain their exact saved actor relationship and whether that
relationship was locked; stale actor generations are never rebound.

## Centering the live camera

Actor "Center camera" is a one-shot framing action on the current live GPose
orbit camera. It uses the actor's drawn mid-body pivot and a height-derived,
clamped distance while preserving view orientation and every target, follow,
link, and ownership field. It never creates a camera or changes parentage.
Free, locked, pinned, unavailable, stale, hidden, and undrawn actors are
refused before any camera write; the action is available from the actor menu
and the Inspector's Actor → Camera section.

## Loading a scene

Poser runs one scene load at a time for the current GPose session. The phases
are:

`set up actors, objects, overlay nodes, and borrowed map objects` →
readiness → character files → readiness → relationships → wait for companion
bodies → freeze → pose and transforms (owner, then companion) →
presentation and gaze → cameras → lights → environment and world toggles.

Readiness means POSE-ready, not merely alive: the slot skeletons exist, the
actor binding names this exact generation, and the bone bindings have been
republished for these skeleton instances. Bone ids are published by the binding
registry's own commit pass, so after a redraw the skeleton service hands out
new bone objects while the registry still holds the previous ones, and every
bone resolves to null until that pass runs. The barrier polls, so a skeleton
mid-publication is waited for; only one that never publishes inside the bound
is refused.

Each phase checks that the load is still running and belongs to the same
session. Character files come before body-dependent state because import
redraws the actor. Loads add to the current session by default. Clearing the
session is outside rollback. Relative loading moves the whole scene from its
saved origin before game work.

Clearing the session removes everything it holds, actors included: an actor
Poser spawned goes through its ownership ledger, an adopted one through the
native scene table. Before either delete the actor's gaze is released and its
appearance reverted, while it still exists to release them against; an Entity
gaze target that LEAVES the scene is kept by id and marked stale, so another
actor's intent to look at it is refused by name rather than scrubbed. A
cleanup that fails is named in the outcome and the removal still proceeds.

No destroy path leaves the selection pointing at something that is gone. Each
removed actor deselects its whole lineage — the actor, its bones and its bone
groups — and emptying the session drops the selection entirely, because props,
overlays, lights, cameras and borrowed objects carry no lineage of their own.

If a load must stop after creating things, Poser removes only the actors and
objects it created, in reverse order. A refused item does not remove successful
items, and Poser names each refusal. A borrowed world object is matched by
model path and map placement in the current territory, never by pointer or
object index. Rollback releases its claim.

The sidebar and Scenes tab show progress, cancellation, results, refusals, and
recovery information. The load probe uses the same file reader in the
background.

Every terminal writes one correlated Scene operation line plus one line per
entity with its kind, scene name, outcome, reason and next step. A refused
entity carries both a reason and a corrective action, and neither is truncated
in the result list. Completion and failure are also announced once through the
normal Dalamud notification channel; the per-entity detail stays in the Scene
tab rather than being repeated in a notification.

The Scene workspace and the file dialogs are two mounts of one answer. The save
options and the load options are editable in both and stored once for the
session, so an option is never reachable only from inside a file browser. The
appearance switch is off by default, is never persisted, and is never inferred
from a previous save. The workspace states what the next save will weigh,
updating as the options change; the appearance figure is a sum of real package
sizes, because the container stores them raw.

The workspace manages the LIVE scene. Browsing saved scenes — recent files and
automatic snapshots — belongs to the Library, which already scans the scene
extension.

World VFX claims retain their observed kind independently of a readable
filename. Adoption captures Playing, Paused, or Inactive playback and refuses
when native playback is unavailable or ambiguous; release restores that exact
state. Transform writes place the effect, notify, and re-cull without
replaying it on every drag tick; this avoids repeated playback restarts, while
paused and inactive effects remain stopped. Native effects may still impose
their own emission behavior after a move; the contract does not promise a
universal particle-origin refresh. Spawned
effect resource-path claims are case-insensitive, reference-counted, and live
until the last exact teardown; failed creation and failed teardown retain or
roll back ownership rather than reporting success.

## Weather ownership

Picking a weather holds that ID, including None (0). The territory/all-weathers
switch filters choices only; it does not validate or change the current ID.
Holding follows Ktisis's pre-environment-update write, not a repeated transition
restart. Release returns control to the game; territory change and logout
release holds, while GPose exit follows Restore on exit. Weather-specific visual
assets remain dependent on what the game can load in the current location.

## Portable appearance

`Modded appearance` makes a save PORTABLE: the scene carries each actor's
appearance package bytes, not a path. Poser embeds the package it already owns
for an actor, and creates one from the actor's live Glamourer, Penumbra and
Customize+ state through the MCDF exporter when it owns none. An actor whose
package cannot be produced is saved with no appearance and named in a note, and
the save reports a partial result — a path, a temporary collection, or any
other live handle is not a portable save.

Restoring an embedded payload streams the container entry into one owned
temporary file and imports it through the same MCDF transaction a hand-driven
import uses. Its checksum is not consulted: the bytes in the container are the
package, so there is nothing to identify them against.

## Appearance identity

Every appearance capture records the SHA-256 of the package's bytes, on both
portable and reference saves. The checksum is the identity; the filename and
the path are not.

A reference is resolved in this order:

1. An embedded portable payload, when the scene has one.
2. The MCDF library, searched for a package whose bytes match the recorded
   checksum. A package that was renamed, filed into a subfolder, or downloaded
   again elsewhere still matches, and the load says where it found it.
3. The recorded path, when the library has no match. A file still at that path
   whose bytes no longer match the checksum is applied with a named warning.
4. Otherwise a refusal that states both things that were tried.

The index hashes lazily — nothing is read until a load asks for a checksum, and
the search stops at the first match — and caches each digest against the path,
byte length and last-write time it was read from, so a package replaced in
place cannot serve its old digest. The cache is in memory for the session,
because the library keeps no derived state on disk. There is no startup pass.

## A scene is a picture, not a performance

Scenes record no animation: no timeline id, no playback position, no speed, no
paused/playing distinction. A timeline id resolves against the LOADING client's
own game and mod list, so the same file would play something different on
another machine, or nothing. Pose data is self-contained and is what a scene
carries instead.

Every restored actor is therefore stopped at speed 0 before its pose is
applied, and the pose lands on a held frame. Expressions come back as part of
the pose, on the frozen face. The same file produces the same picture on every
client, which is the definition of a successful load. Nothing about animation
is attempted, so nothing about animation is refused.

This does not touch the Animation tab, expression hold, or anything else
outside scene save and load.

Poser intentionally keeps absolute stored values and additive default loading,
while Brio and Ktisis use destructive best-effort loads. These are deliberate
compatibility choices, not claims about the other formats.
