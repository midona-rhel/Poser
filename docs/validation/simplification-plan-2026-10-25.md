# Incremental simplification plan — 2026-10-25

Tracking: [#610](https://github.com/midona-rhel/Poser/issues/610). This is the implementation sequence, not a claim that the refactor is finished. Existing contracts remain in [docs/README.md](../README.md); update those homes when a change is implemented.

## Goal and limits

Make each behavior easier to understand, give its state and resources a clear owner, and remove competing implementations. Deliver small changes that can be reviewed, tested, deployed and reverted separately. Do not replace the application architecture in one release.

An actor should have an understandable account of the state Poser applies to it. That does not require moving every service into a giant Actor class. Keep specialist services for animation, posing and integrations. Bring ownership together one behavior at a time, without duplicating their state in a second master object.

The source audit covered 1,075 C# files and 24 scripts/build/configuration files at `04b09fd7272de82035c207e51e13a428595612bd`. [Coverage](audit-2026-10-25/coverage.csv) records inspection, not proof that every comment is clear or every guard is justified. Follow-up reads found remaining jargon. Native metadata and SVG tables were classified as data; their comments and executable helpers were inspected, not every offset or rendered icon independently verified.

## Rules for every change

- One behavior per change. Name the existing owner, the callers to migrate, and the old code to delete before starting.
- Reuse an existing service or store first. Introduce a new type only when it replaces a concrete responsibility; do not add a generic manager, command bus, cleanup framework or second history system.
- Keep one authoritative copy of each piece of state. A temporary forwarding method may help migrate callers, but it must not maintain a second implementation and must be removed in the same completed step.
- Keep file formats, undo behavior and UI behavior unless the issue explicitly changes them. A proposed product change is not a refactoring prerequisite.
- Rewrite comments in touched code as ordinary sentences explaining what happens and why. Remove narration, unexplained shorthand, historical arguments and unsupported guarantees.
- A guard needs a reachable production caller, an actual scheduling boundary, and a meaningful consequence. A test inventing two instantaneous clicks is not enough. Loads waiting for files, redraws or another frame can overlap at ordinary speed; trace that path before deciding.
- Put a justified check at the owner of the operation. Remove repeated checks only after confirming they protect the same interval. Keep native lifetime checks and numerical requirements where memory access, division or inversion actually occurs.
- Record progress in the existing issue and this plan. An added helper is not completion while the duplicate behavior still exists.

## Ordered implementation steps

### 1. Correct the small, proven allocation error — #637

**Scope:** `NativeHelpers.AllocateAlignedMemory` and its three IK callers. Correct padding so an already aligned pointer needs no extra movement. Keep allocation and freeing paired. Do not redesign IK or claim this explains a past crash.

**Remove:** the incorrect padding expression. **Verify:** all 16 address remainders preserve alignment and the requested memory extent; inspect the paired frees. No deliberate live memory corruption.

**Done when:** [#637](https://github.com/midona-rhel/Poser/issues/637) is independently reviewed and its focused checks and Release validation pass. This can ship without any ownership refactor.

### 2. Establish actor ownership through clothing-only — #610, first slice

Start by writing a small ownership table in the issue from actual callers: what is stored, who changes it, who releases it, and what happens on redraw, actor removal and GPose exit. Include `ClothingOnlyControl`, `InvisibleSkinService`, `CharacterModelNative`, the binding registry and existing actor removal paths.

Then implement only this behavior:

1. Keep `ClothingOnlyControl` as the shared entry for UI, scene restoration and bridge calls.
2. Make the actor's clothing-only state and active work explicitly belong to its current lifetime. Reuse existing actor identity and lifecycle notifications; do not invent another generation system.
3. Route redraw invalidation and actor/session removal through one cleanup responsibility. Stop or invalidate pending work before it can write to a released body. Retain an actual failed external cleanup only when there is something left to release.
4. Keep native addresses and model resources in Game. Keep application state as IDs and values. Shared Penumbra collections remain collection-owned; actor cleanup must not release another actor's shared work.
5. Preserve the corrected `CharacterModelNative.ReleaseSlot` path: release the model through its native reference-count operation. Do not manually reset its count, release only its resource handle, or release it a second time during actor cleanup.

**Remove:** separate bookkeeping or cleanup branches made redundant by that owner, including lazy stale-state pruning if explicit lifecycle handling replaces its purpose. Do not delete it merely because it looks defensive.

**Verify:** apply clothing-only, redraw, remove the actor, exit/re-enter GPose, and save/load a scene containing the setting. Check the normal result and that no pending operation writes to the retired body. Check a shared collection only if the traced path actually shares temporary resources.

**Done when:** the issue's ownership table matches the code; all entry points reach the same behavior; state is invalidated/released through the documented lifetime; focused tests and local user acceptance pass. No animation or transform migration is required for this step.

### 3. Make actor transform results truthful — #631

Keep `PosingService` as the native write owner and `TransformRuntimePort` as the application adapter. Return whether the write happened; update callers such as parenting to use that result. Remove the positive-only scale clamp that contradicts accepted authored values. Keep legitimate finite-value checks; establish any actual native scale limit instead of choosing an arbitrary one.

**Remove:** unconditional success after void setters and the contradictory clamp. **Verify:** an existing small Customize+ scale, negative mirroring, requested versus read-back values, undo/redo, and an unavailable actor. Do not expand this into every imaginable numerical input.

**Done when:** [#631](https://github.com/midona-rhel/Poser/issues/631) preserves the intended data and refuses truthfully. Do not move the whole transform service into an actor object as part of this fix.

### 4. Correct transform conversion one caller family at a time — #632

Map the actual uses of `Transform.FromMatrix` first. Preserve authored components where they exist. Handle reflection, collapsed axes and shear according to what each operation needs; an SRT value cannot represent arbitrary shear. Require an inverse only where one is computed.

Start with one affected caller family, then migrate the remaining callers in separately reviewable changes. A temporary forwarding API must use the corrected implementation; it must not create a competing conversion policy.

**Remove:** invented 0.01 scale and duplicate conversion work as callers migrate. **Verify:** equivalent geometry for the chosen caller, reflection/collapsed axes where supported, and the relevant normal posing/collider path. Delete the old conversion only when callers are migrated. Track in [#632](https://github.com/midona-rhel/Poser/issues/632).

### 5. Delete inactive autosave writing — #635

Keep `SceneAutoSaveService` as the active writer. Trace legacy root lookup, recovery records and backup browsing before removing dependencies. Move required read-only behavior to an existing library/configuration owner, then remove unused pose capture, writer, status and registration paths.

**Preserve:** all old files, legacy browsing and meaningful recovery data. **Remove:** inactive writing and obsolete tests that only describe that deleted writer. **Verify:** scene snapshot, retention/status, expected exit behavior and opening an old pose backup. Track in [#635](https://github.com/midona-rhel/Poser/issues/635).

### 6. Reuse import and placement rules — two independent changes

**6a — #634:** Define import mode/type/scope eligibility once in the existing pose-import UI owner. Keep separate menu and dialog layouts; delete their duplicated predicates and labels. Check the same ordinary pose, CMP and selective-import choices in both surfaces. Do not build a new form framework. [Issue](https://github.com/midona-rhel/Poser/issues/634).

**6b — #636:** Route pane camera/light entry creation through `LibrarySceneActions.SpawnEntry`. Delete `ScenePane.LoadEntry` option construction. Preserve additive placement, selection and notices. Check entry creation from the pane and Library, including the existing missing-anchor fallback. [Issue](https://github.com/midona-rhel/Poser/issues/636).

These changes do not depend on a complete actor-state migration.

### 7. Consolidate one resource/recovery path per change — #610 follow-ups

Use the caller evidence in the [Application plan](audit-2026-10-25/application-plan.md). Create or link a bounded implementation issue before each slice; do not combine these into one cleanup PR.

- **MCDF cleanup:** keep `McdfTeardown`; pass the intended restoration target explicitly, then remove duplicated resource release from `McdfImport.Rollback`. Preserve immediate pre-import rollback versus original-baseline Reset. Check import/reset and one real cleanup failure followed by retry.
- **Pose-edit recovery:** make returned and thrown write failures use the existing restoration path in `PoseEditService`. Check a second write failing after the first succeeds. Do not introduce another transaction system.
- **Scene-save temporary files:** make the existing save attempt own its temporary files across its early returns. Check validation refusal and successful output; remove success-only cleanup.

A discovered native correctness defect can be fixed independently ahead of these steps when evidence warrants it. Record the reprioritization; do not silently expand a refactor.

### 8. Decide the next actor-state migration from the first result

After clothing-only is accepted, review what became simpler: fewer state copies, fewer cleanup paths, a clear owner and understandable comments. If the approach helps, choose **one** next actor-local state area. Animation overrides are a candidate, not a commitment to rewrite animation.

For that next area, first identify which state really belongs to an actor, which belongs to a skeleton version, and which is shared. Move the authoritative state, migrate callers, and delete the former store. Keep existing specialist services and history behavior. Do not copy all service dictionaries into an `ActorContext` while leaving the originals active.

**Decision point:** if the clothing-only change only added forwarding layers and did not remove complexity, simplify it before repeating the pattern. Do not roll it out across the app by default.

## Verification and completion for each step

1. Describe the real starting state, action and expected result in the issue. Distinguish source evidence from a reproduced symptom.
2. Implement and review the complete small change, including deletion of superseded code and plain-language comments.
3. Run focused behavior checks, then the required Release build/test gates. Reuse recorded evidence; do not rerun unrelated live matrices after every edit.
4. Deploy native/UI changes through [the existing testing contract](../process/testing.md). Give the user a short actionable test card; compilation is not user acceptance. Documentation-only changes need no game deployment.
5. Merge the accepted step independently. Record the commit, checks and remaining uncertainty before starting the next dependent step. Each step must remain independently revertible; do not require an unfinished later migration for correctness.

Report production code, tests and documentation separately. Reduced line count is useful evidence, not a quota. Completion means one owner, obsolete paths removed, behavior checked and explanations understandable—not merely fewer files or more interfaces.

## Further findings and evidence

The area plans are an investigation backlog, not extra mandatory scope for each change:
[Domain](audit-2026-10-25/domain-plan.md), [Game](audit-2026-10-25/game-plan.md),
[Application](audit-2026-10-25/application-plan.md), [Documents](audit-2026-10-25/documents-plan.md),
[UI/host](audit-2026-10-25/ui-host-plan.md), [scene/world](audit-2026-10-25/game-extra-plan.md),
[spawn](audit-2026-10-25/game-spawn-plan.md), [gaze](audit-2026-10-25/gaze-plan.md),
[tests/build](audit-2026-10-25/domain-tests-scripts-build-plan.md), and
[generated-source limitations](audit-2026-10-25/generated-review-plan.md).

Before implementing a suspected timing problem, establish a reachable caller and outcome. Do not promote hypothetical fast-click, malformed-input or concurrency stories to requirements without that evidence. Preserve deliberate differences such as owned-object destruction versus borrowed-object restoration.

The documentation pass in `3b543112` changed 504 C# files with no executable-token/directive differences in normal or DEBUG parsing. Release compilation and 911 existing tests passed. That validates the patch, not the correctness of every behavior described in the audit. [#633](https://github.com/midona-rhel/Poser/issues/633) remains the readability work across these steps; comments must be reviewed for ordinary human comprehension, not just factual consistency.
