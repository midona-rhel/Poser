# Application state

`Poser.Application` keeps the current scene, selection, edits, undo history,
session information, and recovery information. It stores ids and values, not
game addresses, native entities, or live UI state.

`ActorId` includes an actor's generation. `SkeletonId` also includes its slot
and slot generation, so replacing a weapon does not replace the character
slot. `BoneId` identifies its skeleton, partial, native index, and canonical
name. Commands require the current generation and refuse stale targets.

`SceneSession` provides one scene snapshot and a revision. `Contains` is the
staleness check. `SelectionSession` owns ordered stable-id selection. Selection
scopes preserve compatibility groups and anchors. Filters, disclosure, hover,
and picker lifetime stay in the UI.

One drag or typed transform edit is one gesture. It captures each baseline
once, then applies total deltas from those values. If a write fails, the
gesture attempts to restore every captured baseline. If restore cannot finish,
the recovery information and ownership stay available for retry; the failed
gesture is not added to success history. Cancel and undo/redo use the same
restore path, and discrete edits cannot interleave with a live gesture.

`PortablePose` does not depend on an actor. It uses bone paths and keys, keeps
duplicate-name variants in order, and uses game indices only to find bones.
Legacy matching and broadcast are explicit compatibility choices. Game access
goes through [posing-runtime.md](posing-runtime.md).

## Results and recovery

Application decides whether an operation succeeded, failed, rolled back, or
needs recovery. Game reports game-side work and failures. Storage handles file
formats and safe writes. The UI shows whether an operation is running, finished,
failed, or needs recovery. Results from an old session or operation are ignored.

## Session lifecycle

`SessionLifecycleCoordinator` gives each GPose session a unique token.
Repeated entry keeps the current token. Normal exit clears it before the final
autosave starts; the next entry gets a new token. Repeating exit is safe.
`InvalidateForUnload` closes the session without autosave, events, or game work.

When GPose closes, Poser asks for one final autosave before cleanup. Taking or
queuing that snapshot does not prove it was saved. GPose cleanup is reported
separately, and the background worker receives snapshots only. Autosave rules
are in [files-and-transfer.md](../features/files-and-transfer.md).
