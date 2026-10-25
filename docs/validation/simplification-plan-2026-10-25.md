# Simplification plan — 2026-10-25

Audit base: `04b09fd7272de82035c207e51e13a428595612bd`. The initial sampled review was insufficient and is superseded by the full-source pass below. The full source/comment pass is **complete**. Runtime consolidations below are planned, not implemented by this patch. Tracking: [#610](https://github.com/midona-rhel/Poser/issues/610). Current contracts remain in [the documentation index](../README.md).

## Required scope and evidence

Read every tracked first-party source file, including tests, scripts and build configuration. Review every comment, not just headers or keyword matches. Replace unclear prose, remove redundant narration, and correct claims against the implementation and callers. Keep licenses, wire-format facts and necessary native/math explanations. A generated filename alone is not an exemption: the transcribed Ktisis category file also contains handwritten traversal code and was read in full.

Per-file records live in [audit-2026-10-25](audit-2026-10-25/). `pending` and `partial` mean unfinished. A program opening or counting a file is **not** a review. Agent reports that confused scanning with inspection were rejected and their unsupported coverage was withdrawn. Reconcile each ledger against `git ls-files` before reporting final totals; the current tracked inventory is 1,075 C# files plus 24 script/build/configuration files (1,099 total).

## Work sequence and concrete deliverables

1. **Rewrite all source comments.** Inspect full files in bounded batches. Use ordinary words and explain the actual action, reason, and limitation. Remove unsupported claims such as “safe,” “atomic,” “immutable,” and “never” where code does not establish them. Do not replace one confident fiction with another. Each area delivers its patch and a complete file ledger.
2. **Audit behavior ownership.** For actor appearance, animation, transforms/IK, scene save/load, autosaves, library import/export, UI state and native lifecycle, trace user entry points to the code that performs the change. Identify precisely which competing path is deleted, which owner remains, and which callers move. Similar names or interfaces alone do not justify consolidation.
3. **Audit guards.** For each restriction, name the input it rejects or alters, the real consumer requiring it, the visible result, and the evidence for keeping it. Separate product policy from division/inversion requirements and native memory checks. Move arithmetic requirements to the arithmetic operation; stop silently changing authored data at unrelated load/storage boundaries.
4. **Implement in separate, bounded behavior changes.** Start with contradictory transform handling and the confirmed aligned-allocation calculation, then delete the inactive autosave graph and consolidate import/entry-placement policy. Preserve old-file readers. Do not improve an API already scheduled for deletion. More speculative findings need caller evidence before a fix is designed.
5. **Verify the actual changes.** For this comment pass, compare C# tokens and directives under Release and DEBUG symbols against the base, inspect script/build diffs, and compile Release. For behavior changes, test the demonstrated user path and failure outcome, not every imaginable interruption. Native/UI changes then follow the existing deployment and acceptance contract.

Area plans: [Domain](audit-2026-10-25/domain-plan.md), [Game](audit-2026-10-25/game-plan.md), [Application](audit-2026-10-25/application-plan.md), [Documents](audit-2026-10-25/documents-plan.md), [UI/host](audit-2026-10-25/ui-host-plan.md), [tests/build](audit-2026-10-25/domain-tests-scripts-build-plan.md). The reconciled coverage ledger contains 1,099 reviewed files and no pending entries. Individual agent ledgers retain delegated rows; coverage.csv resolves those against the receiving reviewer's evidence. The five generated-data files also received comment/helper inspection; see [generated review](audit-2026-10-25/generated-review-plan.md) for the explicit limits.

## Decisions and implementation order

| Order | Work | Evidence and decision | Verification |
|---|---|---|---|
| First | [#637 Correct aligned allocation](https://github.com/midona-rhel/Poser/issues/637) | An already aligned pointer advances 16 bytes although only 15 extra bytes were allocated. Correct the shared calculation and retain paired freeing. This does not establish the cause of a historic crash. | All 16 address remainders and requested memory extent, without corrupting the game. |
| 1 | [#631 Preserve actor scale and truthful results](https://github.com/midona-rhel/Poser/issues/631) | `PosingService.cs:221–275` clamps actor scale to 0.01–100 after the domain accepts signed/smaller values; `TransformRuntimePort.cs:53–80` reports success after a void setter. Remove the contradictory clamp and return an actual result. Review global scale limits separately from local inverse math. | Known small-scale appearance fixture, negative actor scale, readback, undo/redo and one unavailable actor. Do not substitute a huge synthetic extremes matrix for normal use. |
| 2 | [#632 Preserve geometry in transform conversion](https://github.com/midona-rhel/Poser/issues/632) | `Transform.cs:74–101` extracts unsigned scale and replaces zero with 0.01. Trace bone-world, collider and IK callers; stop inventing geometry. Prefer authored components where available. | Matrix equivalence for reflection and collapsed axes, then affected normal posing paths. Matrix decomposition cannot uniquely recover authored signs or represent arbitrary shear; define local failure behavior. |
| Whole codebase | [#633 Plain, accurate code documentation](https://github.com/midona-rhel/Poser/issues/633) | Audit every comment and full source file. The named examples in the initial review were not the scope. Rewrite insider language and correct false guarantees throughout. | Complete per-file coverage, source-token comparison, Release build, and reviewer spot checks against implementation. No game test is needed for comment-only edits. |
| 3 | [#634 One import-options policy](https://github.com/midona-rhel/Poser/issues/634) | `PoseFileInspectorSection.cs` independently builds Transform, Type and Scope choices for menu/dialog layouts. Share rules and item definitions; retain layout-specific mounting. | Same options and import behavior for ordinary pose, CMP, Smart/Expression and selected-bone cases in both surfaces. |
| 4 | [#635 Remove inactive pose-autosave writer wiring](https://github.com/midona-rhel/Poser/issues/635) | Production ticks scenes only, but registration still builds pose capture/writer/status services. Keep legacy files and readers; remove inactive write dependencies after checking recovery/debug consumers. | Scene cadence/retention/status and legacy browsing. Preserve meaningful old recovery records rather than discarding them. |
| 5 | [#636 One entry-placement policy](https://github.com/midona-rhel/Poser/issues/636) | `ScenePane.LoadEntry` duplicates `LibrarySceneActions.SpawnEntry` option/anchor decisions. Route pane callers through the existing application owner with explicit fallback. | Camera/light entry from pane and Library, ordinary anchor and one missing-anchor case; always additive. |

## What each consolidation removes

| Behavior | Existing owner to retain | Change to make, then delete | Focused check |
|---|---|---|---|
| Actor transforms | PosingService through TransformRuntimePort | Return the actual setter result; delete downstream unconditional success and contradictory positive-only clamp. | Small authored scale and a refused write retain truthful history. |
| MCDF resource cleanup | McdfTeardown | Accept the restoration target explicitly; move resource cleanup from McdfImport.Rollback, then delete that duplicate. Original reset baseline and immediate rollback baseline stay distinct. | Import/reset and one refused cleanup followed by retry. |
| Pose edit recovery | PoseEditService | Route returned and thrown write failures through the same restoration path; use existing transform handling conventions. | Second write throws after first succeeded. |
| Scene-save temporaries | SceneWorkflow save attempt | Own temporary files across every post-capture exit; remove success-path-only cleanup. | Validation refusal and ordinary saved output. |
| Native spawn identity | Existing SpawnOwnership descriptor/resolver | Check the captured source before deferred collection copying, not only before the later appearance copy. | Source removed between duplicate request and callback. |
| Camera target/history | CameraTargetControl and CameraControl | Propagate restore/switch outcomes; remove successful history after refused native work. | Missing target during replay and ordinary switching. |
| Scene autosave | SceneAutoSaveService | Remove inactive pose writer wiring and dead status dependencies; retain old backup readers. | Scene snapshot plus legacy browsing. |
| Import choices | Existing pose-import UI owner | Define eligible modes/types/scope once; delete card/menu duplicate predicates. | Same options in both surfaces. |
| Scene entry placement | LibrarySceneActions | Route pane entry spawning through it; delete ScenePane.LoadEntry option construction. | Camera/light entries from both entry points. |
| Gaze history/math | GazeSession and current native driver | Decide target history scope, then remove settings-only restore for target/reset; share the repeated forward-point calculation. | Retarget then undo, reset then undo. |

Detailed caller evidence and additional native follow-ups: [scene/world](audit-2026-10-25/game-extra-plan.md), [spawn](audit-2026-10-25/game-spawn-plan.md), [gaze](audit-2026-10-25/gaze-plan.md). These are proposed implementation slices, not fixes secretly included in this comment patch. UI layout, application policy and native writes remain separate responsibilities; reducing their number of classes is not itself a goal.

## Follow-up investigation, not established defects

- Path imports decode and route files in `PoseFileInspectorSection.ImportFromPath` and again in the import service. After #634, trace preview, clipboard, stash and reapply before moving routing into the existing application owner. Preserve visible Smart-import state; avoid a new generic request framework.
- `PoseFileValidation` rejects a pose containing one nonfinite bone. Consider importing valid bones with a named omission, but first decide whether partial import is the desired behavior. Never send nonfinite data to native code.
- `TransformGestureService` blocks edits while rollback remains incomplete. Reproduce whether a permanently removed target blocks an unrelated actor before narrowing this rule. Keep recovery for changes already made; do not discard failed rollback evidence to unblock the UI.
- Scene loading omits invalid actor records, keeps actors that are not ready, but rolls back on actor creation failure. Correct the inaccurate explanation now; assess whether this distinction helps users before changing the policy.

## Guard and documentation rules for these changes

- Keep native object lifetime, pointer, index and layout checks. Explain the concrete access they protect.
- Keep finite-number checks before native writes. Require nonzero/invertible values where division or inversion happens, not automatically at every storage or load boundary.
- A clamp must implement an intentional user-facing range or a demonstrated native requirement. Do not silently alter authored data because it looks unusual.
- Test the user behavior and resulting data. A test that merely proves a restriction rejects a value does not justify that restriction.
- Write comments for a reader unfamiliar with the subsystem: purpose, reason and limitation. Define necessary terms; remove ALL CAPS, metaphors, unsupported guarantees and narration of obvious statements.
- Example: “Check that the actor still exists before reading its game memory,” instead of “the stored address is only a claim.” Immediate model-ID readback proves the field changed, not that redraw finished.

## Completion criteria

Each issue identifies the old paths removed, behavior preserved, targeted checks and remaining uncertainty. Measure production additions/deletions separately from tests and documentation; do not promise a LOC quota. A new helper is not completion while duplicate policy remains. Native/UI changes follow the existing Release/deployment/user-acceptance process; this plan does not modify that process. No runtime code was changed or deployed by this audit.

## Completed pass and validation

- Inventory: all 1,075 tracked C# files plus 24 tracked scripts/build/configuration files. Every source comment was inspected; clear necessary comments and licenses were retained. The generated native/SVG tables were classified as data, not presented as individually verified offsets or artwork.
- Patch: 504 C# files changed, 3,910 lines added and 7,541 removed (net 3,631 fewer). These are documentation/whitespace changes, not production logic reduction. Script/build changes likewise affect comments or Python module help only.
- Release compilation passed with 16 warnings and zero errors. Existing tests: Application 286, Domain 367, Game 258; 911 passed, zero failed or skipped. The first sandbox build could not read the Windows SDK registry; validation succeeded with registry access.
- Final C# comparison checked all 504 changed files: zero token or directive differences in either normal or DEBUG parsing. Debug was not built or deployed. No native/game behavior was exercised by this audit.
- Existing runtime issues remain open. Findings such as allocation extent and incorrect success propagation are source-supported; suspected timing, lifecycle or visual effects still require the focused checks in the area plans. Do not treat source review as proof of the cause of past crashes.
- External generators for native metadata are absent from this checkout. Corrected output comments must be carried into those templates before regeneration; color/icon source provenance is recorded in the generated review.
