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

Spawn search, library actor creation and entity duplication use the shared
creation interface. Deferred UI work keeps session-scoped receipts; only Game
retains the bodies and resolves them after binding. Appearance-copy ordering,
posed-copy initialization and lifecycle history are not implemented per window.
Creation does not change selection: each caller selects its resolved receipt.

Expression gestures and their inverses live in Application and retain actor IDs;
Game owns catalog resolution and native expression layers. Inspector and body/face
maps read detached actor/skeleton/bone descriptors. Propagation settings, IK
translation limits and bake-chain reads cross ID-only contracts; no native bone
or mutable pose-info instance is retained by these surfaces.

World gizmos and skeleton/light/collider overlays also consume ID-only reads.
Game resolves each bulk bone-position request once against the exact skeleton;
caller-owned masks keep hidden bones out of the hot read path. The UI retains
only display values, projects them through `ICameraProjection`, and owns drawing,
hit-testing and gesture input. Collider reads preserve their immutable geometry
identity for rendering caches; copying mesh arrays every frame is not required.

Prop, scenery, furniture and VFX editors read detached values and send exact IDs
through `ISceneObjectControl`; Game resolves native objects and uses the existing
value journals. Picker targets and deferred callbacks cannot follow a replacement
generation or a newly selected entity. Native respawn and diagnostic offsets
stay behind that boundary, rather than in editor closures.

Camera follow, recenter and bone tracking share `ICameraTargetControl`.
For these actions, the pane, sidebar and picker retain IDs and detached readings;
Game owns native target fallback, stale-reference pruning and exact-generation
checks. Follow remains distinct from recentering, which changes framing only.
Tracking mode values keep their existing serialized numbers.
Camera property rows and the inspector joystick use detached `ICameraControl`
readings and the same value journal. A joystick gesture retains its initial
camera ID; changing selection cannot redirect a held gesture. Native units
and ownership reset baselines remain backend values. Portrait edits capture
both the mode and roll so undo restores the authored framing.
Camera file dialogs retain exact IDs or shared creation receipts through
`ICameraFiles`; Game resolves and captures on the framework thread. File import
applies its document before recording the shared lifecycle step, so redo restores
the imported state. The native document mapping is shared with scene capture
and history, not implemented in presentation.

Overlay properties and the inspector pad use detached readings and exact IDs
through `IOverlayControl`, retaining the existing shared value journal.
Pad gestures and deferred icon picks keep their original target; a changed
selection or replacement generation cannot redirect the edit. Collider property
commands update the current backend value, not an older UI snapshot.

Light properties, gobo picks and bone attachments also use detached readings
and exact IDs. Retained pickers edit their opening target, never the current
selection. Light file dialogs use creation receipts; Game owns native capture,
placement and application, sharing the document mapping with scene/history.
Import applies the file before recording the shared creation history step.

Animation panes use the application's playback and journal-action interfaces;
they own picker/filter state, not native bindings or framework callbacks.
Held-expression preview/retry and bake admission belong to the Game boundary.
The bounded retry retains its exact actor, session and native binding, so hiding
the pane does not stop it and replacing its target cannot redirect it.

Equipment and customization commands own their history in Application behind
ID-only controls. The panel retains picker/display state, not undo baselines;
structural body changes capture fresh customization values before redrawing.
Their inverse includes dependent fields normalized by the appearance provider;
ordinary palette/slider edits still restore only their edited fields.
Game supplies external-provider calls and exact-generation availability through
the existing integration runtime. Failed equipment inverses retain their failure
and remain retryable, including partially restored outfits.

Appearance-changing history uses the same complete non-animation snapshot as
Reset All and lifecycle restoration. Capture failure refuses mutation; the redo
state is captured on first undo, after pending redraw/import and later edits have
settled. It never captures the old body immediately after requesting redraw.
Replay restores external appearance/model before pose-dependent state. The
journal advances only after completion, retains failed entries for retry, and
cancels work when its actor, session or history operation no longer applies.
Inherited collection assignments remain inherited; absent C+ overrides remove
owned temporary profiles rather than leaving the later override active.

Actor visibility/presentation and companion changes also journal in Application.
UI holds exact IDs and detached slot readings; Game resolves native bodies on
every read/write. A companion pick revalidates the original subject and owner,
while its history follows that owner's slot because the operation replaces the
child. Replay never retains the previous native actor wrapper. Refused writes
and inverses use the shared value-journal failure handling.

Character-file dialogs and library actions share `ICharacterFiles`. Application
owns format routing, spawn readiness and appearance history; Documents validates
and converts `.chara`/Glamourer data without game access. Pending imports retain
the creation receipt and validated document, never a UI selection or native body.
The framework advances readiness independently of window drawing; session changes,
unload or a 30-second readiness timeout discard pending work without retargeting.
MCDF execution and recovery remain owned by the existing integration transaction.

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
Its inverse captures authored pose/IK plus supported appearance, model,
presentation, gaze, expression and Customize+ state before mutation; failed
capture refuses the reset. Lifecycle restoration shares those state owners.
Appearance and collection restoration completes before pose-dependent writes,
using the Game redraw barrier. MCDF resources remain under the existing
transaction/session ownership. The single history entry advances only after
completion; failures remain retryable. Animation playback is never recovered.
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
