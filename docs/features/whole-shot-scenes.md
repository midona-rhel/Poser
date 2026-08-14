# Whole-shot scenes

`.poserscene` saves and restores an entire shot — actors, props, lights,
cameras, the environment, and the relationships between them. Per-entity file
formats, the atomic-write discipline they share, and per-actor pose auto-save
are defined once in [files-and-transfer.md](files-and-transfer.md); this file
states only what is durable about the WHOLE-SHOT layer.

- The document EMBEDS the existing per-entity codecs — a full `PoseFile` per
  actor, `LightFile` per light, `CameraFile` per camera — rather than restating
  their fields. A light that round-trips through a scene is bit-for-bit the
  light that round-trips through a `.poserlight`, its own `FileVersion`
  semantics included. `FileVersion` is the scene layer's own; a higher version
  is refused as a typed `Future` outcome, never guessed at.
- Relationships are stated EXPLICITLY as in-document keys — companion
  attachment, a light's owner plus slot/partial/bone name, a camera's target
  actor — and the key is a stable logical identity independent of any native
  binding generation. No native index or pointer is persisted. An absent
  relationship is an ABSENT field, never a sentinel member: an empty companion
  slot writes no companion kind at all, matching the Domain rule that nothing
  attached is the absence of a `CompanionAttachment` rather than a fourth kind.
- `SceneId` is the document's stable identity across re-saves and is the exact
  identity a scene operation's `OperationReceipt` targets: a whole-shot
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
  pose and transforms → presentation → cameras → lights → environment.

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
- Whole-shot auto-save rides the same cadence and settings as the pose
  auto-save but writes to its own `SceneAutoSaves/<local day>/` root with its
  own retention count, since one snapshot is one large document rather than a
  file per actor. It deliberately bypasses the scene transaction: an unattended
  snapshot must never occupy the single-flight slot or overwrite the progress a
  user is reading, and it skips by name while a scene operation runs so it can
  never capture a half-restored shot.
- UI: the shot is a workspace MODE beside the library (burger menu → "Save or
  load a shot"), never a property of the current selection. The page carries
  save, load, live progress with cancel, the terminal outcome with every named
  refusal and surviving recovery file, recent shots, and the automatic
  snapshots. The load dialog's side panel runs the highlighted file through the
  same codec the load runs, so corrupt and future files are visible before
  opening.

Reference divergence: Brio (`SceneService`) and Ktisis (`SceneDataService`) are
both destructive-by-default and best-effort — they clear the session first,
catch per entity, and have no rollback. Poser is additive, validates the whole
document first, and reports typed partial recovery instead.
