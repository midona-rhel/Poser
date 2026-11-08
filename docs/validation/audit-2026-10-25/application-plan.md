# Application comment audit follow-up

Scope: `Poser.Application` and `Poser.Application.Tests` at
`04b09fd7272de82035c207e51e13a428595612bd`. Full-read records are in
`application-production.csv`, `application-controls.csv`, `application-posing.csv`
and `application-tests.csv`. The unsupported earlier sample ledger was removed. No files in these projects identify
themselves as generated, so no generated-source exemption was used.

## Rewrite standard

Keep comments that state a constraint the code enforces, especially ordering,
identity, rollback, file lifetime, and thread requirements. State the checked
condition and the limit of the result. Remove control-flow narration, design
history, test convenience as product rationale, metaphors, emphatic capitals,
and claims stronger than the observed check. Brio or Ktisis references belong
only where compatibility explains a deliberate behavior.

## Behavior follow-up sequence

1. **Confirm the intended scene-load policy before changing it.** The old policy header in
   `Poser.Application/Scene/SceneLoadTransaction.cs` claimed every missing actor
   invalidated the scene. Current code instead omits invalid actor records,
   rolls back when a valid actor cannot spawn, and keeps actors that spawn but
   do not become ready. These cases can legitimately have different outcomes. Document the current
   rule plainly and establish whether any outcome violates the intended user
   behavior before replacing it. If partial actor
   loads are accepted, replace the actor-spawn `Stop.Abort` path with a named
   `SceneEntityOutcome` and continue. If actors are required, move per-actor
   validation/readiness into preflight or roll back on every missing actor.
   Delete the superseded branch and policy wording in the same change.
   Validate corrupt actor data, spawn refusal after earlier entities, readiness
   timeout, cancellation, clear-first load, rollback failure, and undo/redo.
   **Certainty:** high about the old comment being wrong; policy unification
   is conditional, not a confirmed defect.

2. **Remove the legacy pose-autosave path through #635.** Do not improve the
   ambiguous `SnapshotNow` integer in `Poser.Application/AutoSave/AutoSaveService.cs`;
   it belongs to the pose-autosave service being replaced by whole-scene
   autosaves. Delete `AutoSaveService`, `AutoSaveWriter`, `AutoSaveHealthLedger`,
   `IAutoSaveService`, and their pose-autosave composition and tests once #635
   has moved any required exit/recovery behavior to `SceneAutoSaveService`.
   Validate that only the scene autosave is scheduled, final GPose exit behavior
   remains intentional, retention and recovery still work, and no inactive pose-writer/status
   wiring remains. Preserve read-only legacy backup browsing and its root lookup. **Certainty:** high about planned
   removal; exact retained behavior is owned by #635.

3. **Decide whether skipped scene autosaves retry.** `SceneAutoSaveService`
   skips a tick when a write, scene operation, or shared pose refresh is busy
   and schedules the next normal interval. If prompt recovery is desired, add
   one coalesced retry flag that runs after the blocker clears; do not add an
   unbounded queue. Delete the current skip-only scheduling path after callers
   consume the new status. Validate repeated busy ticks, exit/re-entry, dispose,
   and a slow writer. **Certainty:** high about current behavior; product choice
   required.

4. **Do not expand the obsolete autosave health API.** Its writer is scheduled
   for removal in #635. Trace existing recovery records and preserve any useful
   reader/migration behavior there. Delete dead status dependencies rather than
   adding retry controls to a service no longer scheduled in production.

5. **Separate MCDF cancellation from completion claims.** Disposal in
   `Poser.Application/Integration/McdfTransaction.cs` waits two seconds, while
   comments previously implied that guards made every abandoned completion
   harmless. Audit every external provider call after cancellation. Where a
   call cannot be fenced by the operation/session check, retain its task and
   resources until completion or add provider-specific cancellation. Delete the
   blanket safety claim. Verify the actual input-file owner and one unfinished provider call before
   designing a targeted completion test. Do not invent every interruption as a
   separate requirement. **Certainty:**
   medium; the bounded wait is confirmed, provider cancellation guarantees are
   not.

6. **Make retained MCDF files observable.** `McdfTeardown` deliberately keeps
   extracted directories when redraw cleanup fails. Ensure the existing Reset
   MCDF action and diagnostics name those directories and retry state; if they
   do not, expose the retained cleanup result through the existing transaction
   receipt. Remove any log-only duplicate reporting. Validate actor present,
   actor gone, redraw timeout, delete failure, repeated reset, and history
   eviction. **Certainty:** high about retention, medium about current UI reach.

7. **Verify history cleanup subscribers before changing history events.**
   `EditHistory.HistoryCleared` releases state stored outside the undo stacks.
   Inventory subscribers and give each external state owner an idempotent clear
   path before altering capacity-zero or clear timing. Delete owner-specific
   duplicate sweeps only after equivalence tests. Validate explicit clear,
   capacity zero, eviction, lifecycle undo/redo, and observer exceptions.
   **Certainty:** high.

8. **Document transform serialization at the boundary.**
   `TransformGestureService` rejects synchronous callback reentry but is not
   thread-safe. Confirm all public callers enter on the framework thread. If any
   do not, marshal them through the existing framework dispatcher rather than
   widening the boolean guard into an implicit lock. Delete off-thread direct
   calls and validate callback reentry, cancellation, rollback, and concurrent
   diagnostic reads. **Certainty:** high about the guard's limit; caller audit
   remains.

Track these as bounded follow-ups under issue #610. Update the existing
normative architecture or feature home only when implementation changes a
durable contract; do not create class-by-class documents.

## Additional full-source findings and concrete changes

- **Report playback failures at the existing owner.** `SpeedAnimationSession.Resume`
  ignores each `ResumeSlotSpeedCore` result and returns only `ClearSpeed`'s result.
  `AnimationSession.ResumeUntracked` likewise ignores `RestartIfEnded` failures.
  `AnimationSteps.Record` records history only for a successful overall result.
  Collect the failed layer results at these two composition points and return a
  truthful partial-change result. Keep successful layer changes, and make history
  capture that partial change instead of silently losing it. First reproduce a
  refused layer write with the existing fake ports; confirm the production port's
  refusal conditions before claiming a normal Play regression. Do not redesign
  pause/resume semantics or add more animation state machines.

- **Choose one owner for external-resource cleanup.** `McdfImport.Rollback` and
  `McdfTeardown.TearDown` both unlock Glamourer, restore appearance, delete temporary
  C+ profiles/collections, and defer file deletion for redraw. Their restore targets
  differ deliberately: rollback uses the immediate pre-import appearance; Reset
  uses the original baseline unless a failed rollback is pending. Extract those
  resource-release operations into McdfTeardown, taking the restore target explicitly.
  Make rollback transfer its recorded resources to that owner and remove its second
  cleanup implementation. Preserve ordering and failed-resource IDs. Check ordinary
  import/reset, replacing a Poser C+ profile, one provider refusal followed by Reset,
  and actor removal; avoid an exhaustive invented cancellation matrix.

- **Do not infer cancellation from a timed wait.** `CancelAndDrain` can time out
  before its queued framework callback has run. `McdfWait.Terminal == false` does
  not prove cancellation was delivered, and a terminal receipt can be published
  before the worker task returns. Trace the input-file owners using `Settled` before
  choosing the completion signal. Keep receipt state for the UI and task completion
  for resource lifetime; remove comments or caller logic that treats them as the
  same thing. This is a source-level contract issue, not a reproduced late mutation.

- **Avoid speculative admission locks.** `SingleFlightOwner.Admit` does not enforce
  `Busy` or `Closed`; callers such as `McdfTransaction.AdmissionGate` perform those
  checks. Before adding a lock or a second guard, enumerate all Admit callers and
  their execution thread. Either move admission into the existing owner and delete
  caller duplicates, or document the confined caller contract. No concurrency bug
  is established merely by the helper lacking its own lock.

- **Keep absence separate from failed restoration.** `AnimationSession.Reconcile`
  calls ResetActor and retains failed restore state; the old comment incorrectly
  claimed it always discarded it. Keep the actual retry behavior until the actor
  identity resolver proves the old object is gone. Do not delete this state just
  to make the implementation match its former comment.

- **Clean temporary scene-save files at one exit point.** SceneWorkflow.RunSave
  can return for cancellation or validation after SealAppearance created files,
  bypassing DeleteTemporary. Move temporary-file ownership around the full save
  attempt with a single finally/disposal path. Keep successfully transferred
  output files; check early refusal and ordinary successful save.

- **Use one recovery path for pose-edit failures.** PoseEditService.Apply restores
  earlier writes after a returned failure, but a thrown write escapes that path.
  Its callers include Reset, Flip, Mirror, pose transfer and actor reset. Reuse
  the exception-to-outcome handling already established by SetAbsoluteMany and
  retain failed restoration state. Check a second write throwing after the first
  succeeded; do not add another general transaction framework.

- **Share target lookup without changing target eligibility.**
  TransformTargetResolver repeats exact-ID lookup loops. Extract that lookup in
  the existing resolver, while keeping the intentional collider omission in the
  appropriate caller. Compare the callers before deleting a branch.
