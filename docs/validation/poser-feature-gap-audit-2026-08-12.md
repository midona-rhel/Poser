# Poser feature-gap audit — Brio and Ktisis

Date: 2026-08-12. This is a non-normative product audit, not a parity mandate.
Eight independent Luna audits compared current user-reachable Poser behavior
against Brio and Ktisis, then ran a separate adversarial validation pass.

Explicit exclusions:

- Do not add Brio-style animation timeline authoring.
- Do not move equipment, customization, dyes, materials, or saved-design
  ownership out of Glamourer. Poser should keep only the presentation and
  native capabilities that need to be local, such as Model ID and granular
  wetness.

## Executive conclusion

Poser already has a broad, unusually careful posing runtime. Its largest gaps
are no longer basic transforms or missing panes. They are complete user
journeys: saving a whole composition, bringing real world actors into it,
authoring relationships between scene objects, and knowing whether an
asynchronous or recovery operation actually succeeded.

The most valuable work is therefore not a long tail of parity checkboxes. It is
the following compact set of product improvements.

## Highest-impact improvements

### 1. Save and restore the whole shot

**What the user hits:** after arranging several actors, props, lights, cameras,
weather, and environment state, leaving GPose destroys the composition. Pose
autosaves recover individual authored actor poses, but not placements, spawned
entities, camera framing, lighting, or environment.

**Why improve it:** this is the clearest missing end-to-end workflow. Poser now
owns enough scene entities that the original “projects deferred” boundary has
become expensive. A scene file and scene-level recovery snapshot would prevent
hours of reconstruction.

**Evidence:** Poser forcibly hides Projects (`Poser/UI/Windows/MainWindow.cs:1056`)
and autosaves actor `.pose` files (`PosingCore/Files/AutoSaveService.cs:249-320`).
Brio captures and imports complete scenes (`Brio/Brio/Services/SceneService.cs:248-347`)
and exposes scene Save/Load (`Brio/Brio/UI/Controls/Stateless/FileUIHelpers.cs:98-117`).
Ktisis persists actors, lights, cameras, environment, and overlays
(`Ktisis/Services/Data/SceneDataService.cs:87-228`) behind Load/Save/Apply
(`Ktisis/Interface/Windows/Editors/SceneWindow.cs:117-130`).

**Classification:** intentional scope cut worth reconsidering. **Confidence:** high.

### 2. Add nearby overworld actors directly

**What the user hits:** a visible NPC or player outside the GPose actor range
cannot be brought into the scene with its current identity. The user must find
a catalog approximation or rebuild it manually.

**Why improve it:** “use that actor standing there” is a concrete creation
workflow, not appearance ownership. It removes error-prone recreation and is
already proven in both references.

**Evidence:** Poser discovers only GPose-table slots and its spawn browser offers
new, clone, and catalog paths (`Poser.Game/LegacyRuntime/ActorManager.cs:160-174`,
`Poser/UI/Windows/SpawnBrowserWindow.cs:302-366`). Brio exposes Actor from World
(`Brio/Brio/UI/Controls/Editors/SpawnMenu.cs:96`) and Ktisis exposes an overworld
picker (`Ktisis/Interface/Editor/Popup/OverworldActorPopup.cs:48-55`).

**Classification:** missing workflow. **Confidence:** high.

### 3. Make scene relationships first-class

**What the user hits:** Poser can attach a catalog minion/mount/ornament through
the native companion slot, and can attach a light to a bone. It cannot attach a
separately spawned actor or prop to a hand, weapon, head, or other bone. A
manually aligned object stops following as soon as the parent pose changes.
Light attachment is also lost when the light is cloned or saved and reloaded.

**Why improve it:** attachment is what turns separately edited entities into a
composition. It should have attach, detach, inspect-parent, clone, and scene-file
semantics rather than being a special case in two panes.

**Evidence:** Poser exposes companion attachment in the actor menu
(`Poser/UI/Windows/MainWindow.cs:2763-2805`) and light attachment in
`Poser/UI/Panes/LightPane.cs:513-536`, but light copying omits the relationship
(`Poser.Game/Lighting/LightingService.cs:394-471`). Ktisis accepts scene entities
on concrete bone targets (`Ktisis/Interface/Components/Workspace/SceneDragDropHandler.cs:40-71`,
`Ktisis/Editor/Posing/Attachment/AttachUtility.cs:13`). Brio at least consumes
native attachment relationships during skeleton reparenting
(`Brio/Brio/Game/Posing/SkeletonService.cs:165`).

**Classification:** meaningful Ktisis-proven extension; current companion/light
paths are real and should be reused. **Confidence:** high.

### 4. Harden spawned-actor and companion lifecycle behavior

**What the user hits:** cloning an actor with a companion reserves a slot but
does not reproduce the attached minion, mount, or ornament. Selecting the child
row does not offer a child-side detach. Attach timeouts are logged after the
picker closes rather than reported to the user. More seriously, Poser records
spawn ownership by object-table index; if another tool destroys the object and
the index is reused, ownership can become stale.

**Why improve it:** actor deletion is destructive. Ownership must follow exact
native lifetime/generation, while clone and detach should preserve the scene the
user sees.

**Evidence:** Poser clone and ownership paths are in
`Poser.Game/LegacyRuntime/ActorSpawnService.cs:68-77,156-165,239-272,518-583`;
the UI ignores the asynchronous companion result at
`Poser/UI/Panes/CompanionSection.cs:131-144`. Brio copies companion/mount/ornament
state when cloning (`Brio/Brio/Game/Actor/ActorSpawnService.cs:108-164`) and
removes created indices from a character-destroy hook
(`Brio/Brio/Game/Core/ObjectMonitorService.cs:62-73`). Ktisis likewise handles
initialize/terminate/destruct lifecycle events
(`Ktisis/Scene/Modules/Actors/ActorModule.cs:311-350`).

**Classification:** incomplete clone/UX plus a potentially destructive lifecycle
bug. **Confidence:** high statically; index reuse requires live validation.

### 5. Report the final result of asynchronous edits

**What the user hits:** importing a pose can return success before the real
apply pass begins four ticks later. If the actor becomes stale, IK/gesture state
blocks the pass, or rollback fails, the UI may remain blank or optimistic while
the failure exists only in logs. Undo/redo similarly discards its result, and
multi-target rollback failures are ignored.

**Why improve it:** a user must be able to distinguish “scheduled,” “applied,”
“rolled back,” and “partially failed.” This is more important than another
import toggle because it protects trust in every file, stash, library, mirror,
reset, and history workflow.

**Evidence:** Poser schedules and immediately returns from import
(`Poser.Game/Posing/CleanPoseFacade.cs:324-446`); late completion/rollback is in
`Poser.Game/Posing/PoseImportCapture.cs:970-1061`, while the file UI consumes
only the immediate result (`Poser/UI/Panes/PoseFileInspectorSection.cs:1699-1777`).
Other rollback results are discarded in
`Poser.Application/Posing/PoseEditService.cs:329-370` and
`Poser.Application/Transforms/TransformCommandService.cs:72-187`. Brio at least
uses visible notifications for invalid pose and clipboard failures
(`Brio/Brio/Capabilities/Posing/PosingCapability.cs:129-183`,
`Brio/Brio/UI/Controls/Stateless/FileUIHelpers.cs:574-607`).

**Classification:** incomplete UI/runtime contract. **Confidence:** high.

### 6. Expand IK beyond four hand/foot endpoints

**What the user hits:** selecting a tail, finger, spine, face, genital,
auxiliary, or custom endpoint provides no IK controls. The user must pose every
joint manually.

**Why improve it:** arbitrary or schema-defined CCD chains are a substantial
posing capability, especially for tails and custom skeletons. Poser can retain
its safe fixed limb presets while adding reusable chain definitions.

**Evidence:** Poser defines only four chains and rejects unsupported endpoints
(`Poser.Domain/Posing/IkConfiguration.cs:100-158`,
`Poser.Game/LegacyRuntime/BonePosingService.cs:1072-1138`). Brio exposes per-bone
CCD/Two Joint IK (`Brio/Brio/UI/Controls/Editors/BoneIKEditor.cs:42-95`). Ktisis
loads arbitrary configured start/end groups, including tail chains
(`Ktisis/Data/Schema/Categories.xml:560-612`,
`Ktisis/Editor/Posing/Ik/IkController.cs:138-198`).

**Classification:** clear posing capability gap. **Confidence:** high.

## Important incomplete workflows

### 7. Make camera targets real relationships

**What the user hits:** Camera → Follow actor records a one-time target offset;
moving the actor afterward need not move the camera target. Saving a camera also
omits target relationship, target offset/name, lock state, portrait state, and
bone tracking. A restored camera can have the same lens values but not the same
shot intent.

**Evidence:** Poser stores one offset/name in
`Poser.Game/Cameras/VirtualCameraService.cs:301-313` and its update consumes the
stored offsets (`:441-443`). The file mapper serializes numeric/projection fields
only (`PosingCore/Files/CameraFileService.cs:73-112`). Ktisis performs per-frame
target tracking (`Ktisis/Editor/Camera/CameraModule.cs:247-301`) and restores
actor-linked orbit targets (`Ktisis/Services/Data/SceneDataService.cs:161,416`).
Brio serializes target offset and selected actor name
(`Brio/Brio/Game/Camera/VirtualCamera.cs:46`).

**Classification:** misleading/incomplete follow contract; stable target identity
ultimately belongs in a scene format. **Confidence:** high source-level.

### 8. Give Model ID the same ownership safety as other Poser fields

**What the user hits:** Model ID is a raw number plus Apply. Poser does not
capture the incoming value, provide Reset Model ID, or offer an NPC/model search.
After changing it, the user must remember the old value. Pose exports also omit
the Model ID hint that Brio Smart Import understands.

**Evidence:** Poser’s UI and native setter are
`Poser/UI/Panes/AppearancePane.cs:174-201` and
`Poser.Game/LegacyRuntime/ActorSpawnService.cs:439-472`; Reset Appearance excludes
Model ID (`AppearancePane.cs:263-266`), and `PosingCore/Files/PoseFile.cs:15-38`
has no field. Brio preserves Model ID in pose metadata
(`Brio/Brio/Files/PoseFile.cs:136-148`) and exposes NPC selection/reset behavior
(`Brio/Brio/UI/Widgets/Actor/ActorAppearanceWidget.cs:52-110`). Ktisis places an
NPC search beside Model ID (`Ktisis/Interface/Windows/Editors/ActorWindow.cs:108-165`).

**Classification:** incomplete session-owned native capability, not an argument
for duplicating Glamourer. **Confidence:** high.

### 9. Fix MCDF teardown ordering before expanding MCDF

**What the user hits:** Reset MCDF, GPose exit, or cancellation can request a
redraw and then release the temporary collection/extracted files without waiting
for that redraw to finish. The failure is timing-dependent and may surface as
missing resources or incomplete cleanup.

**Evidence:** Poser uses fire-and-forget redraw during teardown and rollback
(`Poser.Application/Integration/ActorIntegrationSession.cs:552-568,1218-1233`)
even though `RedrawAndWait` exists
(`Poser.Game/Integration/IntegrationRuntimePort.cs:413`). Brio waits before
collection/file cleanup (`Brio/Brio/Services/MCDF/Game/Services/MCDFService.cs:198,316-318`),
as does Ktisis (`Ktisis/Data/Mcdf/McdfManager.cs:91-99,243`).

**Classification:** incomplete teardown ordering. **Confidence:** high in source;
visual manifestation needs a live stress test.

### 10. Tighten animation control semantics

Four concrete edges survived validation:

- Clearing speed always writes native speed `1`, even when Poser did not own the
  incoming pause/slow state (`Poser.Game/Animation/AnimationRuntimePort.cs:730-742`).
- Replay restarts a timeline without clearing Poser’s zero-speed override, so it
  can appear to do nothing (`Poser/UI/Panes/AnimationPane.cs:284-299`,
  `Poser.Application/Animation/AnimationSession.cs:317-321`).
- The scene-wide Physics switch reflects whether the selected actor owns a
  reference, not whether physics is globally frozen
  (`Poser/UI/Windows/MainWindow.cs:1036-1042`,
  `Poser.Application/Animation/AnimationSession.cs:458-492`).
- Lips has a picker but no ordinary speed/pause controls even though the runtime
  supports generic slot speed (`Poser/UI/Panes/AnimationPane.cs:704-714`,
  `Poser.Game/Animation/AnimationRuntimePort.cs:826-853`).

Brio clears only owned overrides and exposes Lips per-slot speed/pause
(`Brio/Brio/Capabilities/Actor/ActionTimelineCapability.cs:57-60`,
`Brio/Brio/UI/Controls/Editors/ActionTimelineEditor.cs:468-517`). Ktisis exposes
speed for every timeline slot (`Ktisis/Interface/Components/Chara/AnimationEditorTab.cs:267-307`).

**Classification:** correctness and UI-state gaps in playback control; no timeline
authoring is implied. **Confidence:** high statically; replay visibility should be tested.

### 11. Expose the selective import and native reference-pose paths already present

**What the user hits:** the file UI can filter categories but cannot apply a pose
only to the selected wrist, hand, head, or descendant subtree. Likewise, Poser
implements exact native reference-pose restoration but exposes no visible full
or partial reference action; users fall back to A/T presets or broad reset.

**Evidence:** the normal Poser file import does not pass the selected-bone
overload (`Poser/UI/Panes/PoseFileInspectorSection.cs:1721`,
`Poser.Game/Posing/CleanPoseFacade.cs:193`). Native reference application exists
at `CleanPoseFacade.cs:286` without a current inspector caller. Ktisis exposes
selected bones, descendants, anchors, and full/partial reference restore
(`Ktisis/Interface/Windows/Import/PoseImportDialog.cs:102-203`,
`Ktisis/Interface/Editor/Properties/PosePropertyList.cs:90`).

**Classification:** incomplete user paths over existing primitives. **Confidence:** high.

### 12. Make recovery health and bad files visible

**What the user hits:** autosave can fail after the UI-visible timestamp/capture
count advances, but Settings and the Auto-saves tab show no last-success/error
state. Corrupt or future `.pose` files remain ordinary library tiles and fail
only on Apply with a generic status.

**Evidence:** Poser dispatches asynchronous writes and logs failures
(`PosingCore/Files/AutoSaveService.cs:238-355`); unreadable autosave roots become
empty results (`Poser/UI/Panes/PoseLibraryPane.cs:775-820`). Corrupt library
files deliberately remain listed (`PosingCore/Library/PoseLibraryService.cs:396-401`),
and Poser has no explicit pose version gate (`PosingCore/Files/PoseFile.cs:15-38`).
Brio presents recovery/corruption errors (`Brio/Brio/UI/Windows/AutoSaveWindow.cs:89-236`)
and both references carry explicit pose/scene version markers
(`Brio/Brio/Files/PoseFile.cs:140`, `Ktisis/Data/Files/PoseFile.cs:9`).

**Classification:** incomplete observability and validation. **Confidence:** high.

## Lower-cost usability improvements

### 13. Add grouping, search, and metadata authoring for larger libraries/scenes

Poser lists props, lights, and cameras flat (`Poser/UI/Windows/MainWindow.cs:1236-1281`),
while Brio supports folders/reparenting and Ktisis exposes a recursive scene tree.
Pose search matches filenames only even though author/tags are already read and
displayed (`Poser/UI/Panes/PoseLibraryPane.cs:1097`,
`PosingCore/Library/PoseLibraryService.cs:293`). Poser can display embedded
metadata/thumbnails but cannot author or edit them; Brio’s metadata modal can
edit author, version, description, tags, and preview
(`Brio/Brio/UI/Modals/MetadataModal.cs:103-184`).

**User consequence:** a serious library or lighting setup becomes hard to find,
organize, and share long before runtime capability is exhausted.

### 14. Reduce repetitive posing trips back to the sidebar

Poser’s configurable actions are Undo, Redo, four gizmo modes, and Hide UI
(`Poser/UI/PoserKeybinds.cs:13-22`). It lacks reachable Select All, sibling/mirror
selection, overlay toggle, flip/reset, and similar high-frequency actions that
Brio and Ktisis expose. Poser also declares `SkeletonViewMode` and
`ShowSelectedBonesOnly`, and the overlay consumes them, but the current UI has
no writer (`PosingCore/Services/IEditorState.cs:43-90`,
`Poser/UI/Windows/SkeletonOverlayWindow.cs:419-431`).

**User consequence:** existing operations are available, but long posing sessions
require repeated context-menu and sidebar navigation; declared overlay modes are
effectively dead UI state.

### 15. Offer an explicit “mirror evaluated pose” operation

Current Flip/Mirror intentionally transforms Poser-authored layers only. If an
animation or imported evaluated pose is visible but a bone has no authored layer,
the operation does nothing (`Poser.Application/Posing/PoseEditService.cs:94-203`).
Brio and Ktisis can capture the evaluated skeleton before flipping/mirroring
(`Brio/Brio/Capabilities/Posing/PosingCapability.cs:431-487`,
`Ktisis/Editor/Posing/Data/EntityPoseConverter.cs:244-307`).

Keep the current animation-safe operation. Add a separately named capture/bake
action only if users want the visible pose mirrored, so the destructive semantic
change is explicit.

## Areas that should not be reimplemented

- General equipment/customization/design editing belongs to Glamourer. Poser’s
  actor-scoped Penumbra/Glamourer/Customize+ selectors and capture/restore model
  are already strong.
- Opacity, Character/MainHand/OffHand tint, granular wetness, Model ID, and MCDF
  handoff are appropriate Poser-owned exceptions.
- Animation playback, stance, scrubbing, expression layers, gaze, and physics
  control are present. The recommendations above fix specific playback semantics;
  they do not add timeline authoring.
- Skeleton discovery, partial reparenting, stable identities, local/world
  transforms, pivots, symmetry, linked editing, reset, import/export, stash,
  clipboard, pose library, cameras, lights, gobos, and environment controls are
  real current features. They should not be listed as blanket omissions.

## Runtime validation still required

The source findings above are sufficient for prioritization, but these claims
need an actual in-game or fault-injection run before being filed as confirmed
runtime defects:

1. Destroy a Poser-spawned actor through another tool, reuse its object-table
   slot, and verify that Poser never offers Despawn for the replacement.
2. Attach minion, mount, and ornament variants; verify discovery, clone fidelity,
   child-side detach, and timeout feedback.
3. Stress Reset/Cancel MCDF while redraw and extraction are active; observe file
   lifetime and cleanup.
4. Force a late pose-import/apply and rollback failure; verify the final UI status
   and history state.
5. Test Clear Speed and Replay against a pause owned by the game or another tool;
   verify native playback after the next animation update.
6. Move an actor after selecting Camera → Follow actor, and clone/export an
   attached light; verify relationship behavior.
7. Make the autosave directory unwritable and place corrupt/future pose files in
   the library; record what the user sees.

The audit threads requested a live-runtime follow-up for these items. Another
source-only thread would not resolve them; they require the game or the live
harness.

## Documentation drift

`docs/architecture/product-and-boundaries.md:21-27` still describes cameras,
lights, libraries, and autosave as deferred. Current composition registers and
exposes those features. Projects remain deferred. This is a documentation update,
not a feature request.
