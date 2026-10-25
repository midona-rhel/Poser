# Scene-entry and scene-pane audit — 2026-10-25

Scope: complete ScenePane source/comments; LibrarySceneActions, PlacementAnchorSource, SceneLoadPreferences and RememberedFolder; camera/light load callbacks; Library and spawn-browser entry dispatch. Traced underlying scene admission and metadata reader contracts. This is not a completed audit of the whole scene transaction, atomic writer, camera service or Library UI.

## Removed

- ScenePane's second full-scene load setup. LibrarySceneActions.LoadScene already uses the same preferences, territory and placement source. The pane now delegates and retains only its notice.
- TryVerdict's catch-all and cached null-result state. SceneFileStore.Read catches file/ZIP/JSON errors and returns failures; ReadMetadata maps those to non-null metadata results. Successful reads validate required entity lists before metadata counts them. No supported additional failure was found for the outer catch to handle. Runtime/programming faults are not a separate user recovery feature.
- Misleading and repetitive comments throughout ScenePane: claimed deferred opens, a single save option despite two controls, three summaries attached to one callback, guaranteed phase coverage despite a default case, and promises about manually recovering retained files. Rewrote 31 XML comments and surrounding inline comments.
- Incorrect spawn-browser claim that entries spawn at the player. It uses the configured placement mode.

## Retained checks and evidence

| Check | Reachable reason and outcome |
|---|---|
| No actor anchor | A scene can contain only lights/cameras. ActorAnchorNow finds no actor and returns null. File imports explicitly use saved coordinates; Library entry imports return a notice. No fast-click scenario is needed. |
| Actor transform unavailable | ViewportProjection resolves the selected actor's current model transform. An actor can be present before its draw model is ready. Keep this native availability boundary; do not invent a transform. |
| No camera direction | CameraService.GetLookDirection returns zero when projection cannot produce a direction and the native camera fallback is absent/unusable. Placement cannot calculate a heading from that value. Retained; not live-reproduced in this pass. |
| No actor creation handle | SceneCreation refuses outside GPose and Track returns no handle when native spawning returns no actor. SpawnPose must not queue a pose against a nonexistent actor. |
| Overlay/environment category branches | These entries have no camera/actor placement anchor. They apply only their category; do not relocate scene entities. |
| Scene busy / active session / categories | SceneWorkflow is the common admission owner. Ordinary scene loads can take seconds; an open Library or file dialog can submit another load during that time. All categories can also be unchecked. Retained; no submillisecond-click justification. |
| Preview in-flight set | DrawVerdictPanel and DrawSessionOptions ask for metadata on successive frames. Removing the set starts repeated reads while the first read runs. |
| Preview cache size and file stamp | Browsing many files grows the cache; replacing a file while its dialog remains open changes its preview. Retained the existing bounded cache and modification-time/length check. This is cache invalidation, not file recovery. |
| File stat and directory creation catches | The dialog allows filesystem locations the process cannot read or create. Return the existing read-error state or Documents fallback. No retry store. |
| Notification operation ID | DrawBrowsers runs every frame and the finished result remains available. Without the ID check, it repeatedly sends the same notification. |
| Library index bounds | Selection is cleared to -1 by filtering/deselection; actions use that selection. Kept bounds checks. No claim that every bound in all Library code has been justified. |
| Pending/invalid preview branches | A file read is asynchronous; corrupt/missing files return an explicit failed metadata result. These are supported UI states, not invented recovery paths. |
| Error notices | BeginLoad can refuse a real busy/no-session request. Keep the notice so the click has an explanation. |

## Limits and follow-up

The retained-files display receives actual paths from AtomicFile's failed-write/cleanup results; it does not repair files or resume work after restart. Removed the comment promising manual recovery. AtomicFile's commit checks and retention policy need their own existing storage audit; this pass does not establish that all of them are necessary.

Layout size checks remain unchanged; their necessity was not established by this import audit. The existing Library pose reactivation timer is outside saved-entry dispatch and was not changed or declared justified.

Validation: Release build passed, 906 existing tests passed, zero added/removed test cases. Four production files: +110/-202 lines including comments. Runtime changes are limited to shared full-scene load dispatch and removal of the redundant metadata failure representation. Prior entry-dialog live checks remain recorded separately; this follow-up still needs deployment/metadata UI verification.
