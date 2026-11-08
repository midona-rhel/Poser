# UI and host simplification plan

Evidence was read at `04b09fd7272de82035c207e51e13a428595612bd`. These are behavior changes to consider after the comment-only audit; none is implemented here. The issue numbers refer to the repository's existing 2026-10-25 simplification plan.

## #634 — One import-options policy, two layouts

`PoseFileInspectorSection` currently implements the same choices twice: `DrawImportApplyCard` and `DrawTransformSection`, `DrawImportTypeCard` and `DrawImportTypeSection`, and `BuildImportScopeItems` and `DrawScopeSection`. The dialog and menu need different layout, but they do not need separate eligibility rules, labels, or state transitions.

Make one typed import-options model in the existing pose-import UI owner. Have it supply the available apply modes, import types, and selective-scope items. Keep the dialog card and compact menu as thin renderers of that model, then delete their duplicate predicates and copy. Do not introduce a general form framework.

Risk: ImGui IDs and the compact menu's close behavior can change even when labels match. User verification: import an ordinary pose, a CMP pose, a Smart/Expression pose, and a selected-bones pose from both entry points; confirm identical enabled choices and resulting transforms. Evidence confidence: high; the duplicated branches and callers are in the same class.

## #635 — Remove the inactive legacy pose-autosave path

`ServiceRegistration` still constructs `IPoseAutoSaveCapture`, `AutoSaveService`, and `IAutoSaveService`, while `AutoSaveRuntime` now ticks scene autosaves. The old service remains indirectly visible through library-root and health/status consumers, so stale legacy health can be displayed beside a healthy scene-autosave system.

Delete the inactive writer, capture, health, and registration path rather than improving it. Retain only the legacy file/store reader needed to browse existing files. Move any required legacy root resolution to the read-only library/configuration owner, remove stale footer/settings health dependencies, and adjust final-capture/debug-bridge callers that still name the old service. Preserve configuration migration where old paths are still input data.

Risk: deleting registration before migrating every root/status caller can break startup or hide existing backups. User verification: confirm scene snapshots follow their cadence and retention settings, status reflects the scene writer, clean GPose exit behaves as configured, and existing legacy pose backups remain browsable. Evidence confidence: high for inactive writing; medium for the exact compatibility surface until every non-UI caller is included in the implementation change.

## #636 — Use the existing scene-entry spawn owner

`ScenePane.LoadEntry` rebuilds spawn options and anchor fallback before forwarding camera and light entries. `LibrarySceneActions.SpawnEntry` already owns entry-category dispatch and explicit fallback, and the library and spawn browser use it.

Inject/use `ILibrarySceneActions` for the pane actions, route camera and light entry activation through `SpawnEntry`, and delete `ScenePane.LoadEntry` plus its local option construction. If a notice must remain pane-specific, keep only that presentation adapter.

Risk: pane actions may currently rely on a subtly different notification or selection sequence. User verification: spawn a camera and a light from both the pane and library with a valid anchor and with the anchor unavailable; every action must remain additive and select/notify consistently. Evidence confidence: high for duplicated option construction; medium for notification equivalence until exercised live.

## Follow-up — import path ownership

`PoseFileInspectorSection.ImportFromPath` loads files for Smart handling and CMP overrides, maps UI options, and then calls `NativePoseImportService`, which resolves and plans the path again. This may be necessary because the service also exposes an in-memory import boundary; it is not yet proven waste.

After #634, trace all service callers and, only if path loading is truly repeated for the same request, move the typed path request to the existing application import owner and delete UI-side orchestration. Preserve the inspector's visible Smart state and CMP override behavior. Validation must compare the same four import families above plus error reporting for missing and malformed files. Evidence confidence: medium; keep as investigation, not a committed deletion.

The UI/runtime boundary itself is necessary: UI code owns layout and transient ImGui state, application services own import/spawn policy, and runtime ports own native game operations. The needless layers above are duplicated policy or inactive wiring inside those boundaries, not the boundaries themselves.

## Full-source findings beyond the initial sample

- **Fix reserved connector geometry.** SkeletonOverlayWindow.DrawLinePass reserves
  six indices/four vertices per parent-linked bone, but LineToCircle skips a close
  pair without writing or releasing that reservation. The later short-length
  branch already writes a degenerate quad. Make the skipped pair obey the same
  buffer contract, or reserve only for emitted quads. Verify sprites enabled,
  LineToCircle enabled, and a projected separation below twice the dot radius.
  The unwritten reservation is established in source; no rendering symptom has
  been reproduced in this audit.
- **Make PageForm width rules truthful and remove unused options.** The halfRows
  argument is ignored and only forwarded internally; remove it after checking
  all callers. NextFullLine advances the row but retains track width. Separate
  row advance from full-form width only where a real caller needs the latter.
  Fixed icon widths plus the 0.4 text shrink floor can exceed available width;
  do not document this as guaranteed overflow prevention. Check ordinary form
  layouts, without inventing a new resize acceptance matrix.
- **Preserve layout and centralize eligibility.** SidebarComposer's root and group
  dispatch paths apply search differently. Trace the intended group-search result
  before extracting shared eligibility; keep drag/reorder behavior local to its
  surface. A common renderer is not useful if policy still diverges elsewhere.
- **Trace settings reset wiring.** SettingsWindow resets its view model through
  LoadFromConfig without the WireRuntime call made on opening. Check resetting
  settings then recording a shortcut before assigning a defect. If disconnected,
  route both replacement paths through the existing model initialization method.
- **Keep low-exposure parser observations in proportion.** SvgPathParser can fail
  to advance after malformed commands, but its discovered callers use bundled
  icons. Fix cursor progress if modifying that parser; this does not establish an
  external-file vulnerability or justify a new user import validation system.

FileDialog, SidebarComposer and PageForm full reads are recorded in root-extra.csv;
those records replace their pending entries in the UI agent ledger. Other UI
investigation notes are in ui-host.csv and are not established runtime defects.
