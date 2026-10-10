# Scenes

## Load history

Actor names in scene files are authored display nicknames (or the native name
when no nickname exists), never the temporary anonymous UI mask. Loading applies
the saved name through the session nickname owner after actor binding is ready,
before pose import, so a refused pose or placement cannot discard the name;
resave and load redo preserve it. Native actor names remain unchanged because
external appearance providers use them as identity.
New captures mark names as display names. Unmarked legacy names use the normal
object-index cleanup; authored numeric suffixes in marked files remain intact.

Every committed scene load — complete, or partial with named refusals —
records one undo step. Undo removes that load's created entities, groups and
imported parent links, runs the same gaze and appearance teardown a clear
runs on each removed actor, and restores its captured baselines, including
the default camera's followed actor and the live camera. Redo reads the file
again additively (it never clears the session a second time), retargets the
same step to the new load, and lands only when that load commits; a replay
that rolls back reports why and stays redoable. Repeated undo never uses an
earlier load's emptied cleanup lists. Undo while another scene operation runs
is refused and the step kept. Clearing existing scene content before a load
remains destructive. Loaded groups and their transform baselines are restored
before completion, even with the sidebar closed; a member whose binding does
not arrive in time is left out of its group by name.
Actor imports wait for a bound character skeleton, not merely a ready weapon.
Embedded actor and companion poses stay frozen and suppress their own history;
the scene-load entry is their only undo boundary.
Attached companions restore their own captured model placement after their
bones, not just the owner's placement. Their embedded absolute model transform
receives the same scene rebase as the owner; bone-local poses are not rebased.

## Transform parenting

Parenting is independent of sidebar grouping. Actors, props, scenery/VFX,
lights and IK colliders can follow another entity or an actor bone. The shared
application owner stores a stable target and a position/rotation offset;
attaching preserves world placement, and detaching leaves the child in place.
Scale belongs to the child and is not inherited. Screen overlays and cameras
are not transform-parenting participants. Existing light attachments import
into this same owner; they do not retain a separate following loop.

Numeric and gizmo edits update the child's offset. Undo/Redo restores that
offset against the parent's current frame. Multi-entity edits and restoration
write parents before children. Cycles, including an actor attached to its own
bone or companion, are refused. Locks prevent editing relationships, not
following their parents. Missing parents hold children at their last position;
only lifecycle-history rebinding can redirect a removed entity to its restored
instance. Bone redraw rebinding stays within the exact actor, slot and name.

Scenes store entity keys and bone names, never live handles. Relationships load
after entity/pose readiness, within the existing scene transaction. Companion
references identify the saved owner's attachment. Saving without a parent
keeps the child's current placement as a static object and reports the omission.
Loading only selected categories skips links whose endpoints were not restored,
leaving the admitted child at its saved placement. Screen overlays cannot be
endpoints; scene validation rejects those links before any native creation.
Generated body colliders use this contract; Detach makes an individual part static.
Duplicating an entity retains its parent and local offset as part of the
creation history entry, alongside its copied properties.

## Lifecycle history

Each entity family has a typed lifecycle owner over the same transform
history. A slot is the identity shared by every history entry for one entity:
removal captures the latest state into that slot, and restoration rebinds the
slot to the newly created instance before later entries or actor readiness
callbacks use it. Clearing transform history clears every owner's instance
mapping with it.

Removal captures the entity as last edited, not its original spawn arguments.
Actor restoration creates a fresh body, applies captured appearance/equipment,
collection and Poser body-profile values, then waits for its skeleton and bone
bindings before applying placement, pose, presentation, gaze and IK. Companion
state follows the owner's pose. Lifecycle history restores authored bone-transform
stacks directly, like Brio's pose history, rather than exporting and re-importing
the evaluated animation frame. Untouched bones remain untouched. Animation is not
recovered: no timeline, frame, speed or loop restoration, and no added freeze policy.
The user handles animation after undo/redo. Scene files remain pictures, as described
below. A repeated removal captures the latest edits again.

Camera removal includes tracking, target and lock settings; light removal
includes its bone attachment as well as emission, shadow and texture values.
External targets that no longer exist are not replaced by unrelated entities.
World-light acquisition/release preserves the edited copy separately from the
original world's baseline. Undo reacquires only the same native incarnation;
scenery/VFX reclaim uses the same identity rule, never address presence alone.
Light, camera, prop, overlay and world-object history share lifecycle-slot
property replay: resolve the slot's current instance when replaying an edit.
Property sessions request only the typed history-resolver interface, not the
whole lifecycle controller; the shared value journal owns the replay policy.
Lights, props, collider overlays and world objects retain their transform
targets before removal publishes a scene refresh.
Earlier edits survive acquisition undo/redo and removal undo/redo. Expired public
selection IDs and acquisition receipts never redirect to the restored instance.
Duplicate collections retain their resolved resource paths and meta values;
restoration creates a new owned collection rather than reusing its deleted ID.
A duplicate's temporary collection is deleted by its GUID even when the clone's
slot vanished first, and the delete's result code is checked, so none outlives
its clone and a later spawn at the same address never inherits a stale one.
MCDF history reuses its package reference; it is not a portable appearance export.
Embedded scene packages stay staged until the GPose session ends or the plugin
unloads, so deleting an actor does not invalidate its history's appearance source.

## Created entity names

Created actors, cameras, lights, props, overlays, world objects and groups
share a numbered-name policy. Each entity family looks at its current names:
keep the source stem and advance beyond its highest existing numeric suffix.
`Key 1` and `Key 3` therefore produce `Key 4`, even when copying `Key 1`;
deleting a middle entry does not fill that gap. There is no global counter:
`Camera 1` and `Actor 1` can coexist. Case-only differences share a series.
An unnumbered source counts as the first entry; its copy starts at 2.
Borrowing does not rename the borrowed entity. A spawned copy is a new entity
and uses the shared rule. Actor numbering changes the display nickname, not
the native game name used by Penumbra. Loading and history restoration retain
authored names instead of allocating a new number.

## Borrowed world actors

World-actor acquisition history retains the exact observed identity inside
the Game control owner, even while the held actor is absent from discovery.
Redo revalidates that observation, not a remembered native address. A missing
actor, reused address or changed kind refuses acquisition; undo of an already
gone actor does not release its replacement.
Release captures the actor's lifecycle state and name, then restores captured
presentation baselines while the actor is still
bound. Undo reclaims that same actor and restores the saved scene state without
new history entries. Deferred pose restoration stops if the claim is released.

## Scene files

Saved groups may include world-space IK colliders, including nested groups.
The spawn browser offers saved groups in every category after fixed actions and
before individual library entries; a mixed group does not belong only to Actors.

An `.xivs` scene is versioned JSON with a stable `SceneId`. It contains actors
with embedded poses, objects, lights, cameras, environment, overlays, adopted
world objects, relationships, and optional world toggles. An actor can store
model id, catalog kind, companion attachment and pose, visibility, absolute
transform, gaze, and an appearance payload. A minion, mount or ornament spawned
as an actor respawns through the catalog by its saved kind; older files without
a kind load it as a character. Every loaded character is a clone of the local
player, so an actor other than the player saved without an appearance payload
is noted at save and again at load: it comes back wearing the player's look. Permanent Penumbra collections are local GUID
references, restored and redrawn before companions and poses, as in Brio's
`ActorDTO.PenumbraCollection`/`SceneService`. They do not package mods. Only
a collection chosen for the actor is saved: every plain spawn is assigned the
player's collection, and wearing it is inheriting, so it is not recorded. On
load an actor already wearing the saved collection (an older file that recorded
the player's) is left alone with no redraw; the rest are assigned and redrawn
in parallel, not one barrier after another.
Temporary collection IDs are not saved; those require the modded-appearance
payload. Older files without collection references retain their existing load
behavior. Other appearance remains external.

An `.xivs` is a CONTAINER, not a JSON file. `scene.json` inside it is the
document; each appearance payload is its own stored entry under `appearance/`,
named by its content hash so two actors wearing the same package share one
copy. Payload entries are written and read as streams, so a scene carrying
hundreds of megabytes of appearance still has a small document and never puts
that payload in memory.

Library entries (`.xiva`, `.xivl`, `.xivc`, `.xivp`, `.xivw`, `.xivo`,
`.xivg`, `.xive`) are the same container narrowed to one entity, group or the
environment. A reference to an actor the entry left out is cleared with a named
note: a gaze or camera target is dropped, and an attached light is saved at its
world placement. A camera entry without the session default creates a new
camera on load. `.xivl` and `.xivc` files written as bare JSON by older builds
are still read, as a one-light or one-camera entry; nothing writes that form.

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
data is never guessed. Unsupported versions, oversized input, and a malformed
document — its identity, collection caps, text, camera live/default rules, or
the group and parent graph — fail before Poser changes the game. An entity
whose own data is invalid (a name over 256 characters, a non-finite value, a
character-file payload whose entry is not the one its digest names) is left out
of the load by name and the rest of the scene loads; a bad character file or
gaze drops only that part of its actor. Writes stay strict. The description is
prose and allows 4096 characters.

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
mid-publication is waited for. The bound is per actor: an actor still not
pose-ready when it runs out stays in the session, is named in the result, and
leaves every later phase; the other actors load on. A spawned actor's first
draw is held until the game reports it ready, for as long as the spawn lives,
so a busy frame delays the body rather than losing it. A companion body is
waited for with the same three-part test.

The per-actor steps wait for shared single-flight slots instead of refusing on
a busy one. Each pose import waits, within its bound, for the pose slot (an
import, IK bake or open transform gesture); from its first native step to its
terminal the load holds that slot, so pose imports it did not start (library,
inspector, presets) are refused rather than superseding its own. An import
that does not finish within its bound, or whose load is cancelled, is
cancelled by its operation id and never left armed. Character-file imports
wait for the MCDF slot — which a clear-first load's own teardown can hold for
seconds — and report it busy only at the bound. Housing furniture is waited
for until its model streams in; one still loading at the readiness bound is
kept and named in the result, never released afterwards. Every wait honours
cancellation, and a cancelled load always ends Cancelled.

Each phase checks that the load is still running and belongs to the same
session. Character files come before body-dependent state because import
redraws the actor. Loads add to the current session by default. Clearing the
session is outside rollback, so a clear-first load first checks it can spawn
at all (a local player exists) and refuses without clearing when it cannot; a
rollback after the clear reads "the session was already cleared". Relative
loading moves the whole scene from its saved origin before game work. A
library placement (at the camera or an actor) replaces relative loading for
that load, so content is moved once.

Clearing the session removes everything it holds, actors included: an actor
Poser spawned goes through its ownership ledger, an adopted one through the
native scene table; a companion body leaves with its owner. Before either
delete the actor's gaze is released and its
appearance reverted, while it still exists to release them against; an Entity
gaze target that LEAVES the scene is kept by id and marked stale, so another
actor's intent to look at it is refused by name rather than scrubbed. A
cleanup that fails is named in the outcome and the removal still proceeds.

No destroy path leaves the selection pointing at something that is gone. Each
removed actor deselects its whole lineage — the actor, its bones and its bone
groups — and emptying the session drops the selection entirely, because props,
overlays, lights, cameras and borrowed objects carry no lineage of their own.

What a failure costs is decided in one place, the load policy on
`SceneWorkflow`. Required steps — reading the file, the session staying the
same, cancellation, and creating each actor — stop the load: Poser removes only
what it created, in reverse order. Everything else is optional and becomes a
named refusal beside what did restore: an actor that never became pose-ready,
appearance, companions, gaze, pose, objects, cameras, lights, environment,
FABRIK, sidebar groups and order (a member that did not bind in time is left
out of its group), and parent links (a missing parent bone or a refused link
leaves the entity where it was saved). A refused actor spawn names its cause,
such as a full actor table. A borrowed world object is matched by
model path and map placement in the current territory, never by pointer or
object index. Rollback releases its claim.

The sidebar and Scenes tab show progress, cancellation, results, refusals, and
recovery information. The load probe uses the same file reader in the
background.

Every terminal writes one correlated Scene operation line plus one line per
entity with its kind, scene name, outcome, reason and next step. A refused
entity carries both a reason and a corrective action, and neither is truncated
in the result list. An entity restored with a caveat (a missing gobo, a changed
character file, a model still streaming) is listed with its caveat and logged
at Information. Every outcome kind has its next step; the kinds are an enum
whose label and remedy switches do not build with a kind missing. Completion and failure are also announced once through the
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

World acquisition retains exact-instance rollback until its scene identity is
published. Only then does it append history and issue a claim. Binding timeout,
session cancellation or resolution failure rolls back through the native owner,
without acquisition/removal history entries. A refused cleanup stays pending
for framework-thread retry; it never authorizes mutation of a replacement body.

Borrowing a live BG/VFX object remains supported. Undo first enumerates the
current world graph, then reads the candidate's incarnation and compares it
with the identity captured at release; it never probes a saved address before
the graph confirms that address is live. A missing or mismatched candidate
skips that history entry with a reason, allowing older history to continue.
BG identity uses the observed allocation generation and cannot distinguish a
same-address, same-resource replacement if the native resource pointer is
reused unchanged. Earlier edits remain available across release and restoration;
transform history is discarded only when restoring that source fails permanently.
Group restoration preflights all members, so a
borrowed refusal cannot leave owned members partially restored. Bulk release
records only confirmed removals; claims that refuse release remain live with
their acquisition history. A partial redo keeps successful removals recorded
and retries only members still present. In a mixed group, the owned members
remain released when the group restore is refused, and that group's related
history is discarded; recreate those owned objects manually. Owned-only
groups, Poser-owned world-object spawns, and borrowed world-light restoration
keep their existing restore behavior.

Respawning a world object keeps its old native and stable handle while the
replacement loads hidden. Completion includes model readiness and applicable
settings (placement, visibility, opacity, stain/night state or VFX playback/colour).
The latest authored settings are applied before old-body teardown. Undyeable
models do not wait for a stain buffer; raw spawned scenery does not load animation
data. No readiness or property replay remains owed after successful replacement.
After 15 seconds without readiness, a refusal, release, GPose exit or unload,
the replacement is cancelled. Failed cleanup retains exact-incarnation authority
for retry; a reused address never authorizes destruction of a replacement.
An initial BG model-resource attachment belongs to the same allocation generation.

## Borrowed world lights

Acquiring a world light creates an editable native copy and suppresses the
original, following Ktisis's `LightModule.AddFromOverworld`. Game-authored
updates therefore do not overwrite the user's emission settings. The copy
retains its own projected-texture reference; toggling it off/on preserves
intensity. It remains a Light in the sidebar regardless of ownership.
Release, GPose exit and unload restore the original visibility and destroy
only the copy. Other source properties are untouched. Source destruction
removes the copy without restoring into a replacement at the same address;
the original's observed generation remains the authority for restoration.

## Light controls and outlines

Area-light Skew X/Y tilt the throw around the local X/Y axes; they do not
rotate the emitter. Native fields and saved values retain their existing
degree/radian boundary. The outline uses the same X-pitch/Y-yaw convention
as Ktisis; this corrects presentation without swapping stored values.
Spotlight outlines keep a fixed perceived slant length so the full 0–180°
cone range stays bounded. This does not clamp the authored angle or light range.

## Weather ownership

Picking a weather requests that ID and enables hold, including None (0). The territory/all-weathers
switch filters choices only; it does not validate or change the current ID.
Holding follows Ktisis's pre-environment-update write, not a repeated transition
restart. Release returns control to the game; territory change and logout
release holds, while GPose exit follows Restore on exit. Weather-specific visual
assets remain dependent on what the game can load in the current location.
The native update may reject an off-list choice back to None; the same result
was observed using Brio's one-write/territory-update suppression mechanism.
This is an accepted limitation, not a promise that every listed weather works
in every zone. The all-weathers filter remains available.

## Housing interior brightness

Interior brightness is a transient Environment override, independent of held
lighting sections and furniture Night state. It is available only while the
game exposes a live indoor housing territory. The first edit captures that
territory's current target; explicit release, GPose exit, logout, and plugin
unload restore it while that original binding is still valid. A territory
change invalidates ownership without writing through the newly resolved room.

The override writes only the game's current target, transition speed, and
transition flag. It never changes the player's saved housing lighting level.
Scene and environment-library files intentionally do not persist this value:
loading a picture must not acquire a location-specific housing override.

## Portable appearance

`Modded appearance` makes a save PORTABLE: the scene carries each actor's
appearance package bytes, not a path. Poser embeds the package it already owns
for an actor, and creates one from the actor's live Glamourer, Penumbra and
Customize+ state through the MCDF exporter when it owns none. An actor whose
package cannot be produced is saved with no appearance and named in a note, and
the save reports a partial result — a path, a temporary collection, or any
other live handle is not a portable save.

Restoring an embedded payload streams the container entry into one owned
temporary file retained for the session and imports it through the same MCDF
transaction a hand-driven import uses. The entry must be the one its recorded
SHA-256 names, and the bytes are hashed while they are staged: a payload that
does not match its digest is refused by name, never imported.

A scene load's character-file import and a save's appearance export are
children of that load or save. When the parent's bound expires or it is
cancelled, it cancels the child by its own operation id, never the shared
slot, so a newer operation is never touched. It then waits up to 15 seconds
for the child to stop. Once cancelled, the child's remaining phases refuse
and roll back, so a late completion cannot change the actor. A staged
package or export file is deleted only after its child stops. A child that
outlives that wait has its file deleted when it does stop, and the outcome
and log say so. A child that committed before the matched cancel is a
successful import. While the parent waits, the scene pane reads
"Cancelling…" rather than holding the last step.

Plugin unload does not drain: the framework thread is blocked in disposal,
so the drain's framework hop could never run. Disposal cancels the children
directly first, the parent returns at once, and the MCDF transaction's own
bounded drain joins the child.

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

## Furniture

Furniture is a spawned world object addressed by its canonical housing `.sgb`
path, with names/categories/icons from the indoor and outdoor housing sheets.
It uses the same transform, visibility, duplicate, history, scene, and object-library
controls as scenery. Stain zero means the furnishing's default; a custom tint
takes precedence. Choosing a stain clears that tint in the same history edit.

The backend follows Brio's `SGLService`/`FurnitureObject`: one owned shared-group
layout contains the furnishing's entire child graph. Children are not independent
borrow candidates. Graphics edits wait for layout readiness; a fresh load that
times out after 15 seconds is removed, unless a scene load already named it as
still loading and kept it. Release/GPose exit tears down the owning
layout, never individual children. Raw BG debug controls do not apply. Furniture's
Night toggle applies the scenery day/night byte only to its BG child models,
not to the owning layout or its light nodes. Its visible effect is asset-dependent.

Furniture has its own Couch spawn category and Furniture inspector. It shares
the equipment dye picker (one native furniture stain channel). Embedded lights
have individual on/off controls in the inspector and context menu. Their states
survive duplication, lifecycle history, and scene/library saving, addressed by
child paths within the same furniture asset rather than native pointers.

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

A saved Detached gaze is restored before pose import samples its native basis;
detaching afterward changes the chest/neck baseline beneath the imported deltas
and accumulates drift across save/load. Active gaze targets still restore after
the actors' poses and placement, when their targets are available.

This does not touch the Animation tab, expression hold, or anything else
outside scene save and load.

Poser intentionally keeps absolute stored values and additive default loading,
while Brio and Ktisis use destructive best-effort loads. These are deliberate
compatibility choices, not claims about the other formats.
