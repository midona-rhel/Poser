# Scenes

An `.xivs` scene is versioned JSON with a stable `SceneId`. It contains actors
with embedded poses, objects, lights, cameras, environment, overlays, adopted
world objects, relationships, and optional world toggles. An actor can store
model id, companion attachment and pose, visibility, absolute transform,
animation, gaze, and an appearance payload. Other appearance remains external.

The extension and the file version are one identity: `.xivs` is format version
2. Development-format `.poserscene` files are not read and are not migrated.
The file viewer states the format, the version and the size before a load, and
the size includes any embedded appearance payload.

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
bodies → animation → pose and transforms (owner, then companion) →
presentation and gaze → cameras → lights → environment and world toggles.

Each phase checks that the load is still running and belongs to the same
session. Character files come before body-dependent state because import
redraws the actor. Loads add to the current session by default. Clearing the
session is outside rollback. Relative loading moves the whole scene from its
saved origin before game work.

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

The Scene workspace and the two file dialogs are two mounts of one answer. The
save options and the load options are editable in both and stored once for the
session, so an option is never reachable only from inside a file browser. The
appearance switch is off by default, is never persisted, and is never inferred
from a previous save.

## Portable appearance

`Modded appearance` makes a save PORTABLE: the scene carries each actor's
appearance package bytes, not a path. Poser embeds the package it already owns
for an actor, and creates one from the actor's live Glamourer, Penumbra and
Customize+ state through the MCDF exporter when it owns none. An actor whose
package cannot be produced, or which does not fit the per-actor or
whole-document byte limit, is saved with no appearance and named in a note — a
path, a temporary collection, or any other live handle is not a portable save.

Restoring an embedded payload checks it against its own digest, stages it into
one owned temporary file, and imports it through the same MCDF transaction a
hand-driven import uses.

Poser intentionally keeps absolute stored values and additive default loading,
while Brio and Ktisis use destructive best-effort loads. Poser also records
animation and gaze because its runtime can restore them. These are deliberate
compatibility choices, not claims about the other formats.
