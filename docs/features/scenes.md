# Scenes

`.poserscene` saves and restores an entire scene — actors, props, lights,
cameras, the environment, and the relationships between them. Per-entity file
formats, the atomic-write discipline they share, and per-actor pose auto-save
are defined once in [files-and-transfer.md](files-and-transfer.md); this file
states only what is durable about the WHOLE-SCENE layer.

- The document EMBEDS the existing per-entity codecs — a full `PoseFile` per
  actor, `LightFile` per light, `CameraFile` per camera — rather than restating
  their fields. A light that round-trips through a scene is bit-for-bit the
  light that round-trips through a `.poserlight`, its own `FileVersion`
  semantics included. `FileVersion` is the scene layer's own; a higher version
  is refused as a typed `Future` outcome, never guessed at.
- Relationships are stated EXPLICITLY as in-document keys — companion
  attachment, a light's owner plus slot/partial/bone name, a camera's target
  actor, an actor's gaze target — and the key is a stable logical identity
  independent of any native binding generation. No native index or pointer is
  persisted, and a gaze target is never the runtime `GameObjectId` it is keyed
  by: every actor in a restored scene is freshly spawned, so a saved object id
  names nothing. An absent relationship is an ABSENT field, never a sentinel
  member: an empty companion slot writes no companion kind at all, matching the
  Domain rule that nothing attached is the absence of a `CompanionAttachment`
  rather than a fourth kind.
- What a scene records PER ACTOR, beyond the embedded pose: the model id, the
  companion attachment AND the companion's own pose, visibility, WHERE it
  stands, WHAT it is playing, WHERE it is looking, and WHICH character file it
  is wearing. Each of those is its own optional member with its own absent
  state, and each is absent from a document that records nothing of that kind —
  so a scene written before they existed reads back unchanged.
  - PLACEMENT is stated by the scene layer in its own right rather than being
    read out of the embedded pose. The pose codec's `ModelAbsoluteValues` marks
    "unrecorded" with `BoneData.Identity` — zero position, identity rotation,
    ZERO scale — which an actor genuinely standing at the world origin is
    indistinguishable from, so the restore could silently place nothing. The
    embedded values remain the FALLBACK for older documents, and a placement
    the transform owner refuses is a named entity refusal: a scene never
    reports a placement it did not make.
  - ANIMATION records only what `AnimationSession` has a route to put back:
    the base timeline, the overall speed (zero being the pause — the only
    pause either reference has), lips, stance and pose, weapon state, the held
    expression, per-slot pins and armed loops, and the position lock. It is
    replayed BEFORE the pose, because the pose was authored on top of whatever
    was playing; a timeline replayed afterwards would animate over it. It also
    records the FRAME a paused timeline stands on, for the Base and UpperBody
    slots the control lookup supports — and only while the actor is paused,
    because a running control's time is whatever the game advanced it to this
    tick and a file cannot have observed it. The frames are written back last,
    after the pause that makes them mean anything, through the ordinary scrub
    gesture: the native write needs a token from a FRESH control enumeration, so
    the token is taken on the restored skeleton and never persisted.
  - The COMPANION is a posable body, not just an attachment: its own pose is
    saved alongside its owner's and imported after it, through the same
    single-flight engine. The load waits, bounded, for the companion's body to
    build — a companion draws several frames after it attaches — and a companion
    that never draws is one named refusal, never a failed scene. Brio saves the
    same document (`ChildActor.PoseFile`); Ktisis has no companion in its scene
    at all.
  - GAZE records the mode, the participating parts, the anchor and each part's
    own point, the frozen parts, and the followed actor as an in-document key.
    It is applied AFTER the pose: the look-at re-drives its channels every
    frame and its target is another RESTORED actor.
  - The CHARACTER FILE an actor is wearing is recorded as a REFERENCE — the
    package's path, its name, and a SHA-256 of its bytes — never as the payload:
    an MCDF is tens of megabytes of another player's mods. On load it is
    re-imported through the ordinary MCDF transaction, so the ownership that
    import registers, and therefore the by-name unlock-and-restore teardown that
    ownership buys, is identical to a hand-driven import. It runs BEFORE
    anything that hangs off the actor's body, because the import redraws the
    actor and takes every skeleton with it, and the skeleton readiness barrier
    runs again afterwards. A package that has moved is a named per-actor
    refusal; a package whose bytes changed since the save is RESTORED with the
    divergence named; a package that could not be hashed at save time records an
    empty hash, which says the reference is followable but unverifiable. Nothing
    here is ever a silent skip. Brio records only a `WasMCDF` boolean and then
    explicitly refuses to restore the appearance; Ktisis records the path and
    re-imports it, warning by name when the file has moved — this follows
    Ktisis and adds the hash Ktisis has no equivalent of.
  - Appearance BEYOND the model id and that character-file reference is
    deliberately NOT scene data. Both references capture it (Brio embeds an
    `AnamnesisCharaFile` plus the Glamourer/Penumbra/Customize+ ids; Ktisis
    embeds a `CharaFile` plus the collection and profile ids), and Poser does
    not: that state belongs to those plugins, and a scene that claimed to
    restore it would be lying about who owns it. The character file is the one
    exception because it is the one appearance fact Poser itself put on the
    actor, and therefore the one it can put back.
- The document records WHERE it was captured — the territory id and the place
  name resolved at capture time. The NAME is persisted beside the id, not
  derived from it, because the codec and the library scan have no game data to
  resolve an id with; a listing must be able to say where a scene was taken
  with the game shut. Both are OPTIONAL: a file written before scenes recorded
  a place carries neither member, loads unchanged, and groups in the library
  under its day alone. No place is ever inferred for such a file. The
  `TerritoryType` → `PlaceName` resolution itself (Brio `CatalogWindow.cs:545`)
  has ONE home, `IPlaceService`, which pose auto-save stamps its own documents
  from — a recorded place means the same thing in both file kinds.
- The library groups scenes by the place and the day the DOCUMENT records; the
  file's modified time is the fallback for a scene that records no capture
  time, never a preference, so a copied or synced file does not file under a
  day it was never captured on.
- `SceneId` is the document's stable identity across re-saves and is the exact
  identity a scene operation's `OperationReceipt` targets: a whole-scene
  operation has no single target actor. Receipts, epochs and session
  generations are the ordinary Application types — there is no scene-specific
  receipt.
- Beyond the entities, a scene records the SESSION-WIDE toggles: the water
  freeze and the physics freeze. They are not environment values — the
  environment is a set of held per-section VALUES, while these are patches whose
  enabled state is the whole of their state — so they are stated in their own
  block. Physics states what the SCENE holds rather than the raw global, because
  the patch can be on for a reason the scene did not ask for. A file that states
  NO toggles is asking for the game's own behaviour, so loading one RELEASES
  whatever the session was holding; the absence is a statement, not a gap. A
  toggle the running client cannot reach is a named degradation. Brio records the
  water freeze (`EnvironmentData.IsWaterFrozen`); neither reference records a
  physics freeze.
- Capture is read-only and pointer-free, and produces the complete document on
  the framework thread BEFORE any file work. It is ARMED rather than called: the
  bone values a scene serializes come out of the same raw transform caches an
  ordinary pose export reads, and those caches are refreshed only for skeletons
  the per-frame rebuild qualified — so a never-posed actor's cache still holds
  the values written when its skeleton was BUILT, and a synchronous capture
  would file a pose the actor never wore. The save therefore registers the same
  no-op refresh batch a pose export registers, over every posable skeleton in
  the scene at once, and captures in the update pass that follows; the refresh
  has its own tick bound, and the save has a bound over that, so a scene is
  never written from caches nothing refreshed and never parks forever waiting
  for one. Brio needs no such arming because it refreshes every
  capability-bearing skeleton's caches every frame; Poser restores that parity
  per save. Capture refuses while a pose import owns the caches, for the same
  reason copy-capture does: the apply window pauses and rewinds the animation,
  and a snapshot inside it would persist a half-transitioned pose. The refresh
  slot is single-flight and shared with pose exports, so a save that cannot have
  it is refused by name.
- Load is ONE transaction. The complete document validates before any native
  mutation, so a corrupt, oversized or future file never reaches the session.
  Phases then run in this order, each re-guarded against cancellation and
  session replacement before it mutates anything:

  spawn/admit actors and props → readiness barrier → character files →
  readiness barrier again → relationships → companion-body barrier →
  animation → pose and transforms (owner, then companion) → presentation and
  gaze → cameras → lights → environment and the session-wide toggles.

  Character files come first because their import redraws the actor and takes
  its skeletons with it. Environment last matches both references. Entities are
  added; nothing pre-existing is destroyed.
- Failure is typed, and the terminal state says exactly what the session is
  left holding. A STRUCTURAL refusal (a failed actor spawn, a readiness
  timeout, cancellation, a replaced session) rolls back everything THIS
  operation created in reverse order and lands `RolledBack` or `Cancelled`. An
  ENTITY-level refusal keeps what restored and lands `Failed` with every
  refusal named in the outcome — a light whose attachment bone is missing
  spawns NOTHING rather than detaching into world space. `RecoveryRequired` is
  not used here: its contract requires transform recovery evidence.
- Whole-scene auto-save rides the same cadence and settings as the pose
  auto-save but writes to its own `SceneAutoSaves/<local day>/` root with its
  own retention count, since one snapshot is one large document rather than a
  file per actor. It deliberately bypasses the scene transaction: an unattended
  snapshot must never occupy the single-flight slot or overwrite the progress a
  user is reading, and it skips by name while a scene operation runs so it can
  never capture a half-restored scene. It arms the same bone refresh a
  user-driven save does, and defers by name when the user's own export holds
  that slot — a snapshot yielding to the user is the right trade against filing
  bone values the game never showed.
- A scene SAVES into the library's SCENES home by default, so a freshly saved
  scene appears in the Scenes tab without navigating anywhere. The home is one
  of Poser's four configurable folders; its shape, seeding and creation order
  are specified once in `files-and-transfer.md` ("Poser's home folders"). The
  save dialog still allows choosing elsewhere, and the choice sticks for the
  session.
- UI: the scene is a workspace MODE, never a property of the current selection,
  and it is reached from TWO places that mean the same thing — the sidebar's
  own SCENE section, which stands above the environment because the environment
  is one of the things a scene contains, and the library's Scenes tab, which
  lists `.poserscene` files beside the poses with the same folders, search and
  verbs. A scene tile's primary LOADS (there is no target to pick), and saving
  the current scene is an action on that tab rather than a menu entry. The
  workspace page still carries save, load, live progress with cancel, the
  terminal outcome with every named refusal and surviving recovery file, recent
  scenes, and the automatic snapshots. The load dialog's side panel runs the
  highlighted file through the same codec the load runs, so corrupt and future
  files are visible before opening; the probe is a background read, so the
  frame never waits on a document that may be tens of megabytes.

Reference divergence: Brio (`SceneService`) and Ktisis (`SceneDataService`) are
both destructive-by-default and best-effort — they clear the session first,
catch per entity, and have no rollback. Poser is additive, validates the whole
document first, and reports typed partial recovery instead. Neither reference
records animation or gaze in a scene at all: Brio's `ActorDTO` declares
`ActorFrozen`, `HasBaseAnimation` and `BaseAnimation` but never writes or reads
them, and Ktisis' `ActorInfo` has no animation member. Poser records both,
because it has the hooks to put them back.

What a scene deliberately does NOT record, and why — each of these is a thing
one reference carries that Poser has no fact to put in it, not a gap left for
later:

- A SCENE ORIGIN (Ktisis `SceneFile.SceneOrigin`) exists because Ktisis saves
  actor positions RELATIVE to the local player's position at scene start and
  re-bases them on an auto-save load. Poser states each actor's ABSOLUTE world
  transform, so there is nothing to re-base and no origin to record.
- OVERLAYS (Ktisis `OverlayInfo` — speech balloons, talk boxes, status icons)
  are a Ktisis compositing feature. Poser has none, so there is no state.
- A FORCE-LOOP timeline is unreadable: the port exposes only a setter, and
  `SupportsForceLoop` is false on this client, so there is no field to read and
  nothing a restore could put back. Neither reference records one either.
- A THUMBNAIL (Brio `SceneFileMetaData.Base64Image`) has no producer in Poser:
  the pose codec's `Base64Image` is READ for library tiles authored by
  Anamnesis and Brio, and nothing in Poser captures an image. TAGS
  (`SceneFileMetaData.Tags`) are user metadata rather than captured scene state,
  and Poser has no surface that authors them.
- A PARENT FOLDER ID (Brio `ActorDTO.ParentFolderId`) names a folder in Brio's
  in-session entity tree. Poser has no entity folder tree; its library folders
  are filesystem directories and belong to the library, not the document.
- Ktisis' `ActorInfo.DefaultRotation` is the game object's own `DefaultRotation`
  field, written at respawn. Poser drives the restored actor's model transform
  through a per-frame override that supersedes it.
