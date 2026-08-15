# Brio/Ktisis parity checklist

> **Recreated 2026-08-03.** The previous `docs/brio/` tracking docs (`parity-checklist.md`,
> `ktisis-audit.md`, `anamnesis-audit.md`, `ui-coverage.md`) are gone — the directory was empty
> and git has never tracked those filenames on any branch. This file is rebuilt from a fresh
> three-way source audit; it does not carry over any earlier Done/Not-done rows.
>
> Runtime/source basis: Poser code `HEAD` `e6c2c77`; rows 2, 9, 12, 13A, and 17
> plus the polish-table dispositions were re-verified 2026-08-14 against the
> integration head `42d41bd` (evidence: parity-checklist-disposition, 39 rows).
> Reference basis: Ktisis clone @
> `a5ae200d` (0.3.9.2 with the 0.4-style layout) and Brio clone @ `73bb59d`.
> Inherited documentation snapshots informing this checklist were
> `docs/validation/poser-feature-gap-audit-2026-08-12.md`,
> `docs/validation/poser-code-health-audit-2026-08-12.md`,
> `docs/validation/code-health-remediation-plan-2026-08-12.md`, and
> `docs/validation/backend-maintainability-audit.md`; mechanisms were
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

### Source-verified / acceptance-pending pass 2026-08-12, re-swept 2026-08-15 for the first-beta release (only the user calls live behavior Accepted)

In this table, **Source-verified** means the implementation or product decision
is resolved by source inspection and/or an explicit user decision. It does not
mean live-game acceptance; that remains pending on the applicable rows.

| Gap | Source status |
|---|---|
| 1 Redraw pose carryover | **Source-verified; acceptance pending** |
| 2 Rest poses | **Source-verified; acceptance pending** — A/T done (import surfaces, per user rule 2026-08-08); reference pose UI-exposed 2026-08-14 behind a two-step armed confirm in the Presets row |
| 3 Pose library | **Source-verified; acceptance pending** (exceeds spec) |
| 4 Auto-save | **Source-verified; acceptance pending** |
| 5 Freeze-on-import | **Source-verified; acceptance pending** |
| 6 Target sync | **Source-verified; acceptance pending** |
| 7 Copy/paste pose UI | **Source-verified; acceptance pending** — stash/apply is the retained UI; clipboard covers cross-session transfer |
| 8 Gaze fixed-position | **Source-verified; acceptance pending** (ships as "Point" mode, exceeds spec) |
| 9 IK bake | **Implemented, on safety hold** — the convergence brief's standing exclusion governs; not accepted, no live card |
| 10 Overlay filter wiring | **DONE (source-verified 2026-08-15); acceptance pending** — every one of the seven sub-items is live: `ShowSelectedBonesOnly` is written (keybind `UIManager.cs:184`, Settings) and filters (`SkeletonOverlayWindow.cs:568`), `SkeletonViewMode` is written (cycle keybind `UIManager.cs:186`, Settings) and switched on (`:586`), `ShowSkeletonLines` (`:591`), `IkChainColor` (`:1328`) and `MirroredBoneColor` (`:1330`) all have readers, `ShowNsfwBones` filters the matrix, the maps, the inspector and the overlay (`BoneMatrixBuilder.cs:47`, `GraphicalBonePane.cs:265,613`, `PoseInspectorPane.cs:1375`, `MainWindow.cs:2522`, `SkeletonOverlayWindow.cs:470`), and the dead `BoneDisplayMode`/`DebugMode` state is gone |
| 11 Bone visibility presets | **DONE (source-verified 2026-08-15); acceptance pending** — `Poser/UI/BoneVisibilityPresetService.cs` with the preset store in `SkeletonConfiguration`, registered in `ServiceRegistration.cs`, applied from `MainWindow`, covered by `Poser.ContractTests/BoneVisibilityPresetContractTests.cs` |
| 12 Overworld actor | **Source-verified; acceptance pending** — implemented and reviewed (`d7603ca` backend, `44cb748` World tab, `42d41bd` refresh fix); the tab was removed 2026-08-15 and the adoption is the viewport's own handles, marked by the sidebar footer's class glyphs (`58892b3`) |
| 13A Companion attach UI | **Source-verified; acceptance pending** — gated attach picker + detach live in the actor context menu (user decision 2026-08-14 supersedes the 2026-08-11 "do not re-add") |
| 13B Actor-to-bone attach | not started |
| 14 Scene save/load | **DONE (source-verified 2026-08-15); acceptance pending** — the whole subsystem ships: capture, ordered load with per-step outcomes and `SceneLoadOptions`, autosave, and the Scene pane (`Poser.Game/Scene/SceneCaptureService.cs`, `SceneLoadOptions.cs`, `SceneAutoSaveService.cs`, `SceneRuntimeAdapter.cs`, `Poser/UI/Panes/ScenePane.cs`), tested in `Poser.Game.Tests/Scene/SceneWorkflowTests.cs`. Scenes also record borrowed world objects and take them back on load in the same zone |
| 15 IPC provider | not started |
| 16 Keybind expansion | **PARTIAL** — dual slots, 24 actions, Poser/Brio/Ktisis presets and conflict flagging shipped; Esc-clear-selection, flip, sibling select and per-actor pause still unbound |
| 17 Import options | **PARTIAL** — (a) done, (b) filter-only (parked, user call 2026-08-11), (c) precondition restored by selective import; anchor-positions slice assigned (user decision 2026-08-14: implement now) |
| 18 Transform lock | not started |
| 19 Linked-bones toggle | **DONE (source-verified 2026-08-15); acceptance pending** — `BoneLinkCatalog` (Anamnesis' same-delta groups) behind `IBonePosingService.LinkedBonesEnabled`, written from the shell toggle (`MainWindow.cs:1344`) and honoured by the gizmo (`GizmoOverlayWindow.cs:1405`) and the inspector (`PoseInspectorPane.cs:2485,2848`); the runtime port suspends it for its own writes (`TransformRuntimePort.cs:96-115`) |
| 20 Ray-snap translate | **DONE (source-verified 2026-08-15); acceptance pending** — `GizmoConfiguration.AllowRaySnap` (off by default) is read during a translate drag and Shift runs the snap after the increment snap, matching Ktisis' precedence (`GizmoOverlayWindow.cs:1508,1565,1569`); the switch is in Settings |
| Polish table | GPose-open/close and dock/tree-guide rows fixed; the eight unscheduled rows joined the small-parity queue (user decision 2026-08-14: schedule all) |

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
`Poser.Core/Data/RestPoses/BrioAPose.pose`/`BrioTPose.pose` (`RestPoses.cs:29-30`),
`CleanPoseFacade.ApplyRestPose` (one undoable edit, rotation-only body,
reset-before-import for A→T→A idempotence), "Presets" row with A-pose/T-pose buttons in the
import surfaces (`PoseFileInspectorSection.cs:992-999`) reachable from actor context menu,
titlebar burger, and FILES Import — per user rule 2026-08-08 rest presets live with import,
not the POSE rail.

**Superseding user decision 2026-08-14 (with the selective-import range): the reference
pose is UI-exposed.** A "Reference" button sits in the Presets row behind a two-step armed
confirm — arm shows the visible warning, the second press applies as one undoable edit, and
a reopened menu disarms (`PoseFileInspectorSection.cs:1047-1052`, `:421`,
`ApplyReferencePreset`). Placement in the Presets row and session-only toggles were the
user's explicit calls. Live check rides the combined live card.

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
but never surfaced (`Poser.Core/Files/PoseFile.cs:23-24`).

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

**Verified 2026-08-11: DONE.** `AutoSaveService` (`Poser.Core/Files/AutoSaveService.cs`),
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

**Poser:** absent — `Poser.Core/Files/PoseFileService.cs` has zero animation interaction, so
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
`Poser.Core/Files/PoseClipboard.cs` (Brio-compatible compressed JSON), "From clipboard"
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

**Verified 2026-08-11: implemented — ON SAFETY HOLD (downgraded from "DONE" 2026-08-14).**
The implementation exists in the tree but is not accepted: the convergence brief's standing
exclusion ("Do not treat Bake IK as accepted") governs, the rejected diagnostics chain
(`634fb30`/`5088c27`/`fdae242`) is confirmed absent from the head lineage, the live card
excludes Bake, and only the user can authorize the tranche that would un-park it.
Implementation facts: `Poser.Game/Posing/IkBakeCapture.cs`: Brio's ResetIK order
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

**Verified 2026-08-15: DONE (source-verified; live acceptance pending).** All seven
sub-items are closed, superseding the 2026-08-11 "NOT STARTED" finding.
`ShowSelectedBonesOnly` is now written by the "Selected bones only" keybind
(`UIManager.cs:184-185`) and by Settings (`SettingsWindow.cs:119,345`), and it filters the
dot pass (`SkeletonOverlayWindow.cs:568`, cached predicate at `:801`). `SkeletonViewMode`
is written by the "Cycle skeleton view" keybind (`UIManager.cs:186-191`) and by Settings
(`SettingsWindow.cs:118,343`), and it selects the shape (`SkeletonOverlayWindow.cs:586-591`).
`ShowSkeletonLines` (`:67,591`), `IkChainColor` (`:64,1328`) and `MirroredBoneColor`
(`:65,1330`) all have readers. `Display.ShowNsfwBones` now genuinely filters — the matrix
(`BoneMatrixBuilder.cs:47`, `MainWindow.cs:2522`), the graphical maps
(`GraphicalBonePane.cs:265,613`), the inspector (`PoseInspectorPane.cs:1375`), the overlay
(`SkeletonOverlayWindow.cs:470`) and the curated categories (`BoneInfoService.cs:73,88`) —
so the default-off state no longer lies. `BoneDisplayMode` and `Skeleton.DebugMode` were
deleted rather than wired; no reference to either survives.

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

**Verified 2026-08-15: DONE (source-verified; live acceptance pending).** The preset store
lives in `SkeletonConfiguration`, the service that names, saves, applies and seeds the
built-ins is `Poser/UI/BoneVisibilityPresetService.cs` (registered in
`Poser/Composition/ServiceRegistration.cs`), and the presets are reachable from
`MainWindow`. Contract coverage: `Poser.ContractTests/BoneVisibilityPresetContractTests.cs`.

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

**Verified 2026-08-14: DONE (implemented + reviewed; live acceptance pending).** Read-only
overworld discovery lives outside the 201–439 write gate
(`Poser.Game/LegacyRuntime/WorldActorDiscovery.cs`, `Poser.Application/Actors/IWorldActorReadPort.cs`,
integrated `d7603ca`); the spawn browser gained a World tab — nearest-first snapshot rows,
clone-on-activate through the typed import, stale refusals restate and re-list (`44cb748`),
structural row refresh deferred to Draw start (`42d41bd`). **The tab is gone as of
2026-08-15 (`58892b3`)**: the same clone is reached by clicking a world handle in the
viewport, and which classes draw handles is the sidebar footer's class glyphs. The clone is
the one crossing into the scene and enters through the owned spawn transaction. Live check: rides the spawn
card as an added step.

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

**Newer user decision 2026-08-14: the gated companion attach picker stays.** Both slot
verbs are actor-context-menu rows: "Attach companion" opens
`Poser/UI/Panes/CompanionSection.cs` — a catalog picker seeded from the slot's current
contents (`GetCompanionInfo` at `:112`), one `SetCompanion` call attaching or swapping
(`:141`) — disabled unless `HasCompanionSlot` holds; "Detach companion" stays gated on
`GetCompanionInfo` (`MainWindow.cs:2797-2803`, picker opened at `:2835`). This supersedes
the 2026-08-11 "do not re-add" instruction, and closes the write-only-picker /
zero-`GetCompanionInfo`-callers holes recorded above.

**Task (session A):** closed by the decision above — the owner-slot attach picker and
current-state display exist; only live acceptance remains.
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

**Verified 2026-08-15: DONE (source-verified; live acceptance pending)** — and it exceeds
the v-slice below. Capture, an ordered load with per-step outcomes and caller-chosen
`SceneLoadOptions`, autosave on a cadence, and a versioned file with typed refusals for
too-old/too-new/damaged documents all ship (`Poser.Game/Scene/SceneCaptureService.cs`,
`SceneLoadOptions.cs`, `SceneAutoSaveService.cs`, `SceneRuntimeAdapter.cs`,
`Poser.Core/Files/SceneFileValidation.cs`), driven from `Poser/UI/Panes/ScenePane.cs` and
covered by `Poser.Game.Tests/Scene/SceneWorkflowTests.cs`. A scene carries actors, lights,
camera, environment and the world objects it borrowed — appearance and gaze targets stay
out, per the standing exclusion. `docs/features/scenes.md` is the normative description.

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

**Poser:** 24 actions with a PRIMARY and a SECONDARY chord each, listed once in
`KeybindRegistry` and dispatched from `UIManager.BuildKeybinds`. The settings page rebinds
both slots by capture-on-click, flags a chord bound twice on both of its rows, and switches
the whole table between Poser, Brio and Ktisis chord sets. Config v3 turns a pre-existing
single binding into that action's primary. Still a hardcoded Alt-hides-dots in the overlay;
Esc still only cancels a live gizmo drag.

**Remaining:** Clear selection (Esc, plus swallowing the game's ESC while a bone is
selected, per Brio's `AllowEscape` pattern — verify the input-suppression mechanism first),
Flip bone, Select mirrored bone, and per-actor pause. Each needs its command surfaced to
`UIManager` first; the registry, the dispatch table and the rebind rows all take a new
action by one entry.

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
(`:1133-1141`, `:2163-2165`), i.e. exactly the common paths. (c) **precondition RESTORED
(2026-08-14)**: the selective-import range brought Selected-bones/Include-descendants back
as dialog-only rows with confirm-time freezing (`2465157`), running through the one
pose-import transaction (`0818dc2`) with Ktisis bypass semantics (`7a13086`). Anchor
positions itself remains absent at head (no anchor-position capture anywhere in
`PoseFileService`/inspector). The import surface was
otherwise rebuilt well beyond this task: Smart import, clipboard both ways, Reapply last,
From stash, Brio bone-filter popup, live CharaView preview, shared three-mount options band.

**User calls:** (b) stays parked ("import is good enough for now", 2026-08-11); (c) was
re-decided 2026-08-14 — **implement the anchor-positions checkbox now**; the slice is
assigned to the selective-import writer.

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
| Undo/redo tooltips show what will be undone | (Poser's own backend) | **fixed** — both descriptions are pumped into the shell view-model (`MainWindow.cs:1348-1349`) and are what the undo/redo affordances say (`AppShellView.cs:790,799,1483,1492`) |
| Mouse-wheel nudge on hovered gizmo rings / numeric fields | Brio `ImGuizmoExtensions.cs:10-45`; Ktisis `TransformTable.cs:200-218` | **half fixed** — numeric wells take the wheel with ImGui's own claim (`Poser.UI/Primitives/Tags/AxisWell.cs:52-85`, `PoseFileInspectorSection.cs:1865-1898`); the gizmo's rail rings are still drag-only — **Scheduled** (user 2026-08-14, small-parity queue) |
| Wheel-cycling the overlay disambiguation popup | Brio `PosingOverlayWindow.cs:346-450`; Ktisis `SelectableGui.cs:101-158` | **fixed** — the whole lifecycle is ported per mode, not just the cycling. The cluster preview follows the cursor, takes no input and dies with the hover in both modes; Ktisis then wheels a carried highlight the click commits, while Brio raises its second surface — an anchored pick popup that outlives the hover, takes the dots out of play while up, scrubs the selection one entry per notch and closes on Escape, a press outside, or a picked row (`SkeletonOverlayWindow.PreviewVisible`/`PickPopupOpens`/`PickPopupStaysOpen`/`BrioPickStep`, covered in `Poser.ContractTests/OverlayGizmoContractTests.cs`) |
| Per-bone / per-actor transform movement speed | Brio `PosingTransformEditor.cs:282-318` | **fixed** — separate `EntitySpeed`/`BoneSpeed` chosen per edited thing (`Poser.Core/Config/TransformConfiguration.cs:18-26`), both in Settings as the "Transform Slider Speed" pair |
| Undo depth setting | Brio `UndoStackSize` (Settings, default 50) | **fixed** — `UndoDepth` in config, wired at registration (`ServiceRegistration.cs`) and exposed in Settings; recovery-tested (`Poser.Core.Tests/Core/ConfigurationRecoveryTests.cs`) |
| "Open with GPose / Close with GPose" settings do nothing | (Poser's own settings) | **fixed** — both read now (`UIManager.cs:104,107`, `UiWindowSet.cs:89`) |
| Sidebar/inspector dock + tree-guide settings do nothing | (Poser's own settings) | **resolved** — `ShowTreeGuides` read (`ShellSidebar.cs:192`); dock settings removed |
| Reference images overlay | Ktisis `ReferenceImage` entity + `Editor.ReferenceImages` | **fixed** — reference pictures are floating, aspect-locked, opacity-dimmed windows that join the Overlays group and persist across leaving GPose (`Poser/UI/Windows/ReferenceImageWindow.cs`, `ReferenceImageSession.cs`, `ReferenceImageGeometry.cs`, `Poser.Core/Config/ReferenceImageConfiguration.cs`), covered by `Poser.ContractTests/ReferenceImageContractTests.cs` |
| Custom 2D pose-view images per view | Ktisis `PoseViewConfig` + Settings → Pose View | absent (embedded maps only) — **Scheduled** (user 2026-08-14, small-parity queue) |
| Per-race overlay bone-dot offsets | Ktisis `OffsetConfig` + offset editor | absent — **Scheduled** (user 2026-08-14, small-parity queue) |
| Spawn-frozen option | Brio `SpawnEx(spawnFrozen)` IPC + prop spawn | **fixed** — `PoserConfiguration.SpawnFrozen` applies to every actor Poser adds, the spawn browser's own rows and world adoption alike (`WorldAdoptionSource.cs:369-377`) |

### Explicitly *not* gaps (verified better-or-equal in Poser)

- Weapon/prop/ornament slots in pose files: Poser round-trips MainHand/OffHand/Prop/Ornament;
  Ktisis never writes them (`EntityPoseConverter.cs:33` TODO), Brio does.
- Import scopes: Poser's per-component + reset-first + Brio type matrix matches both
  references' popup options (given gap 17's remaining items). *2026-08-14 note:*
  selected/subtree import is UI-reachable again — dialog-only Selected-bones/descendants
  rows with Ktisis bypass semantics (`2465157`, `7a13086`), superseding the 2026-08-11 note
  that it survived backend-only.
- Stance/idle-pose control, weapon drawn, position lock, per-layer speed/pause, physics
  freeze: present with real UI (Animation tab / toolbar).
- Stash/apply pose transfer: parity with Ktisis' stash, including timestamp.
- Graphical maps: marquee box-select and race-variant faces match Brio; matrix view exceeds
  both (Anamnesis-style).
- Expression action-unit sliders and gaze per-part locks: no reference equivalent
  (Brio's Actor gaze mode is a stub; Ktisis disables gaze while posing).
- Command-only surface: nothing user-facing is command-gated; `/poser` only opens the window
  and runs the validation harness.
