# Brio/Ktisis parity checklist

> **Recreated 2026-08-03.** The previous `docs/brio/` tracking docs (`parity-checklist.md`,
> `ktisis-audit.md`, `anamnesis-audit.md`, `ui-coverage.md`) are gone — the directory was empty
> and git has never tracked those filenames on any branch. This file is rebuilt from a fresh
> three-way source audit; it does not carry over any earlier Done/Not-done rows.
>
> Audit basis: Poser `feature/imperative-rebuild` @ `cd77073` (working tree), Ktisis clone @
> `a5ae200d` (0.3.9.2 with the 0.4-style layout), Brio clone @ `73bb59d`. Mechanisms were
> verified against reference call sites, not doc claims.
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

**Poser:** implemented (awaiting in-game validation) — `AutoSaveService`
(`PosingCore/Files/AutoSaveService.cs`): interval + GPose-exit snapshots of
authored-edit actors via `ExportPose` into `<configDir>/AutoSaves/<UTC>/`,
disk-based retention, clean-on-exit, Settings rows (General → AUTO-SAVE),
FILES "Auto-saves…" recovery entry. Normative:
`docs/features/files-and-transfer.md` § Auto-save.

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

**Poser:** backend-only — `CleanPoseFacade.Copy`/`.Paste` exist and are exercised only by
`LiveTestService` (`CleanPoseFacade.cs:294,297`); the validation harness even has a
`posing.copy-paste-pose` scenario for it.

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

**Poser:** absent for arbitrary attachment. Related but distinct: companion/mount/ornament
attach is **backend-only** — `IActorSpawnService.SetCompanion`/`GetCompanionInfo` are
implemented with zero callers (`ActorSpawnService.cs:290,365`) while Detach companion has UI.

**Task (session A — cheap, backend exists):** companion attach UI: an "Attach…" actor
context-menu item opening a searchable companion/mount/ornament picker feeding
`SetCompanion`, displaying current state from `GetCompanionInfo`, next to the existing
Detach item.
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

**Poser:** `ApplyModelTransform` is backend-only — the option exists and is honored
(`PoseImportOptions.cs:59`, `PoseFileService.cs:209`) but `PoseFileInspectorSection.BuildOptions`
never sets it (`PoseFileInspectorSection.cs:119-145`). Ear exclusion and anchoring are absent.

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
- Import scopes: Poser's Full/Body/Expression/Selected + per-component + descendants +
  reset-first matches or exceeds both references' popup options (given gap 17's three items).
- Stance/idle-pose control, weapon drawn, position lock, per-layer speed/pause, physics
  freeze: present with real UI (Animation tab / toolbar).
- Stash/apply pose transfer: parity with Ktisis' stash, including timestamp.
- Graphical maps: marquee box-select and race-variant faces match Brio; matrix view exceeds
  both (Anamnesis-style).
- Expression action-unit sliders and gaze per-part locks: no reference equivalent
  (Brio's Actor gaze mode is a stub; Ktisis disables gaze while posing).
- Command-only surface: nothing user-facing is command-gated; `/poser` only opens the window
  and runs the validation harness.
