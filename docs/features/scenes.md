# Scenes

A `.poserscene` is versioned JSON with a stable `SceneId`. It contains actors
with embedded poses, objects, lights, cameras, environment, overlays, adopted
world objects, relationships, and optional world toggles. An actor can store
model id, companion attachment and pose, visibility, absolute transform,
animation, gaze, and an MCDF reference. Other appearance remains external.

Placements in the file are absolute. An optional origin records a capture
anchor for relative loading; it is not needed to read the stored numbers.
Territory id and capture-time place name are optional metadata. Missing place
data is never guessed. Unsupported versions, malformed structure, oversized
input, and invalid numbers fail before Poser changes the game.

Capture does not change the scene. It refreshes pose data, takes the document
on the framework thread, then validates and writes it in the background. A
capture waits while pose import uses the shared refresh slot. Scene autosave
uses its own root and retention and skips a name while a scene operation runs.

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

Poser intentionally keeps absolute stored values and additive default loading,
while Brio and Ktisis use destructive best-effort loads. Poser also records
animation and gaze because its runtime can restore them. These are deliberate
compatibility choices, not claims about the other formats.
