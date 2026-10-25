# Simplification plan — 2026-10-25

Reviewed head: `0d659222202a77c8ea359eb7c607edc3c837f1ca`. Three agents reviewed guards, duplicate behavior and code documentation; the main task checked the strongest findings against their callers. This was source review, not a new live test pass or a complete audit of every file. Tracking: [#610](https://github.com/midona-rhel/Poser/issues/610). Current contracts remain in [the documentation index](../README.md).

## Decisions and implementation order

| Order | Work | Evidence and decision | Verification |
|---|---|---|---|
| 1 | [#631 Preserve actor scale and truthful results](https://github.com/midona-rhel/Poser/issues/631) | `PosingService.cs:221–275` clamps actor scale to 0.01–100 after the domain accepts signed/smaller values; `TransformRuntimePort.cs:53–80` reports success after a void setter. Remove the contradictory clamp and return an actual result. Review global scale limits separately from local inverse math. | Known small-scale appearance fixture, negative actor scale, readback, undo/redo and one unavailable actor. Do not substitute a huge synthetic extremes matrix for normal use. |
| 2 | [#632 Preserve geometry in transform conversion](https://github.com/midona-rhel/Poser/issues/632) | `Transform.cs:74–101` extracts unsigned scale and replaces zero with 0.01. Trace bone-world, collider and IK callers; stop inventing geometry. Prefer authored components where available. | Matrix equivalence for reflection and collapsed axes, then affected normal posing paths. Matrix decomposition cannot uniquely recover authored signs or represent arbitrary shear; define local failure behavior. |
| Alongside 1–2 | [#633 Plain, accurate code documentation](https://github.com/midona-rhel/Poser/issues/633) | Correct misleading summaries before stylistic rewrites: `SceneLoadTransaction.cs:8–35`, `ModelIdRuntimePort.cs:10–18`, `FrameProfiler.cs:160–162`, and misattached autosave XML comments. | Check claims against actual conditions and callers. Explain what success proves and what it does not. No game test is needed for comment-only edits. |
| 3 | [#634 One import-options policy](https://github.com/midona-rhel/Poser/issues/634) | `PoseFileInspectorSection.cs` independently builds Transform, Type and Scope choices for menu/dialog layouts. Share rules and item definitions; retain layout-specific mounting. | Same options and import behavior for ordinary pose, CMP, Smart/Expression and selected-bone cases in both surfaces. |
| 4 | [#635 Remove inactive pose-autosave writer wiring](https://github.com/midona-rhel/Poser/issues/635) | Production ticks scenes only, but registration still builds pose capture/writer/status services. Keep legacy files and readers; remove inactive write dependencies after checking recovery/debug consumers. | Scene cadence/retention/status and legacy browsing. Preserve meaningful old recovery records rather than discarding them. |
| 5 | [#636 One entry-placement policy](https://github.com/midona-rhel/Poser/issues/636) | `ScenePane.LoadEntry` duplicates `LibrarySceneActions.SpawnEntry` option/anchor decisions. Route pane callers through the existing application owner with explicit fallback. | Camera/light entry from pane and Library, ordinary anchor and one missing-anchor case; always additive. |

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
