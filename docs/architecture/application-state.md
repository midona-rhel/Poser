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

Shared entity verbs read capabilities for the exact current `SelectionId`;
they never promote a stale generation. The application read exposes only
pointer-free visibility and removal capability, while the host resolves the id
again for live visibility and before acting. Sidebar toggles and group baseline
capture use that live value so repeated input cannot wait on snapshot refresh.
Sidebar, context-menu and Selection inspector visibility share the same command
and value journal. Deferred inspector commands capture ids and recheck their
current capabilities when dispatched. Removal keeps
each entity's destroy or release owner and lifecycle history. Adopted actors,
borrowed lights and world objects, default cameras, and locked-group members
retain their separate ownership rules.

Inspector, multi-selection and category removal capture exact IDs at confirmation and
dispatch one batch through the same application command. The Game adapter
rechecks ownership, membership and group locks on the framework thread; world
releases retain claim bookkeeping. Only confirmed removals leave selection.
Per-item outcomes distinguish removed, already absent, refused and failed.
The UI reports refusals through existing notices, not inline state text.
Single-entity inspectors use this same route and never clear unrelated selection.
Successful removal inverses form one history entry, even if another member
refuses. Earlier unfinished value edits are sealed separately. This batch is
synchronous: it never stays open across an await or captures unrelated edits.

Native discovery runs on the framework thread. Notifications during refresh
coalesce into one immediate follow-up; notifications during that pass remain
pending for the next tick, never recursive. Unchanged structure and auxiliary
bindings publish nothing. GPose exit cancels old pending work before requesting
the exit scene; disposal stops queued refresh callbacks altogether.

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

Scene pose reset/flip/mirror/transfer use `IPoseCommands` with exact actor and
bone generations. Application selects the participating skeleton slots and owns
history through the shared pose edit/transfer services. Region resets are
Character-only; whole-pose reset and transfer span all present bone slots.
Only mirror includes authored actor facing; transfer never includes placement.
Game provides the cheap authored-layer read and native transform mechanisms.
Whole-actor Reset All uses `IActorResetControl`: Application owns admission,
reset order and the single history entry; replay retains only the exact actor ID.
Expression/gaze release precedes pose/IK, animation and presentation follow,
and external integrations run last because their restoration may redraw.
Its existing inverse restores pose/IK, not animation or external appearance.
Pose capture/export takes exact actor IDs through `IPoseFileCapture`; Game
resolves on the framework thread and refreshes every slot before reading.
Application owns preview baseline capture, retries, and rebase-then-file
sequencing through `PosePreviewController`. UI supplies options and a frame
clock; Game owns the hidden body, native application and rendered surface.
Late captures from a replaced source cannot overwrite its successor's baseline.
Application also owns import pause, supersession, settle and speed restoration.
Game retains scope planning and in-pass writes/rollback behind the import runtime.
All import callers use `IPoseImportCommands` with exact actor IDs. Game resolves
those IDs before planning on the framework thread; stale targets refuse without
reading skeletons. The native facade contract has been removed from Core.
Pose-file dialogs retain exact actor IDs, including deferred browser callbacks,
not native actors or skeletons. Compatibility inspection returns detached facts;
Game resolves the current rig, while UI owns option drafts and warning wording.
A completed import's delayed speed restore is settled before its successor
captures a baseline, so it cannot resume the newer import's actor.

Gaze inspector, sidebar reads and point gestures use `IGazeControl` with exact
actor generations and immutable readings. Application owns gesture coalescing
and settings history; Game resolves each runtime call and owns native look-at
state. Neither UI target matching nor history retains native addresses/wrappers.
Changing selection during a gaze drag seals that gesture; it cannot transfer
to the newly selected actor. Gaze settings history preserves its existing scope:
mode, parts, locks and points, not entity retargeting. Native transition refusals
remain refusals during history replay, rather than reported successes.
Posed-copy gaze initialization belongs to actor lifecycle, not each spawn UI;
it runs before the first draw and creates no separate gaze history entry.

## World borrowing control

`IWorldService` is the application-facing entry for discovery, highlighting,
acquisition and release. It publishes immutable candidate values and returns
stable scene identities; UI code only owns filters, projection and selection.
The Game implementation owns refresh cadence, framework dispatch and native
revalidation, routing actor and object history through their existing owners.
Actors, lights and BG/VFX keep separate native implementations.
Candidate IDs identify an observed native incarnation. Claim receipts target
one resulting scene incarnation; releasing it ends those receipts, including
when release starts from the entity rather than the receipt. Undo may recreate
a scene incarnation, but an old receipt never redirects to that replacement.
Native lifetime/restoration behavior is defined in [Scenes](../features/scenes.md).

## Results and recovery

Application decides whether an operation succeeded, failed, rolled back, or
needs recovery. Game reports game-side work and failures. Storage handles file
formats and safe writes. The UI shows whether an operation is running, finished,
failed, or needs recovery. Results from an old session or operation are ignored.

## Session lifecycle

The host keeps failed-startup cleanup armed until activation finishes. The
provider owns service disposal; host-owned fonts, command registration and
global UI callbacks are unwound separately in reverse acquisition order.
Cleanup failures are logged without replacing the original startup exception.
Rollback never resolves additional services merely to dispose them.

`SessionLifecycleCoordinator` gives each GPose session a unique token.
Repeated entry keeps the current token. Normal exit clears it before the final
autosave starts; the next entry gets a new token. Repeating exit is safe.
`InvalidateForUnload` closes the session without autosave, events, or game work.

When GPose closes, Poser asks for one final autosave before cleanup. Taking or
queuing that snapshot does not prove it was saved. GPose cleanup is reported
separately, and the background worker receives snapshots only. Autosave rules
are in [files-and-transfer.md](../features/files-and-transfer.md).
After final capture, `GPoseExitingEvent` restores presentation while actor
bindings still exist; only then does `GPoseStateChangedEvent(false)` clear
actors and bindings. Normal exit and plugin unload share this ordering.
Destroyed native bodies are skipped, not written through retained bindings.

Actor nicknames and anonymous-name masks last for one GPose session. The exit
notification clears them after final-save capture, since native slot reuse can
retain a logical lineage for an unrelated actor. Temporary disappearance and
same-session undo/redo do not clear names. Saved scene names remain in the file
and are reapplied by normal loading; native actor names are never changed.
