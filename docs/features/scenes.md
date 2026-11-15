# Scenes

## Load history

### Names in saved scenes

- With Anonymous mode on, a save stores numbered actor labels, also when a nickname
  contains a real character name. With it off, a save stores the name shown.
- Original GPose characters also carry a SHA-256 hash of their normalised name.
  Loading matches that hash to characters already in the scene before it clears or
  spawns anything. Each live character is matched at most once.
- Older files can match by their saved name. Entries that match nothing spawn as
  before.
- The game's own actor names are never changed, because other plugins use them to
  identify the actor.
- New saves mark their names as display names. Older, unmarked names get the usual
  clean-up of the object number. Number suffixes you wrote in a marked file are
  kept.

### Undoing a load

Every scene load that is committed records one undo step. That includes a load that
was only partly applied and named what it refused.

**Undo** removes the entities, groups and parent links that load created. It runs
the same gaze and appearance clean-up on each removed actor that clearing the scene
runs. It restores what the load had captured, including which actor the default
camera followed and the live camera.

**Redo** reads the file again and adds to the scene. It never clears the session a
second time. The step now points at the new load and counts only when that load
commits. If the replay rolls back, it says why and can be redone again.

- Repeated undo never reuses the emptied clean-up lists of an earlier load.
- Undo is refused while another scene operation is running, and the step is kept.
- Clearing the existing scene before a load is still destructive.

### What a load waits for

- Loaded groups and their starting transforms are restored before the load
  finishes, also with the sidebar closed. A member whose binding doesn't arrive in
  time is left out of its group and named.
- An actor import waits for the character's skeleton to be bound, not just for a
  weapon to be ready.
- Poses embedded for actors and companions stay frozen and record no history of
  their own. The scene-load step is their only undo point.
- An attached companion gets its own saved placement back after its bones, not just
  its owner's placement. Its saved model transform is moved with the scene the same
  way the owner's is. Poses stored relative to bones are not moved.

## Transform parenting

Parenting is separate from grouping in the sidebar. Actors, props, scenery, VFX,
lights and IK colliders can follow another entity or a bone on an actor. Screen
overlays and cameras can't take part.

- A parent link stores a stable target and an offset in position and rotation.
- Attaching keeps the child where it is in the world. Detaching leaves it in place.
- Scale belongs to the child and is not inherited.
- Light attachments from older files become ordinary parent links.

### Editing a child

Typing a value or dragging the gizmo changes the child's offset. Undo and redo
restore that offset against where the parent is now. Edits and restores of several
entities write parents before children.

- Loops are refused, including an actor attached to its own bone or companion.
- A lock stops the relationship being edited. It does not stop the child following.
- If the parent goes missing, the child stays at its last position. Only undoing
  the removal can point the child at the restored parent.
- After a redraw a bone link is matched again to the same actor, slot and bone
  name, and nothing else.

### In scene files

A scene stores entity keys and bone names, never live handles.

- Links are restored after the entities and poses are ready, inside the same load.
- A link to a companion names the saved owner's attachment.
- Saving a child whose parent isn't saved keeps the child's current placement as a
  static object and reports the missing link.
- Loading only some categories skips links whose other end wasn't loaded. The child
  that was loaded stays at its saved placement.
- A screen overlay can't be either end of a link. Such a link is rejected before
  anything is created.
- Generated body colliders use these rules. Detach makes one part static.
- Duplicating an entity keeps its parent and adjusts the offset if placement moves
  the copy. That is part of the creation step, with its copied properties.

## Lifecycle history

Creating and removing entities is undone through the same history as transforms.
Each entity has a slot in that history, shared by every step about it. Removing an
entity saves its latest state into the slot. Restoring it points the slot at the
newly created entity before later steps or readiness callbacks use it. Clearing
transform history clears these slots too.

### Removing and restoring an actor

Removal captures the actor as last edited, not as it was spawned. A second removal
captures the latest edits again.

Restoring creates a new body and applies the captured appearance and equipment,
collection and Poser body profile. It then waits for the skeleton and bone bindings
before applying placement, pose, presentation, gaze and IK. A companion's state
follows its owner's pose.

- Your bone edits are restored directly. The evaluated animation frame is not
  exported and imported again, and bones you never touched stay untouched.
- Animation is not brought back: no timeline, frame, speed or loop. You handle
  animation after undo or redo. Scene files do keep animation, as described below.
- An actor whose appearance Glamourer can't read at all (for example
  `ActorNotFound`) is still removed. The step is recorded without its Glamourer
  state, the notice says so, and undo brings the actor back with the appearance it
  spawned with.
- Removal is still refused while another plugin holds the actor in Glamourer, or
  while a character-file operation is busy.

### Other entities

- Removing a camera saves its tracking, target and lock. Removing a light saves its
  bone attachment as well as its emission, shadow and texture values.
- A target that no longer exists is never replaced by an unrelated entity.
- Borrowing and releasing a world light keeps your edited copy separate from the
  world's original. Undo takes back only the same game object. Scenery and VFX
  follow the same rule, and an object merely being at the same address is not
  enough.
- When an edit is replayed for a light, camera, prop, overlay or world object, it
  is applied to whatever entity the slot points at now.
- Lights, props, collider overlays and world objects keep their transform targets
  until after the removal has refreshed the scene.
- Earlier edits survive undo and redo of borrowing and of removal. Expired
  selection IDs and borrow receipts never lead to the restored entity.

### Collections and packages

- A duplicate's collection keeps its resolved file paths and metadata. Restoring it
  creates a new collection Poser owns. It does not reuse the deleted one's ID.
- A duplicate's temporary collection is deleted by its GUID, also when the copy's
  slot went first, and the result of the delete is checked. So no collection
  outlives its copy, and a later spawn at the same address never inherits a stale
  one.
- MCDF history reuses its package. It is not a portable appearance export.
- Packages embedded in a scene stay unpacked until the GPose session ends or the
  plugin unloads, so deleting an actor doesn't remove what its history needs.

## Created entity names

New actors, cameras, lights, props, overlays, world objects and groups are numbered
the same way. Each kind looks at its own current names, keeps the source's name and
goes one past the highest number in use.

- `Key 1` and `Key 3` give `Key 4`, also when copying `Key 1`. Deleting one in the
  middle does not free its number.
- There is no shared counter. `Camera 1` and `Actor 1` can both exist.
- Names that differ only in case share one series.
- A source with no number counts as the first, so its copy is 2.
- Borrowing doesn't rename the borrowed entity. A spawned copy is a new entity and
  is numbered.
- Actor numbering changes the nickname shown, not the game name Penumbra uses.
- Loading and restoring from history keep the names that were written.

## Borrowed world actors

History for borrowing a world actor remembers exactly which actor it saw, also
while that actor is out of range. Redo checks that observation again, not a
remembered address. If the actor is missing, its address was reused or its kind
changed, borrowing is refused. Undoing the borrow of an actor that is already gone
does not release whatever replaced it.

Releasing captures the actor's state and name, then restores what Poser changed
about its presentation while the actor is still bound. Undo takes the same actor
back and restores the saved state without adding history. A pose restore that was
waiting stops if the actor is released.

Borrowed actors are outside the object range that spawned actors use, so the
borrow record handles their visibility. Hiding sets the character's alpha to 0, and
showing restores the alpha it had. Releasing, leaving GPose and unloading give a
hidden body back the alpha it was borrowed with.

## Scene files

### What a scene holds

An `.xivs` scene is a container. Inside it, `scene.json` is the document: versioned
JSON with a stable `SceneId`. It holds actors with their poses, objects, lights,
cameras, the environment, overlays, borrowed world objects, relationships and
optional world toggles.

Each appearance payload is its own entry under `appearance/`, named by a hash of
its contents, so two actors wearing the same package share one copy. Payloads are
read and written as streams. A scene with hundreds of megabytes of appearance still
has a small document and never loads that payload into memory.

An actor can store its model ID, catalog kind, companion attachment and pose,
visibility, placement, gaze and an appearance payload.

- A minion, mount or ornament spawned as an actor comes back through the catalog by
  its saved kind. Older files with no kind load it as a character.
- Every loaded character is a copy of the local player. An actor other than the
  player that was saved without an appearance payload is pointed out when saving
  and again when loading: it will come back wearing the player's look.

Saved groups may include IK colliders and nested groups. The spawn browser offers
saved groups in every category, after the fixed actions and before the single
library entries. A mixed group does not belong to Actors alone.

`.xivs` is the only scene format. Anything else is not a scene and is not listed,
opened or converted. Before a load, the file viewer shows the format, the version
and the size, including the appearance payloads.

### Penumbra collections

A permanent Penumbra collection is saved as a reference to a local collection. It
does not package mods. On load it is assigned and the actor redrawn before
companions and poses.

- Only a collection you chose for the actor is saved. Every plain spawn gets the
  player's collection, and wearing that is inheriting it, so it is not recorded.
- An actor already wearing the saved collection is left alone, with no redraw. The
  others are assigned and redrawn at the same time, not one after another.
- An embedded appearance payload takes precedence over a saved collection.
- If the saved collection doesn't exist on this machine, the load still succeeds
  and the actor keeps the collection it inherited. One notice names the actors
  affected. Poser never picks a replacement by collection name.
- A failure to list collections, to assign one or to redraw, and an invalid
  appearance package, are real failures. They are not treated as a missing
  collection.
- Temporary collections are not saved. Those need the modded-appearance payload.
- Older files with no collection references load as they always did.

Other appearance stays with the other plugins.

### Library entries

Library entries (`.xiva`, `.xivl`, `.xivc`, `.xivp`, `.xivw`, `.xivo`, `.xivg`,
`.xive`) are the same container, holding one entity, one group or the environment.

- A reference to an actor the entry left out is cleared, with a note that names it.
  A gaze or camera target is dropped. An attached light is saved at its place in
  the world.
- A camera entry without the session's default camera creates a new camera on load.
- `.xivl` and `.xivc` files that older builds wrote as plain JSON are still read,
  as one light or one camera. Nothing writes that form any more.

Importing an entry from the file pane or from the Library uses the same loader,
which adds to the scene. If the chosen placement anchor isn't available, a
file-pane import falls back to the saved coordinates and a Library import reports
the problem. Neither can clear the existing scene.

### Format versions

Every scene document and library entry carries a whole-number `FileVersion`.
`SceneFile.CurrentVersion` is what this build writes, and every write stamps it.

Raise the version for any change an older reader would lose or misread: a new
member, a new enum name or a changed meaning, not only breaking changes. An older
build skips members it doesn't know and would destroy them when saving again, and
it fails a whole file on an enum name it doesn't know.

- Readers accept every version from `SceneFile.MinimumVersion` (2, the first
  `.xivs`) to the current one, and fill in defaults for members an older version
  lacks.
- A newer version is refused before anything changes in the game, with a message
  to update Poser.
- Version 3 covers groups, parent links, overlays, world-object extras and anchors
  that shipped under 2. Older readers refuse a version-3 file as too new, so they
  don't drop those members.
- Version 4 adds the optional saved animation and bone edits described below.

Pose files follow Brio's `FileVersion` the same way: only a version above
`PoseFile.CurrentFileVersion` is too new. The `Version` text is the author's own
label for the pose and never decides whether the file is supported.

### Size

The only size limit is the MCDF importer's own limit per package. A package Poser
could not import back is not worth saving. There is no limit on the appearance in a
whole scene. Past a threshold the save warns how large the scene became. It never
refuses, and it never quietly saves without a payload you asked for. A save that
could not build a payload you asked for reports a partial result, not a success.

### What a load checks

Placements in the file are absolute. An optional origin records where the scene was
captured, for loading it relative to somewhere else. It isn't needed to read the
numbers. The territory ID and the place name at capture are optional, and missing
place data is never guessed.

A file fails before Poser changes the game when its version isn't supported, when
it is too large, or when the document is malformed: its identity, its collection
limits, its text, the rules for live and default cameras, or the group and parent
graph.

An entity whose own data is invalid is left out of the load and named, and the rest
loads. That means a name over 256 characters, a number that isn't finite, or a
character-file payload that isn't the one its hash names. A bad character file or a
bad gaze drops only that part of its actor. Writing stays strict. The description
is free text of up to 4096 characters.

### Capture

Capturing a scene does not change it. It refreshes the pose data, takes the
document on the framework thread, then checks and writes it in the background. A
capture waits while a pose import is using the shared refresh. Scene autosave has
its own folder and its own count of kept files, and skips a turn while a scene
operation is running.

A camera target keeps the exact actor it was saved with and whether that link was
locked. It is never moved to an actor that replaced the original.

## Centering the live camera

**Center camera** on an actor frames it once with the live GPose orbit camera. It
uses the actor's mid-body point as drawn and a distance worked out from its height,
within limits. It keeps the view direction and every target, follow, link and
ownership setting. It never creates a camera and never changes a parent.

It is refused before any camera write for a free, locked or pinned camera, and for
an actor that is unavailable, stale, hidden or not drawn. It is in the actor menu
and in the Inspector under Actor → Camera.

## Autosave

Autosave writes one `.xivs` scene each time the scene has changed, under
`<configDir>/SceneAutoSaves/<yyyy-MM-dd>`. It uses the normal scene capture and
safe file write. It includes actors and poses, scene objects, lights, cameras, the
environment and relationships. Appearance is handled as in an ordinary capture.
Autosave does not export mod packages again and again.

- The first save waits one interval.
- An empty or unchanged scene makes no backup.
- A scene save or load in progress postpones the attempt.
- The scene backup count is the one count shown. The old optional-scene toggle is
  ignored: Enabled now controls scene autosave.

### Cameras in saved scenes

This applies to manual scene loads too.

- An orbit camera saves the position it was drawn at. On load that is applied once
  as a shift of the orbit, so zoom and rotation still work. A position you pinned
  stays pinned.
- Loading a saved main camera creates a separate camera. The saved document is
  never applied to the session's main camera. Its saved live flag, target and
  relationships belong to that new camera, and undoing the load removes it.
- Older files with no saved position can restore only the orbit they contain. Where
  the camera was in the world can't be worked out reliably.

### The Auto-saves tab

The Auto-saves tab opens on scene autosaves and loads them without needing an
actor. Legacy poses is a separate, read-only list of old backups. Searches stay
inside the selected part of the list.

- Old status files are ignored. A recorded unfinished write does not mean a file is
  corrupt and offers nothing to recover.
- On leaving GPose, captures that were waiting are dropped. The last finished
  snapshot is kept, and no capture starts while actors are being removed.
- Delete backups on exit clears the scene snapshots after any write that is in
  progress. A write that was queued can't recreate them afterwards.
- Failures stay visible in the tab's footer and in the log. A queued write is not
  proof that a backup exists.

## Loading a scene

### Where the scene is placed

The Library and the scene file dialog default to **As saved** when the scene's
territory ID is set and matches the current map. On another map, or an unknown one,
they use the spawn placement from settings. A placement you pick yourself is
remembered for that file for the session and wins over the default.

The load works out the default against the actual file before it places anything.
History stores the placement that was used, not a rule that could put the scene
somewhere else on redo.

Loading relative to the saved origin moves the whole scene before anything is done
in the game. A Library placement, at the camera or at an actor, replaces that for
the load, so the content is moved once.

### Order of a load

Poser runs one scene load at a time in a GPose session. The steps are:

1. set up actors, objects, overlays and borrowed map objects;
2. wait until they are ready;
3. character files;
4. wait again, because importing a character file redraws the actor;
5. relationships;
6. wait for companion bodies;
7. freeze;
8. pose and transforms, the owner first and then its companion;
9. presentation and gaze;
10. cameras;
11. lights;
12. environment and world toggles.

Each step checks that the load is still running and belongs to the same session.

A load adds to the current session by default. Clearing the session first can't be
rolled back. So a load that clears first checks that it can spawn at all, meaning
there is a local player, and refuses without clearing when it can't. A rollback
after the clear says "the session was already cleared".

### Ready means ready to pose

An actor is ready when it can be posed, not merely when it exists:

- its slot skeletons exist;
- the actor binding names this exact body;
- its bone bindings have been published again for these skeletons.

After a redraw the bones don't resolve until the binding registry has published
them, so the load keeps checking and waits for a skeleton that is still being
published.

- The time limit is per actor. An actor that still isn't ready when it runs out
  stays in the session, is named in the result, and takes no part in later steps.
  The other actors carry on.
- A spawned actor's first draw is held until the game says it is ready, for as long
  as the spawn lives. A busy frame delays the body. It does not lose it.
- A companion body is waited for with the same three checks.

### Waiting instead of refusing

The per-actor steps wait their turn. They do not refuse because something is busy.

- A pose import waits, within its time limit, while another import, an IK bake or a
  drag is under way. From its first write to its end the load holds that turn, so
  pose imports it didn't start (from the library, the inspector or presets) are
  refused and can't override it.
- An import that doesn't finish in time, or whose load was cancelled, is cancelled.
  It is never left waiting to fire.
- A character-file import waits for the MCDF turn, which the load's own clearing can
  hold for seconds. It reports busy only when the time limit runs out.
- Housing furniture is waited for until its model has loaded. A piece still loading
  at the limit is kept and named in the result. It is never released afterwards.
- Every wait stops when the load is cancelled, and a cancelled load always ends as
  Cancelled.

### Clearing the session

Clearing the session removes everything in it, actors included. An actor Poser
spawned is removed through Poser's own record of what it spawned. A borrowed actor
is removed through the game's scene table. A companion body leaves with its owner.

Before an actor is deleted, its gaze is released and its appearance reverted while
it still exists. If another actor was looking at an entity that leaves the scene,
that target is kept by ID and marked stale, and the gaze is refused by name later.
It is not erased. A clean-up that fails is named in the outcome, and the removal
still goes ahead.

Nothing is left selected that no longer exists. Each removed actor deselects itself,
its bones and its bone groups. Emptying the session clears the selection
completely, because props, overlays, lights, cameras and borrowed objects have no
such family to deselect.

### What a failure costs

One place decides this: the load policy on `SceneLoadTransaction`.

**Required steps stop the load.** These are reading the file, the session staying
the same, cancellation and creating each actor. Poser then removes only what it
created, in reverse order. A refused actor spawn names its cause, such as a full
actor table.

**Everything else is optional.** A failure becomes a named refusal listed beside
what did load:

- an actor that never became ready to pose;
- appearance, companions, gaze and pose;
- objects, cameras, lights and the environment;
- sidebar groups and their order (a member that didn't bind in time is left out of
  its group);
- parent links (a missing parent bone or a refused link leaves the entity where it
  was saved).

### Borrowed map objects in a load

A borrowed world object is matched by its model path and its place on the current
map, never by address or object number.

- Loading again reuses that object, also when it was moved or hidden.
- Undo releases an object the load borrowed. For one that was already borrowed, it
  restores the edits from before the load.
- An object missing from the map is reported and skipped.

### Progress and results

The sidebar and the Scenes tab show progress, cancelling, results, refusals and
recovery information. Checking a file before loading uses the same reader, in the
background.

Every finished load writes one Scene operation line to the log and one line per
entity with its kind, name, outcome, reason and next step.

- A refused entity has both a reason and something to do about it. Neither is cut
  short in the result list.
- An entity restored with a caveat (a missing gobo, a changed character file, a
  model still loading) is listed with that caveat.
- Every kind of outcome has a next step. Adding a kind without one does not build.
- Completion and failure are also announced once as a Dalamud notification. The
  detail for each entity stays in the Scene tab.

### Save and load options

The Scene workspace and the file dialogs show the same save and load options. They
can be edited in both and are stored once for the session, so no option is only
reachable from inside a file browser.

- The appearance switch is off by default. It is never saved and never guessed from
  an earlier save.
- The workspace shows how large the next save will be and updates as the options
  change. The appearance figure adds up the real package sizes, because the
  container stores them uncompressed.

The workspace manages the live scene. Browsing saved scenes, recent files and
automatic snapshots is the Library's job.

### Borrowing world effects and objects

**Effects.** A borrowed world VFX keeps the kind it was seen as, whether or not its
file name can be read. Borrowing records whether it was playing, paused or
inactive, and is refused when playback can't be read or is unclear. Releasing
restores that exact state.

Moving an effect places it, tells the game and updates culling. It does not replay
the effect on every tick of a drag, so the effect doesn't keep restarting, and
paused and inactive effects stay stopped. An effect may still behave in its own way
after a move. Poser does not promise that every particle source moves.

Claims on a spawned effect's resource path ignore case, are counted, and last until
the final teardown of that exact effect. A failed creation or teardown keeps or
rolls back the claim. It is never reported as a success.

**Borrowing.** A borrow can be rolled back exactly until its place in the scene has
been published. Only then is a history step added and a claim issued. A binding
that times out, a cancelled session or a failed lookup rolls the borrow back
without adding steps. A clean-up that is refused waits to be tried again on the
framework thread. It never allows changes to a body that replaced the original.

**Undo of a release.** Undo first lists the objects currently in the world, then
reads the candidate and compares it with what was recorded at release. It never
reads a saved address before the list confirms that address is live.

- A candidate that is missing or doesn't match skips that step with a reason, and
  older history carries on.
- For scenery, identity uses the object's allocation generation. It can't tell a
  replacement apart when the same address holds the same resource and the game
  reused the resource pointer unchanged.
- Earlier edits stay available across release and restore. Transform history is
  dropped only when restoring that object fails for good.

**Groups.** Restoring a group checks every member first, so a refused borrowed
object can't leave Poser's own members half restored.

- Releasing several objects records only the removals that were confirmed. Claims
  that refuse stay live with their history.
- A partial redo keeps the removals that worked and retries only members still
  present.
- In a mixed group, Poser's own members stay released when the group restore is
  refused, and that group's history is dropped. Recreate those objects by hand.
- Groups of only Poser's objects, world objects Poser spawned and borrowed world
  lights restore as they did before.

**Respawning.** Respawning a world object keeps its old body and its stable handle
while the replacement loads hidden. It is complete when the model is ready and its
settings are applied: placement, visibility, opacity, stain or night state, or VFX
playback and colour. The latest settings are applied before the old body is
removed.

- A model that can't be dyed doesn't wait for dye data. Raw spawned scenery doesn't
  load animation data.
- Nothing is left to do after a successful replacement.
- The replacement is cancelled after 15 seconds without becoming ready, on a
  refusal, on release, on leaving GPose and on unload.
- A failed clean-up keeps its right to retry on that exact object. A reused address
  never allows a replacement to be destroyed.

## Borrowed world lights

Borrowing a world light makes a copy you can edit and hides the original, as Ktisis
does. So the game's own updates don't overwrite your settings.

- The copy keeps its own projected texture. Turning it off and on keeps its
  intensity.
- It shows as a Light in the sidebar, whoever owns it.
- Releasing, leaving GPose and unloading show the original again and destroy only
  the copy. Nothing else on the original is touched.
- If the original is destroyed, the copy is removed. Nothing is restored into
  whatever now sits at the same address.

## Light controls and outlines

Skew X and Skew Y on an area light tilt where the light is thrown, around the
light's local X and Y axes. They do not rotate the light itself. Stored values keep
their existing units. The outline uses the same pitch and yaw convention as Ktisis,
which corrects what is drawn without swapping the stored values.

A spotlight's outline keeps a fixed apparent slant length, so the whole 0–180° cone
range stays on screen. That does not limit the angle or the range you set.

## Weather ownership

Picking a weather asks for that weather and holds it, including None (0). The
switch between this territory's weathers and all weathers only filters the list. It
does not check or change the current weather.

- Holding writes the weather before the game's environment update. It does not keep
  restarting the transition.
- Releasing gives control back to the game. Changing territory and logging out
  release a hold. Leaving GPose follows Restore on exit.
- What a weather looks like still depends on what the game can load in the current
  place.
- The game may reject a weather that isn't on the territory's list and fall back to
  None. That is an accepted limit. Poser does not promise every listed weather
  works in every zone.

## Housing interior brightness

Interior brightness is a temporary Environment override. It is separate from held
lighting sections and from furniture's Night state. It is available only while you
are in an indoor housing area.

- The first edit captures the area's current brightness. Releasing it, leaving
  GPose, logging out and unloading restore that, as long as you are still in the
  same area.
- Changing area drops the override without writing to the new room.
- It writes only the game's current target, transition speed and transition flag.
  It never changes the lighting level saved for your house.
- Scene and environment files do not save this value on purpose. Loading a picture
  must not take over the lighting of whatever house you are in.

## Portable appearance

`Modded appearance` makes a save portable: the scene carries each actor's
appearance package itself, not a path to it.

- Poser embeds the package it already holds for an actor. When it holds none, it
  makes one from the actor's live Glamourer, Penumbra and Customize+ state with the
  MCDF exporter.
- An actor whose package can't be made is saved with no appearance and named in a
  note, and the save reports a partial result. A path, a temporary collection or
  any other live handle does not make a save portable.

### Restoring a package

Restoring copies the package out of the scene into one temporary file Poser keeps
for the session, and imports it the same way an import by hand works. The entry
must be the one its recorded SHA-256 names, and the bytes are hashed as they are
copied. A package that doesn't match is refused by name and never imported.

### Cancelling

A character-file import during a load, and an appearance export during a save,
belong to that load or save.

- When the load or save times out or is cancelled, it cancels exactly its own
  import or export, never a newer one.
- It then waits up to 15 seconds for it to stop. While it waits, the scene pane
  says "Cancelling…".
- Once cancelled, the rest of the import refuses and rolls back, so finishing late
  can't change the actor.
- The temporary package or export file is deleted only after the import or export
  has stopped. If that takes longer than the wait, the file is deleted when it does
  stop, and the result and the log say so.
- An import that had already committed before the cancel counts as a success.

On plugin unload nothing is waited for through the framework thread, because that
thread is busy unloading. Unload cancels the imports and exports directly, and the
MCDF import's own limited wait finishes them.

## Appearance identity

Every appearance capture records the SHA-256 of the package's bytes, for portable
saves and for saves that only reference a package. That hash is the identity. The
file name and the path are not.

A reference is resolved in this order:

1. An embedded package, when the scene has one.
2. The MCDF library, searched for a package with the same hash. A package that was
   renamed, moved into a subfolder or downloaded again somewhere else still
   matches, and the load says where it was found.
3. The recorded path, when the library has no match. If the file there no longer
   has the same hash, it is applied with a warning that names it.
4. Otherwise a refusal that states both things that were tried.

Nothing is hashed until a load asks for a hash, and the search stops at the first
match. Each hash is remembered with the file's path, size and last-write time, so a
package replaced in place can't answer with its old hash. That memory lasts for the
session only, and nothing is scanned at startup.

## Furniture

Furniture is a spawned world object identified by its housing `.sgb` path. Names,
categories and icons come from the indoor and outdoor housing sheets. It uses the
same transform, visibility, duplicate, history, scene and library controls as
scenery.

- Stain zero means the furnishing's default colour. A custom tint takes precedence
  over a stain. Choosing a stain clears the tint in the same undo step.
- Furniture has its own Couch spawn category and its own inspector. It uses the
  equipment dye picker, with one stain channel.
- Lights built into a furnishing each have an on/off control in the inspector and
  the context menu. Their states survive duplicating, undo history and saving. They
  are identified by their path inside the furnishing, not by an address.

### How a furnishing is built

One layout that Poser owns holds the furnishing and all its parts, as in Brio. The
parts can't be borrowed separately.

- Edits to how it looks wait until the layout is ready. A new furnishing that
  hasn't loaded after 15 seconds is removed, unless a scene load has already named
  it as still loading and kept it.
- Releasing it, or leaving GPose, removes the whole layout, never single parts.
- The raw scenery debug controls don't apply.
- The Night toggle sets the day/night value only on the furnishing's model parts,
  not on the layout or its lights. What you see depends on the asset.

When a world object's model is being replaced, the replacement stays tied to the
operation that started it until it is committed. If history is cleared meanwhile,
the hidden replacement is cancelled on the framework thread before the original is
destroyed. A replay that fails still reports back, so later undo and redo aren't
left waiting.

## A scene keeps its animation unless the save turns it off

By default a scene saves what each actor is doing. The **Animation** option in the
save dialog starts on. It is also in the library save and on the Scene page, and
scene snapshots follow it.

For each actor, and each attached mount, minion or ornament, it saves:

- `Animation`: every layer Poser played, with its timeline, whether it was playing
  or had ended, loop, speed and weight, the frame of a paused layer and how far a
  running one had played. Also the whole-actor speed, the held expression, lips,
  stance, weapon, position lock and whether Advanced mode was on.
- `PoseEdits`: only your own bone edits, stored as offsets on top of the
  animation's frame. The frame underneath is never stored.

### Loading with animation

The actor plays that state again. Paused layers land on their frame, running ones
carry on from where they were, and paused actors stay paused. Files saved before
the progress was recorded restart their running layers.

Your edits then go back on top, in place of importing the baked pose. So the actor
keeps playing and your edits move with the animation. The baked `Pose` is still
written, as the picture to use for an actor whose animation can't be restored.

A timeline ID is looked up in the game and mod list of whoever loads the file. The
same file can therefore play something different on another machine.

### Loading without animation

Turning the option off saves only the picture. Every restored actor is stopped at
speed 0 before its pose is applied, and the pose lands on a held frame. The same
file then gives the same picture on every machine.

### Gaze

A saved Detached gaze is restored before the pose import reads its starting pose.
Detaching afterwards would change the chest and neck under the imported edits and
build up drift over repeated saves and loads. Active gaze targets are still
restored after the actors' poses and placement, once their targets exist.

None of this affects the Animation tab, a held expression or anything else outside
saving and loading scenes.

Poser stores absolute values and adds to the scene by default. Brio and Ktisis
replace the scene and do the best they can. That is a deliberate difference.

## Placement when adding entities

The Placement setting is shared by the Library, the file dialogs and the spawn
menu. It also applies to duplicates and to actors created from MCDFs. Changing it
in Settings or in a load control changes the same saved preference.

- **As saved** keeps positions from the file and keeps a duplicate where its source
  is. New entities use their usual starting position.
- **In front of camera** places the content three metres ahead.
- The relative modes keep a file's saved offset from the camera or an actor. A new
  entity has no saved anchor, so it lands at the current camera or the selected
  actor.
- A copied group keeps its spacing and moves its centre to the chosen position.
- Screen overlays keep their screen coordinates. Colliders are placed in the world.

A full scene saved on the current map still defaults to As saved, and a placement
you chose for that scene wins over the map default. Loading a library entry reports
a missing anchor. It does not quietly use a different placement.
