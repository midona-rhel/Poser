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
  companion attachment, visibility, WHERE it stands, WHAT it is playing and
  WHERE it is looking. Each of the last three is its own optional member with
  its own absent state, and each is absent from a document that records
  nothing of that kind — so a scene written before they existed reads back
  unchanged.
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
    was playing; a timeline replayed afterwards would animate over it.
  - GAZE records the mode, the participating parts, the anchor and each part's
    own point, the frozen parts, and the followed actor as an in-document key.
    It is applied AFTER the pose: the look-at re-drives its channels every
    frame and its target is another RESTORED actor.
  - Appearance beyond the model id is deliberately NOT scene data. Both
    references capture it (Brio embeds an `AnamnesisCharaFile` plus the
    Glamourer/Penumbra/Customize+ ids; Ktisis embeds a `CharaFile` plus an
    MCDF path), and Poser does not: the state belongs to those plugins, and a
    scene that claimed to restore it would be lying about who owns it.
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
- Capture is read-only, pointer-free, and completes on the framework thread
  BEFORE any file work. It refuses while a pose import owns the caches, for the
  same reason copy-capture does: the apply window pauses and rewinds the
  animation, and a snapshot inside it would persist a half-transitioned pose.
- Load is ONE transaction. The complete document validates before any native
  mutation, so a corrupt, oversized or future file never reaches the session.
  Phases then run in this order, each re-guarded against cancellation and
  session replacement before it mutates anything:

  spawn/admit actors and props → readiness barrier → relationships →
  animation → pose and transforms → presentation and gaze → cameras →
  lights → environment.

  Environment last matches both references. Entities are added; nothing
  pre-existing is destroyed.
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
  never capture a half-restored scene.
- A scene SAVES into the library's scenes root by default — a shipped library
  source under `Documents/Poser/Scenes`, so a freshly saved scene appears in
  the Scenes tab without navigating anywhere. The root is seeded on its OWN
  flag, not the shipped-defaults one, because every existing configuration
  already has that set; it is created before the library can be asked
  anything, since a configured root the scan cannot observe aborts the whole
  pass; and it is resolved through the SOURCE, so a user who repoints or
  renames it keeps their choice. The save dialog still allows choosing
  elsewhere, and the choice sticks for the session.
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
