# Brio/Ktisis parity checklist

> **Recreated 2026-08-03.** The previous `docs/brio/` tracking docs (`parity-checklist.md`,
> `ktisis-audit.md`, `anamnesis-audit.md`, `ui-coverage.md`) are gone — the directory was empty
> and git has never tracked those filenames on any branch. This file is rebuilt from a fresh
> three-way source audit; it does not carry over any earlier Done/Not-done rows.
>
> Runtime/source basis: Poser code `HEAD` `e6c2c77`; later docs-only candidate
> commits do not change this runtime truth. Reference basis: Ktisis clone @
> `a5ae200d` (0.3.9.2 with the 0.4-style layout) and Brio clone @ `73bb59d`.
> Inherited documentation snapshots informing this checklist were
> `docs/validation/poser-feature-gap-audit-2026-08-12.md`,
> `docs/validation/poser-code-health-audit-2026-08-12.md`,
> `docs/validation/code-health-remediation-plan-2026-08-12.md`, and
> `docs/architecture/backend-maintainability-audit.md`; mechanisms were
> verified against source/reference call sites, not doc claims.
>
> **Standing exclusions (user, 2026-08-03):** animation-timeline features (timeline UI,
> per-slot scrub/blend editors) and native appearance/equipment/customize editing (Poser
> delegates appearance to Glamourer — "Open in Glamourer" is the design). Neither area is
> listed as a gap below.

## Player-actor gaps (2026-08-03 audit)

Exposure legend used below — **UI**: reachable in the real windows; **backend-only**: code
exists, nothing user-facing calls it; **command-only**: chat command only (counts as a gap
per standing rule); **absent**: no code. Ordered by workflow importance. Each task is sized
for a single focused session.

### Source-verified / acceptance-pending pass 2026-08-12 (against code `HEAD` `e6c2c77`; only the user calls live behavior Accepted)

In this table, **Source-verified** means the implementation or product decision
is resolved by source inspection and/or an explicit user decision. It does not
mean live-game acceptance; that remains pending on the applicable rows.

| Gap | Source status |
|---|---|
| 1 Redraw pose carryover | **Source-verified; acceptance pending** |
| 2 Rest poses | **PARTIAL** — A/T done (import surfaces, per user rule 2026-08-08); reference pose backend-complete, deliberately UI-hidden |
| 3 Pose library | **Source-verified; acceptance pending** (exceeds spec) |
| 4 Auto-save | **Source-verified; acceptance pending** |
| 5 Freeze-on-import | **Source-verified; acceptance pending** |
| 6 Target sync | **Source-verified; acceptance pending** |
| 7 Copy/paste pose UI | **Source-verified; acceptance pending** — stash/apply is the retained UI; clipboard covers cross-session transfer |
| 8 Gaze fixed-position | **Source-verified; acceptance pending** (ships as "Point" mode, exceeds spec) |
| 9 IK bake | **Source-verified; acceptance pending** |
| 10 Overlay filter wiring | not started |
| 11 Bone visibility presets | not started |
| 12 Overworld actor | not started |
| 13A Companion attach UI | **Decision-resolved/source-verified; acceptance pending** — owner-slot attach/current-state display intentionally not exposed |
| 13B Actor-to-bone attach | not started |
| 14 Scene save/load | not started |
| 15 IPC provider | not started |
| 16 Keybind expansion | not started |
| 17 Import options | **PARTIAL, accepted as-is** — (a) done, (b) filter-only, (c) not started after the selected-scope precondition was removed |
| 18 Transform lock | not started |
| 19 Linked-bones toggle | not started |
| 20 Ray-snap translate | not started |
| Polish table | nothing started |

### 1. Pose is lost when the actor redraws

**Reference:** Ktisis preserves the pose across every redraw/skeleton rebuild: on draw-disable
it stores a full `PoseContainer` keyed by object index (`Editor/Posing/PosingManager.cs:113-121`)
and restores it per partial inside the `SetSkeleton` hook (`Editor/Posing/PosingModule.cs:137-166`).
Brio keeps pose data on the entity capability (survives the draw object) and re-applies the
model-transform override after redraws (`Game/Posing/ModelTransformService.cs:88-93`).

**Poser:** absent — deliberately inverted. A replaced slot skeleton gets a *fresh* pose store and
the old one is purged (`Poser.Game/LegacyRuntime/BonePosingService.cs:613-619`); gestures,
history and animation entries for the old generation are released
(`Poser.Game/Scene/CleanSceneLifecycle.cs:178-195`). This bites hardest inside Poser's own
design: the Appearance pane's Penumbra-collection / Glamourer / MCDF actions request redraws
(`Poser.Application/Integration/ActorIntegrationSession.cs:91,116,399`), so using Poser's own
appearance integrations wipes the pose you just authored.

**Verified 2026-08-11: DONE.** Carryover store `Poser.Game/LegacyRuntime/PoseCarryover.cs`
keyed `(LogicalId, Slot)` with 30 s expiry; capture runs before both purge paths
(`BonePosingService.cs:597-598,1007-1008`), restore in `OnSkeletonChanged`
(`BonePosingService.cs:429-482`) with no history entry; rotation everywhere, root position
only, scale never. Config `PreservePoseAcrossRedraws` default-on, Settings → BEHAVIOR
"Keep pose through redraws" (`SettingsView.cs:350-354`). Deviation: restore hangs off
`SkeletonChangedEvent` inside `BonePosingService`, not the lifecycle retry pump —
functionally equivalent. Known hole: capture bails if the actor loses its binding mid-redraw.

**Task:** Add a redraw pose-carryover: before `PurgeSkeletonState` runs for a replaced
`(actor, slot)`, capture the authored `SkeletonPoseInfo` (and the actor's world-transform
override) into a store keyed by stable `LogicalId + slot`. When the new-generation skeleton
settles (the existing retry pump in `CleanSceneLifecycle` already detects "skeleton ready"),
re-apply it as a single restore that does not enter undo history, then drop the carryover
entry. Cover the Ktisis semantics: rotation everywhere, root position only. Gate with a config
default-on. Acceptance: pose an actor, assign a Penumbra collection from the Appearance pane,
pose survives the redraw.

### 2. No rest poses — A-pose / T-pose / reference pose

**Reference:** Brio ships embedded `Data.BrioAPose.pose` / `Data.BrioTPose.pose` and applies
them body-scope rotation-only from the import popup ("Import A-Pose"/"Import T-Pose",
`UI/Controls/Stateless/FileUIHelpers.cs:187-199`). Ktisis has "Set to reference pose" —
`hkaPose::SetToReferencePose()` on every partial, recorded as one memento
(`Editor/Posing/PosingManager.cs:192-206`, entry in the pose context menu).

**Poser:** absent. The Reset Bone/Body/Face/Hair ops return to the *animation* pose, not a
neutral rest pose; there is no A/T-pose anywhere.

**Verified 2026-08-11: PARTIAL.** A-pose/T-pose are done end-to-end: embedded
`PosingCore/Data/RestPoses/BrioAPose.pose`/`BrioTPose.pose` (`RestPoses.cs:29-30`),
`CleanPoseFacade.ApplyRestPose` (one undoable edit, rotation-only body,
reset-before-import for A→T→A idempotence), "Presets" row with A-pose/T-pose buttons in the
import surfaces (`PoseFileInspectorSection.cs:992-999`) reachable from actor context menu,
titlebar burger, and FILES Import — per user rule 2026-08-08 rest presets live with import,
not the POSE rail. Reference pose: backend-complete (`CleanPoseFacade.ApplyReferencePose`,
`Skeleton.CaptureReferencePose`) but **deliberately UI-hidden** until the capture path is
proven in game (`PoseInspectorPane.cs:1991-1995`) — the one remaining item.

**User call 2026-08-11: accepted as-is** ("rest poses is fine") — reference pose stays
UI-hidden; do not reopen without a new user decision.

**Task:** Embed A-pose and T-pose files (Poser already reads Brio-v3 `.pose`, so Brio's
embedded files work as-is) and add "A-Pose", "T-Pose" buttons to the POSE rail section plus
actor context-menu entries. Apply through the existing import pipeline with Body scope +
rotation-only so it composes with the current options and lands as one undoable edit. Add
"Reference pose" alongside, implemented as a skeleton-wide reset through the same edit
machinery (equivalent of Ktisis' `SetToReferencePose`, single history entry).

### 3. No pose library / browser

**Reference:** Brio's `LibraryWindow` (`UI/Windows/LibraryWindow.cs`, `Library/*`):
file-system sources (defaults: `Documents/Brio/Poses`, `Documents/Anamnesis/Poses`, plus
user-managed folders), type filters, text search, tag filter with suggestions, favorites,
breadcrumbs, icon-size slider, per-entry import options, "Apply To {actor}", Ctrl-hold →
"Spawn As New Actor" (spawns a clone, waits `IsReadyToDraw`, applies the pose), and a
config switch making every "Import" button open the library instead of the OS picker.

**Poser:** absent — two raw file dialogs only. `PoseFile.Base64Image`/`Tags` are serialized
but never surfaced (`PosingCore/Files/PoseFile.cs:23-24`).

**Verified 2026-08-11: DONE (exceeds spec).** Full workspace mode: `PoseLibraryService` +
`PoseLibraryPane`/`PoseLibraryView`, entered from the sidebar LIBRARY header, titlebar
burger, FILES "Library" button, and the import menu's "From library". Seeded Brio/Anamnesis
source folders with Settings management (`SettingsView.cs:552-623`), thumbnail grid
(`PoseThumbnailCache`), text + tag filters, favorites (star, context menu, virtual folder),
apply-to-actor picker, spawn-as-new-actor (`PoseLibraryPane.cs:1634-1690`), shared
import-options rail. Beyond spec: live CharaView pose preview, `UseLibraryWhenImporting`
redirect, "Export to library", MCDF and Auto-saves tabs.

**Task:** Build a pose-library surface in the shell (new pane or modal reachable from the
FILES section): configurable source folders in Settings (seed Brio's and Anamnesis' default
paths), a scrollable grid/list of `.pose`/`.cmp` entries showing name, timestamp, and the
embedded `Base64Image` thumbnail when present, a text filter, per-entry favorite star
persisted in config, double-click/Apply using the existing import-options state, and a
"Spawn as new actor" action that chains the existing `ActorSpawnService` spawn +
auto-select + import. Tag surfacing can ride the same entry model since `Tags` already
deserializes.

### 4. No auto-save

**Reference:** Ktisis: timed `PoseAutoSave` writing one `.pose` per character into dated
folders with retention pruning, plus save-on-disconnect and save-on-posing-disable
(`Editor/Posing/AutoSave/PoseAutoSave.cs:45-148`, hooks in `PosingManager.cs:107-137`).
Brio: `AutoSaveService` timer during GPose writing a scene file and, with
`AutoSaveIndividualPoses`, per-actor `.pose` files, `MaxAutoSaves` retention, optional
clean-on-exit, and a "View Auto-Saves" browser (`Game/Core/AutoSaveService.cs`,
`FileUIHelpers.cs:33-69`).

**Verified 2026-08-11: DONE.** `AutoSaveService` (`PosingCore/Files/AutoSaveService.cs`),
eagerly resolved at startup (`Poser.cs:79-82`), timer armed only in GPose, GPose-exit
snapshot, disk-based retention counting save events, clean-on-exit, Settings rows
(General → AUTO-SAVE incl. "Open in Explorer"), burger "Auto-saves" recovery entry plus a
richer library Auto-saves tab. Layout is `<configDir>/AutoSaves/<yyyy-MM-dd>/<HH-mm-ss>
<actor>.pose` — per-day folders, not folder-per-save (user call 2026-08-08, documented at
`AutoSaveService.cs:33-39`; normative: `docs/features/files-and-transfer.md` § Auto-save).
Flag: clean-on-exit *replaces* the exit snapshot rather than snapshot-then-clean.

**Task:** Add an auto-save service: while in GPose, on a configurable interval, export every
actor with authored edits via the existing `PoseFileService.Export` into
`<configDir>/AutoSaves/<timestamp>/<actor>.pose`; keep the newest N folders and prune the
rest; also fire once on GPose exit. Settings rows: enable, interval, retained count,
clean-on-exit. Recovery path: a "Open auto-saves" button in the FILES section that opens the
existing import browser rooted at the auto-save directory — no new browser needed.

### 5. Pose import ignores the running animation (no freeze-on-import)

**Reference:** Brio wraps every import in `StopSpeedAndResetTimeline`: speed forced to 0,
animation local times zeroed, pose applied, speed restored *unless* "Freeze Actor on Import"
(popup checkbox) or `Posing.FreezeActorOnPoseImport` (config) is set
(`Capabilities/Actor/ActionTimelineCapability.cs:110-181`, `FileUIHelpers.cs:78-199`).
Ktisis is frozen by definition while posing is enabled.

**Poser:** absent — `PosingCore/Files/PoseFileService.cs` has zero animation interaction, so
importing onto an actor whose animation is playing applies edits against a moving baseline
and the result is immediately fought by playback.

**Verified 2026-08-11: DONE.** Pause bracket in `CleanPoseFacade` (`:366-444`): capture
prior speed → `Pause` → settle +4 ticks → `RewindPausedControls` (Brio's
`StopSpeedAndResetTimeline` sequence) → restore unless frozen, with failure/throw
restoration; freeze decision is `options.FreezeOnImport || Config.FreezeActorOnPoseImport`.
"Freeze" checkbox in the import options row (`PoseFileInspectorSection.cs:1036-1046`);
every import source (file, clipboard, stash, reapply, library, spawn-as-new, rest presets)
funnels through the bracket. Flag: the checkbox writes the config default directly — one
persistent control, no per-import-only freeze (intentional per `:79`).

**Task:** Bracket `ImportPose` with the existing per-actor pause machinery
(`AnimationSession` speed override): pause before applying, restore afterwards unless a new
"Freeze actor" checkbox in the FILES import options is set (plus a config default mirroring
Brio's). Reuse the exact pause path the sidebar play/pause button already uses so state
stays consistent with the Animation tab. Acceptance: import onto an actor mid-emote lands
the file pose exactly; with the checkbox set the actor stays paused.

### 6. Game target does not drive Poser selection

**Reference:** Brio's `TargetService` syncs per frame in both directions behind config:
`BrioTargetChangesWithGPose` (default **on** — GPose-targeting an actor selects it in Brio)
and `GPoseTargetChangesWithBrio` (`Game/Core/TargetService.cs:60-78`, checkboxes in
Settings→Posing). Ktisis follows the GPose target in legacy mode and optionally blocks
click-targeting.

**Poser:** backend-only/absent — Poser can *set* the GPose target from the sidebar, but
`IActorManager.GetGPoseTarget()` has zero callers (`Poser.Game/LegacyRuntime/ActorManager.cs:179`);
targeting an actor in game changes nothing in Poser.

**Verified 2026-08-11: DONE.** `Poser.Game/Scene/TargetSyncService.cs` (commit `c382836`):
per-frame edge-detected sync, GPose-target → primary selection via `SelectionSession.Promote`
(default on, Brio parity) and reverse direction (default off); Settings → BEHAVIOR rows
"Follow game target" / "Game target follows selection". Minor: target→selection isn't
explicitly gated on `IsGPosing` (relies on `GPoseTarget` being null outside GPose).

**Task:** Add a per-frame target sync: when the GPose target changes and resolves to a scene
actor, promote it to the primary selection (`SelectionSession`), behind a Settings toggle
defaulting on to match Brio; add the reverse toggle (selection sets GPose target) defaulting
off since the sidebar already has an explicit action. The backend read exists — this is
wiring `GetGPoseTarget()` into the session update plus two settings rows.

### 7. Copy/paste pose has no UI

**Reference:** Ktisis' only cross-actor transfer is its single stash slot (Poser already has
stash parity). Brio has clipboard copy/paste for the model transform. Poser's own backend is
strictly stronger than both — a whole-pose `PortablePose` capture/apply that works across
actors — but it is unreachable.

**Current Poser:** retained stash/apply is reachable from the actor context menu and
the clipboard is the cross-session/cross-tool transfer path. `CleanPoseFacade.Copy`/`.Paste`
remain harness-facing (`CleanPoseFacade.cs:294,297`); this is not an absent transfer
capability, but it is not a separate Copy/Paste rail affordance.

**Verified 2026-08-11: PARTIAL.** The clipboard half shipped (commit `11a4633`):
`PosingCore/Files/PoseClipboard.cs` (Brio-compatible compressed JSON), "From clipboard"
import (`PoseFileInspectorSection.cs:970-973`) and "To Clipboard" export (`:445`), both
reachable from the actor context menu and FILES rail, with round-trip tests. Still missing:
the Copy/Paste pair itself — `CleanPoseFacade.Copy`/`.Paste` retain **zero UI callers**
(LiveTest only), the rail Transfer group is still Stash/Apply, and no context-menu
Copy/Paste rows exist. Clipboard also rides `PoseFile`, not the stronger `PortablePose`.

**Resolution 2026-08-11 (honest audit + menu fix):** `Copy`/`Paste` on the facade are the
*same machinery* as Stash/Apply — `PoseTransferService.Stash` is `Capture` into the single
slot, `ApplyStash` is `Apply` of it; the facade pair exists for the harness round-trip
scenario, not as an unshipped feature. The real gap was discoverability, fixed at menu
level: "Stash pose" / "Apply stashed pose" rows added to the actor context menu
(`MainWindow.cs`, pose group; apply disabled until a stash exists). Cross-session/cross-tool
transfer stays with the clipboard. Gap considered closed pending in-game validation.

**Task:** Surface it: "Copy pose" / "Paste pose" buttons in the POSE rail section and actor
context menu (paste disabled until a copy exists, tooltip showing source actor + timestamp
like the existing stash pill). Paste lands as one undoable edit via the existing facade. As
part of the same session, serialize the held `PortablePose` to clipboard JSON on
Ctrl+click/secondary action — the `.pose` serializers already exist, giving cross-session
and cross-tool transfer for free.

### 8. Gaze has no fixed-position target (and no gaze gizmo)

**Reference:** Brio's Position mode: per part (Eyes/Head/Body) an enable toggle,
"set to camera value" snap, and a draggable Vector3 with lock semantics
(`UI/Widgets/Actor/ActorDynamicPoseWidget.cs`, `Game/Actor/ActorLookAtService.cs:71-96`).
Ktisis goes further: a dedicated world-space TRANSLATE gizmo for the gaze target drawn in
the overlay, targets seeded at the actor↔camera midpoint, plus camera-tracking and
gizmo-tracking pseudo-modes (`Interface/Editor/Properties/ActorPropertyList.cs:174-296`,
`Interface/Overlay/OverlayWindow.cs:71-76,123-154`).

**Poser:** partial — GAZE section has Off/Fwd/Cam/Actor with per-part enable+lock
(`PoseInspectorPane.cs:1141-1241`), but no way to aim at an arbitrary world point.

**Verified 2026-08-11: DONE (exceeds spec).** Ships as **"Point"** mode (UI label differs
from the task's "Position"): `GazeTargetMode.Position` with a shared anchor *plus three
divergeable per-part points* (`IGazeService.cs:20,52-59`), actor↔camera midpoint seeding on
mode entry (`GazeService.cs:244-245,542`), per-part numeric Vector3 + snap-to-camera
(`PoseInspectorPane.cs:1580-1665`), world gizmo grabs the gaze target with bone-gizmo
mutual exclusion (`GizmoOverlayWindow.cs:275-282,615-619`), sidebar gaze-target child rows
while active (`MainWindow.cs:986-987`). No history entries, as specced.

**Task:** Add a **Position** gaze mode: seed the target at the actor↔camera midpoint on
enable (Ktisis' `GetCameraLerpFor` behavior), show a numeric Vector3 with a "set to camera"
snap per part, and while Position mode is active with no bone gizmo in use, render the
existing `WorldGizmo` in translate mode at the gaze target so it can be dragged in-world.
Route writes through the existing gaze service; no history entries (matches both references).

### 9. No IK bake ("Set IK Changes")

**Reference:** Brio's `ResetIK` (toolbar Lock button, "Set IK Changes"): exports the current
pose, clears every bone stack, resets IK defaults, then re-imports the export with an
all-components filter — baking solved IK results into plain bone transforms
(`Capabilities/Posing/SkeletonPosingCapability.cs:132-148`,
`PosingOverlayToolbarWindow.cs:532-555`). Ktisis' frozen solver writes results statically
into the pose, so its IK is effectively always baked.

**Poser:** absent — IK is live per chain with full solver config
(`PoseInspectorPane.cs:1263-1448`), but nothing converts the solved result into ordinary
pose edits, so disarming a chain abandons the solved placement and exports/undo interact
with a transient state.

**Verified 2026-08-11: DONE.** `Poser.Game/Posing/IkBakeCapture.cs`: Brio's ResetIK order
(export solved skeleton → clear stacks → disarm → re-import), framework-thread guarded,
refused mid-gesture, full rollback on failure; one history entry "Bake IK"
(`IkBakeCapture.cs:482`). "Bake" button in the IK rail section
(`PoseInspectorPane.cs:1757-1770`), enabled via `CanBake`. Deviation (documented at
`IkBakeCapture.cs:175-178`): bakes the whole skeleton like Brio, not only the chain bones.
Live-test coverage exists (`LiveTestService.cs:943`).

**Task:** Add "Bake IK" to the IK rail section (enabled while the chain is armed and has
produced a solve): capture the solved model-space transforms of the affected chain bones,
write them through the normal bone-edit path as a single undoable history entry, then disarm
the chain. Reuse the capture logic the pose exporter already uses for settled transforms.
Acceptance: arm IK, drag the hand target, bake, disarm — pose holds and exports identically.

### 10. Overlay bone filtering is half-wired (view modes, selected-only, dead settings)

**Reference:** Brio: bone-filter popup with per-category checkboxes, Select All/None,
right-click a category = enable-only, gizmo-stays option
(`UI/Controls/Editors/PosingEditorCommon.cs:95-157`); overlay defaults exclude clothing/
weapons/legacy. Ktisis: per-category eyes plus NSFW filter that actually filters.

**Poser:** a pile of backend-done-but-UI-missing and settings-without-effect:
`ShowSelectedBonesOnly` is read by the overlay but never written
(`SkeletonOverlayWindow.cs:237`); `SkeletonViewMode` (Default/Octahedra/Joints) read but
never written (`:248-259`); `Skeleton.ShowSkeletonLines`, `IkChainColor`,
`MirroredBoneColor`, and `Display.ShowNsfwBones` are saved by Settings but have **no
reader** — the NSFW toggle silently does nothing while IVCS rows always render
(`AnamnesisMatrixTable.cs:98-104`); `BoneDisplayMode`/`DebugMode` are dead state.

**Verified 2026-08-11: NOT STARTED** (all seven sub-items; the cited line numbers drifted —
the read-never-written sites are now `SkeletonOverlayWindow.cs:337-338` and `:349-359` —
but every finding still holds; `Display.ShowNsfwBones` now even defaults **off** while IVCS
rows still render unconditionally, so the shipped default state lies).

**Task:** One wiring session: add a small overlay-options popup on the Armature titlebar
toggle (right-click): view mode selector, "Selected bones only" switch, and a category
checklist backed by the existing `SkeletonOverlayPresentation` mask with Select All/None and
right-click-isolate. In the same pass make the dead settings real: honor `ShowSkeletonLines`,
`IkChainColor` (tint armed-chain bones), `MirroredBoneColor` (tint the symmetry partner
while Mirror/Link is active), and make `ShowNsfwBones` filter IVCS rows in the matrix,
sidebar, and overlay — or delete the rows that are decided against rather than shipping
no-op settings.

### 11. No bone visibility presets

**Reference:** Ktisis: named presets (built-ins from `Categories.xml` — Arms, Body, Face,
Hands, Tail, Weapons, … — plus user presets saved from current visibility), enable via actor
context menu "Presets…", default-on-spawn presets, a manager in Settings
(`Scene/Entities/Game/ActorEntity.cs:197-291`, `Interface/Components/Config/PresetEditor.cs`).

**Poser:** absent — per-row eye toggles only; no way to name, save, or re-apply a visibility
set, and no one-click "face only" workflow.

**Task:** Add a preset store to config (`name → bone-name set`), a "Presets…" submenu on the
actor context menu listing presets with checked state + "Save current as…", and application
through the existing per-bone overlay-visibility mask. Seed 4–5 built-ins (Body, Face,
Hands, Tail, Weapons) from the curated `BoneInfoService` categories. Settings list for
rename/delete/default-on-spawn.

### 12. Cannot bring an overworld actor into the scene

**Reference:** Ktisis "Add overworld actor": popup lists overworld actors, selecting spawns
a GPose copy and forces `SetTargetable(true)` (`Interface/Editor/Popup/OverworldActorPopup.cs:34-51`,
`Scene/Modules/Actors/ActorModule.cs:93-100`). Brio lacks this (open TODO).

**Poser:** absent — the actor list is the GPose object table only
(`ActorManager.cs:114-177`); spawn/clone operate on scene actors and the local player.

**Task:** Add "Add overworld actor…" to the `+` add menu: enumerate the non-GPose object
table (Pc/BattleNpc/EventNpc), list names (respecting anonymous-name masking), and on pick
run the existing clone path with that actor as the copy source, then auto-select like
spawn/clone already do. Verify the copy mechanism against Ktisis' spawn-from-overworld call
sites before implementation (standing rule).

### 13. No actor-to-bone attachment (parenting an actor to another actor's bone)

**Reference:** Ktisis: drag an actor/weapon row onto a bone row (partial 0) to attach —
`AttachUtility.SetBoneAttachment`; link icon shows attachment with detach on click; attached
charas transform relative to the attach point (`Interface/Components/Workspace/SceneDragDropHandler.cs:29-73`,
`Editor/Posing/Attachment/AttachManager.cs`, `Scene/Entities/Character/CharaEntity.cs:94-105`).

**Current Poser:** arbitrary actor-to-bone attachment remains absent by user decision.
Companion/mount/ornament catalog entries spawn as standalone actors; Detach companion
remains for clones that arrive with a slot companion.

**Verified 2026-08-11: session A DONE, session B NOT STARTED.** Companion attach ships via
the `+` spawn browser: catalog rows badged Minion/Mount/Accessory, activation calls
`SetCompanion` (`SpawnBrowserWindow.cs:313-318,448-450`) with real failure notes; reachable
from titlebar, ACTORS header, and context menu. Two holes: `GetCompanionInfo` still has
**zero callers** (the picker is write-only — no current-attachment display), and attach
lives in the spawn browser while Detach stays in the context menu.

**Design pivot (user, 2026-08-11):** the attach-companion *concept* leaves the UI. No model
editing is ever exposed in Poser, and instead of attaching catalog entries to an owner's
slot, the spawn browser's minion/mount/accessory rows now **spawn the entry as its own
actor** — a fresh battle character whose `ModelCharaId` is written internally at spawn
(before first draw; mechanism verified against Brio `ActorAppearanceService`), named from
the sheet, auto-selected, and **automatically classified** by kind
(`IActorSpawnService.GetSpawnedKind`; sidebar shows Paw/Horse/Diamond accordingly).
"Detach companion" remains, gated on `GetCompanionInfo` (its first caller), because clones
can still arrive with a slot companion; `SetCompanion` survives as internal machinery with
no UI caller by design. Do not re-add an attach surface without a new user decision.
Bone attachment (B):
nothing — no drag-drop anywhere in the repo; the only bone-attach code is the lights path,
not reusable for charas.

**Superseded task (session A — user decision 2026-08-11):** the earlier request for
an owner-slot "Attach…" context-menu picker and current-state display is superseded
by the design pivot above. Do not reintroduce that UI without a new user decision.
**Task (session B — native work):** Ktisis-style bone attachment: sidebar drag-drop of an
actor row onto a partial-0 bone row, attach via the skeleton-attach mechanism (grep Ktisis'
`AttachUtility` call sites and verify struct semantics first per standing rule), link
indicator + detach on the attached row.

### 14. No scene save/load

**Reference:** Brio: Save/Load Project — a `SceneFile` storing, per actor, pose file,
appearance, frozen flag, base animation, and companion (with its own pose); load spawns
actors, forces speed 0, applies pose/appearance (`Game/Scene/SceneService.cs:155-217`,
`Files/ActorFile.cs:14-60`), plus the auto-save integration (gap 4).

**Poser:** absent — and the shell's project affordance is hard-disabled
(`MainWindow.cs:410` sets `ShowProject = false`; `AppShellView.cs:416-422` renders it only
when enabled).

**Task:** A pose-scene v-slice honoring the appearance exclusion (appearance stays
Glamourer's): serialize spawned-actor list with nickname, world-transform override, pause
state, and embedded pose (reuse `PoseFile`), save/load via the existing file dialogs; load
spawns through `ActorSpawnService`, pauses, applies poses. Re-enable the shell's Project
button as the entry point. Appearance round-trip is explicitly out of scope; note in the
file format that actors rely on external appearance state.

### 15. Poser exposes no IPC

**Reference:** Brio provides `Brio.*` v2.0 — spawn/despawn, model transform get/set/reset,
pose load-from-JSON/file, get-pose-as-JSON, reset, speed, freeze/unfreeze, physics
(`IPC/BrioIPCService.cs`); Ktisis provides `Ktisis.*` v1 — LoadPose/SavePose, bone matrix
get/set/batch, SelectedBones, RefreshActors (`Interop/Ipc/IpcProvider.cs`). Third-party
tools drive posing through these.

**Poser:** absent — no `GetIpcProvider` anywhere.

**Task:** Stand up a Poser IPC provider mirroring the Brio v2 names (so existing consumers
work by string swap or shim): `ApiVersion`, `Actor.Pose.LoadFromJson`/`GetPoseAsJson`/
`Reset`, `Actor.SetModelTransform`/`GetModelTransform`/`ResetModelTransform`,
`Actor.Spawn`/`Despawn`/`Exists`/`GetAll`, `Actor.Freeze`/`UnFreeze`/`SetSpeed`/`GetSpeed`,
`FreezePhysics`/`UnFreezePhysics` — each a thin adapter over the existing facades
(`CleanPoseFacade`, `CleanTransformFacade`, `ActorSpawnService`, `AnimationSession`). Gate
behind a settings toggle like Brio's `EnableBrioIPC`.

### 16. Keybind coverage is thin

**Reference defaults:** Brio — toggle overlay Ctrl+O, Esc clears bone selection (and is
swallowed from the game while a bone is selected), mirror-mode cycle L, freeze actor
Shift+F, select-all-actors, held Ctrl/Shift/Alt = disable picking / disable gizmo / hide
overlay, toggle world/local (`Config/InputManagerConfiguration.cs:9-49`). Ktisis — flip pose
Ctrl+F, select mirrored sibling `\`, gizmo ops Ctrl+T/R/S/U, world/local Ctrl+X, overlay
Ctrl+O, gizmo toggle Ctrl+G (`Actions/Handlers/*`).

**Poser:** 7 actions (Undo, Redo, 4 tool modes, Hide UI — `PoserKeybinds.cs:13-22`), plus a
hardcoded Alt-hides-dots in the overlay. Esc only cancels a live gizmo drag. No binds for
overlay toggle, clear selection, flip, symmetry cycle, pause actor, space toggle, sibling
select.

**Task:** Extend the keybind action set with: Toggle skeleton overlay, Clear selection
(Esc, plus swallow game ESC while a bone is selected, per Brio's `AllowEscape` pattern —
verify the input-suppression mechanism first), Toggle space, Cycle symmetry, Flip bone,
Pause/resume actor, Select mirrored bone. All targets are existing commands; the work is
`PoserKeybinds` entries, `UIManager` dispatch, and Settings rebind rows, which already
generalize.

### 17. Import options: model transform, ear exclusion, anchor positions

**Reference:** Brio: "Import Model Transform" toggle in the popup; Ktisis: "Exclude ear
bones" (filters 20 ear-bone names so Viera/ear poses don't corrupt other races,
`PosingManager.cs:43-55`) and "Anchor group positions" (restores original positions of the
selection's top-level bones after a selected-bones+position import so groups don't drift,
`PosingManager.cs:242-253`).

**Current Poser:** the Model transform option is wired and honored. Ear handling is
filter-only, while selected-scope anchoring remains absent and is accepted as-is by the
user decision recorded below.

**Verified 2026-08-11: PARTIAL.** (a) **DONE** — "Model" checkbox
(`PoseFileInspectorSection.cs:1100-1105`) wired through `BuildOptions` and honored
(`PoseFileService.cs:242`). (b) filter-only — no dedicated exclude-ears control; an "Ears"
category exists in the Brio-style bone-filter popup (covers Ktisis' 20 names via prefixes)
but the filter is disabled whenever Body/Expression typed import is active
(`:1133-1141`, `:2163-2165`), i.e. exactly the common paths. (c) **NOT STARTED — and its
precondition was removed**: the Selected-scope/descendants rows were dropped from the
import UI (user 2026-08-10, `:1124-1125`); selected-bones import is now backend-only dead
code (`CleanPoseFacade.cs:195-222` has no UI caller passing bones). The import surface was
otherwise rebuilt well beyond this task: Smart import, clipboard both ways, Reapply last,
From stash, Brio bone-filter popup, live CharaView preview, shared three-mount options band.

**User call 2026-08-11: accepted as-is** ("import is good enough for now") — (b) and (c)
stay parked; do not reopen without a new user decision.

**Task:** Three additions to the FILES import options: a "Model transform" checkbox wiring
the existing flag (trivial); an "Exclude ears" checkbox implemented as a name-set filter in
`PoseFileService` using Ktisis' 20-name list; and an "Anchor positions" checkbox available
for Selected scope + Translation that captures the pre-import positions of the top-level
selected bones and restores them post-import inside the same undoable edit.

### 18. No per-bone / per-actor transform lock

**Reference:** Brio: "Freeze Transforms" checkbox per bone (`Bone.Freeze`) and per actor
(`ModelPosing.Freeze`) — gizmo, trackball, and numeric edits are dropped for frozen targets,
surfaced in red in the selection header (`PosingTransformEditor.cs:300-316`,
`PosingOverlayWindow.cs:628,643`).

**Poser:** absent.

**Task:** Add a `Locked` flag on bone/actor pose state; when set, the gizmo skips the
target, numeric wells render disabled, and pose import skips the bone (matching Brio's
behavior of protecting deliberate placements). Affordance: padlock toggle in the rail header
next to the selection summary plus a bone/actor context-menu item; locked rows get a
padlock glyph in the sidebar.

### 19. Linked-bones behavior has no user control

**Reference:** both tools put their equivalent linkage/mirror behaviors behind visible,
user-controllable state.

**Poser:** backend-only toggle — `IBonePosingService.LinkedBonesEnabled` defaults true and
is consumed by the pane and gizmo, but no UI writes it (`IBonePosingService.cs:150`); the
only surface is the read-only "Linked N" pill (`PoseRailPane.cs:70-98`).

**Task:** Make the "Linked N" pill clickable as the on/off toggle for
`LinkedBonesEnabled` (visual off-state when disabled), persisted to config, with a matching
Settings row under Skeleton. Small session; can be bundled with gap 17.

### 20. No ray-snap translate (place on surface)

**Reference:** Ktisis: holding Shift during a translate gizmo drag raycasts
`ScreenToWorld` and snaps the translation to the surface under the cursor — the fastest way
to put an actor on the ground/furniture (`Interface/Overlay/OverlayWindow.cs:108,156-168`,
config `Gizmo.AllowRaySnap`).

**Poser:** absent.

**Task:** Add surface snap to `GizmoOverlayWindow` translate drags on a held modifier:
raycast via the game's BGCollision screen-to-world (verify Ktisis' `ScreenToWorld` call
sites and the sig it uses before implementation — standing rule), and while held, set the
dragged target's world position to the hit point. Respect the existing one-gesture-one-
history-entry contract. Config toggle mirroring `AllowRaySnap`.

### Smaller / polish gaps (not top-10 material, kept for completeness)

| Gap | Reference | Poser state |
|---|---|---|
| Undo/redo tooltips show what will be undone | (Poser's own backend) | `UndoDescription`/`RedoDescription` exist unused (`CleanTransformFacade.cs:31-34`); badges show static text |
| Mouse-wheel nudge on hovered gizmo rings / numeric fields | Brio `ImGuizmoExtensions.cs:10-45`; Ktisis `TransformTable.cs:200-218` | rail rings have drag-only; numeric wells drag-only |
| Wheel-cycling the overlay disambiguation popup | Brio `PosingOverlayWindow.cs:342-397`; Ktisis `SelectableGui.cs:63-153` | hover list exists, no wheel cycling |
| Per-bone / per-actor transform movement speed | Brio `PosingTransformEditor.cs:282-318` | Ctrl/Shift multipliers only |
| Undo depth setting | Brio `UndoStackSize` (Settings, default 50) | fixed internal depth, no setting |
| "Open with GPose / Close with GPose" settings do nothing | (Poser's own settings) | saved (`SettingsWindow.cs:118-119`), no reader — implement or remove |
| Sidebar/inspector dock + tree-guide settings do nothing | (Poser's own settings) | saved (`SettingsWindow.cs:136-138`), zero readers — implement or remove |
| Reference images overlay | Ktisis `ReferenceImage` entity + `Editor.ReferenceImages` | absent |
| Custom 2D pose-view images per view | Ktisis `PoseViewConfig` + Settings → Pose View | absent (embedded maps only) |
| Per-race overlay bone-dot offsets | Ktisis `OffsetConfig` + offset editor | absent |
| Spawn-frozen option | Brio `SpawnEx(spawnFrozen)` IPC + prop spawn | spawn always live; pause is a separate click |

### Explicitly *not* gaps (verified better-or-equal in Poser)

- Weapon/prop/ornament slots in pose files: Poser round-trips MainHand/OffHand/Prop/Ornament;
  Ktisis never writes them (`EntityPoseConverter.cs:33` TODO), Brio does.
- Import scopes: Poser's per-component + reset-first + Brio type matrix matches both
  references' popup options (given gap 17's remaining items). *2026-08-11 note:* the
  Selected/descendants scope this bullet originally credited was removed from the UI on
  2026-08-10 (user call) — the import surface now mirrors Brio's popup; selected-bones
  import survives backend-only.
- Stance/idle-pose control, weapon drawn, position lock, per-layer speed/pause, physics
  freeze: present with real UI (Animation tab / toolbar).
- Stash/apply pose transfer: parity with Ktisis' stash, including timestamp.
- Graphical maps: marquee box-select and race-variant faces match Brio; matrix view exceeds
  both (Anamnesis-style).
- Expression action-unit sliders and gaze per-part locks: no reference equivalent
  (Brio's Actor gaze mode is a stub; Ktisis disables gaze while posing).
- Command-only surface: nothing user-facing is command-gated; `/poser` only opens the window
  and runs the validation harness.
