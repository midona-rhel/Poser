using System;
using System.Collections.Generic;
using Dalamud.Plugin.Services;
using Poser.Application.Transforms;
using Poser.Core;
using Poser.Domain.Identity;
using Poser.Domain.Transforms;
using Poser.Entities;
using Poser.Files;
using Poser.Game.Bindings;
using Poser.Services;

namespace Poser.Game.Posing;

/// <summary>
/// Applies a <see cref="PoseImportPlan"/> INSIDE the apply pass — Brio's
/// interactive pose-file import (SkeletonPosingCapability.ImportSkeletonPose →
/// PoseImporter.ApplyBone, Game/Posing/PoseImporter.cs:9-87), on the same
/// transitive-action lifecycle as <see cref="IkBakeCapture"/>.
///
/// The mechanism is the point: each write's delta is diffed against
/// <c>bone.LastRawTransform</c> exactly as the pass has just refreshed it —
/// after the synchronous reset cleared the scope's stacks, and after every
/// parent already written in this same pass has moved this bone. A basis read
/// on any earlier tick (the replaced ImportEdit path) predates those parent
/// deltas, so children double-move under propagation. Partial-component
/// imports mask the DELTA (Brio PoseImporter.cs:35 → PoseInfo.Apply's applyTo,
/// PoseInfo.cs:108 calc.Filter), so an excluded component contributes nothing
/// instead of pinning the bone to a stale absolute.
///
/// One pass is not the whole story for faces. File data is exported from
/// <c>bone.LastRawTransform</c> AFTER the update phase's post-reparent
/// refresh (PoseFileService.cs:74, BonePosingService STEP 4; Brio
/// SkeletonService.cs:243), while the pass's mid-pass basis is PRE-reparent;
/// for bones of a non-zero partial the two spaces differ by the head's
/// posed-vs-animated delta, so the first diff lands the face wrong in both
/// tools. Brio converges by scheduling Snapshot at +4 ticks after its import
/// (PosingCapability.cs:249-250), which runs ReconcileHead (:316-317,
/// :323-352) into ReconcileChildren("j_kao", false) (:370-401): re-export
/// the j_kao subtree from the now POST-reparent LastRawTransform (:385) and
/// re-import it in-pass with TransformComponents.All (:380). This engine
/// ports that as a second one-shot transitive batch between the apply pass
/// and completion; the single history entry covers the CONVERGED state.
/// </summary>
public sealed class PoseImportCapture : IDisposable
{
    /// <summary>Framework ticks a registered batch is given to reach a pass
    /// before the import gives up and rolls back — same guard as
    /// <see cref="IkBakeCapture"/>: a skeleton that stops updating must not
    /// leave the import pending forever.</summary>
    private const int CompletionTimeoutTicks = 60;

    /// <summary>Brio schedules its post-import Snapshot — the reconcile's
    /// driver — at +4 ticks (PosingCapability.cs:249-250). Poser counts from
    /// the apply batch's outcome instead of from registration; same spirit:
    /// the post-reparent refresh has settled before the subtree re-export.
    /// The single <see cref="CompletionTimeoutTicks"/> armed at Begin spans
    /// both phases.</summary>
    private const int ReconcileDelayTicks = 4;

    /// <summary>Brio's Reconcile() runs its export-reset-reimport at +2
    /// ticks (PosingCapability.cs:419,428).</summary>
    private const int FlattenDelayTicks = 2;

    private readonly IFramework _framework;
    private readonly StableBindingRegistry _bindings;
    private readonly IBonePosingService _posing;
    private readonly ITransformRuntimePort _runtime;
    private readonly TransformHistory _history;
    private readonly TransformGestureService _gestures;
    private readonly IkBakeCapture _ikBake;
    private readonly IPoseFileService _poseFiles;
    private readonly ISkeletonService _skeletons;
    private readonly IPluginLog _log;

    /// <summary>One slot skeleton's share of an import: the plan's writes
    /// keyed the way the apply pass identifies a bone.</summary>
    private sealed class SlotImport
    {
        public required ISkeleton Skeleton;
        public required Dictionary<(int Partial, int Index),
            (TransformTargetId Target, Transform File, TransformComponents Components)> Writes;
        public bool Ended;
        public bool Executed;
    }

    /// <summary>Which transitive batch the pending import is waiting on:
    /// the plan's file writes, the expression import's head restore, the
    /// post-reparent face reconcile, or the expression import's final
    /// whole-pose flatten.</summary>
    private enum ImportStage
    {
        Apply,
        HeadRestore,
        Reconcile,
        Flatten,
    }

    private sealed class Import
    {
        public required long Generation;
        public required string Description;
        /// <summary>The CURRENT stage's batches. <see cref="BeginReconcile"/>
        /// replaces the verified apply slots with the one reconcile slot.</summary>
        public required List<SlotImport> Slots;
        /// <summary>Ordered import targets and their pre-edit states —
        /// captured before anything was written, so a failure restores
        /// exactly what was there and success has a Before half that needs
        /// no re-reading.</summary>
        public required List<TransformTargetId> Order;
        public required Dictionary<TransformTargetId, TransformTargetState> Before;
        /// <summary>Targets the synchronous reset cleared. A reset bone that
        /// had no authored layers did not change and stays out of the
        /// history entry unless a write landed on it.</summary>
        public required HashSet<TransformTargetId> Resets;
        /// <summary>Targets an action (or the model edit) actually wrote.</summary>
        public readonly HashSet<TransformTargetId> Written = new();
        /// <summary>Fires exactly once when an import <see cref="Begin"/>
        /// returned Ok for finishes — with true after the history entry
        /// landed, false after a rollback. A Begin that returned Fail never
        /// fires it; a pending import dropped by Dispose does not either
        /// (session teardown restores animation state wholesale).</summary>
        public Action<bool>? OnFinished;
        /// <summary>The import targets the pose library's hidden preview
        /// body. Its changes are scenery, never user edits — they must not
        /// spend the user's undo stack.</summary>
        public required bool PreviewTarget;
        public ImportStage Stage = ImportStage.Apply;
        /// <summary>Whether this is an expression import — it runs the head
        /// restore and, at the very end, Brio's whole-pose flatten
        /// (Reconcile(reset: true), PosingCapability.cs:417-429): the phase-2
        /// call leaves ImportPose_Internal's reset/reconcile at their TRUE
        /// defaults, so unlike a body import (reconcile: false, :156) the
        /// expression chain finishes by exporting the entire visual pose,
        /// clearing every stack, and re-importing it whole.</summary>
        public bool Expression;
        /// <summary>Expression imports only: every j_kao instance the plan
        /// writes, with the pre-import absolute the head-restore stage puts
        /// back — Brio's tempPose reduced to the one bone its
        /// expressionPhase2 actually uses (PosingCapability.cs:194,
        /// PoseImporter.cs:11-26). The head lands transiently in the apply
        /// stage so the face computes its deltas in the FILE's head space;
        /// this restores it. Seeded at Begin, RE-EXPRESSED by the apply
        /// pass in its own basis — see <see cref="HeadRestore.PreImport"/>
        /// for why the space is the whole point.</summary>
        public List<HeadRestore>? HeadRestores;
        /// <summary>Whether the plan wrote any Character-slot bone of a
        /// non-zero partial — the only writes whose export/basis spaces can
        /// disagree, so the only imports a reconcile can converge.</summary>
        public bool WroteFacePartial;
        /// <summary>The Character slot skeleton among the apply batches —
        /// where Brio's ReconcileHead looks up j_kao
        /// (PosingCapability.cs:326, PoseInfoSlot.Character).</summary>
        public ISkeleton? CharacterSkeleton;
        public string? Failure;
        public bool Completing;
    }

    /// <summary>One j_kao instance's target for the expression head
    /// restore.</summary>
    private sealed class HeadRestore
    {
        public required IBone Bone;
        public required TransformTargetId Target;

        /// <summary>The pre-import head absolute the restore stage writes
        /// back (position-only; rotation reverts through the stack pop).
        ///
        /// THE SPACE IS THE FIX (repeated-apply head drift, user
        /// 2026-08-10). Begin seeds the cached <c>LastRawTransform</c>,
        /// which is the last settled frame BEFORE the rewind: the facade's
        /// bracket pauses the actor and the settle tick rewinds every
        /// paused control to LocalTime 0 on the very tick it calls Begin
        /// (CleanPoseFacade.BeginImport), so no pass has evaluated the
        /// rewound animation yet. Every other write in this chain diffs
        /// against the REWOUND in-pass basis, and the final flatten bakes
        /// its stacks against that same rewound basis — so restoring the
        /// head to a pre-rewind absolute baked
        /// (anim(pause frame) − anim(LocalTime 0)) into the head position
        /// ON TOP of the previous apply's settled state, once per apply:
        /// progressive head drift whenever the animation ran between
        /// applies. <see cref="ApplyBone"/> therefore overwrites the seed
        /// with the bone's own apply-pass basis — anim(rewound) ⊕ the
        /// pre-import stacks the expression reset deliberately leaves on
        /// the head — which IS the pre-import head expressed in the
        /// chain's one basis. Apply N+1 then restores exactly apply N's
        /// settled head and the restore delta rejects as near-identity.
        /// Brio's pre-rewind capture (tempPose) matches its own brief
        /// bracket — it hands speed back +2 ticks after the import call
        /// (ActionTimelineCapability.cs:169-175), before its reconcile
        /// ever reads the pose; Poser holds the pause through reconcile
        /// and flatten, so the in-pass basis is the only consistent
        /// space.</summary>
        public required Transform PreImport;
    }

    private Import? _pending;
    private long _generation;

    public PoseImportCapture(
        IFramework framework,
        StableBindingRegistry bindings,
        IBonePosingService posing,
        ITransformRuntimePort runtime,
        TransformHistory history,
        TransformGestureService gestures,
        IkBakeCapture ikBake,
        IPoseFileService poseFiles,
        ISkeletonService skeletons,
        IPluginLog log)
    {
        _framework = framework;
        _bindings = bindings;
        _posing = posing;
        _runtime = runtime;
        _history = history;
        _gestures = gestures;
        _ikBake = ikBake;
        _poseFiles = poseFiles;
        _skeletons = skeletons;
        _log = log;
        _posing.TransitiveActionsEnded += OnTransitiveActionsEnded;
    }

    /// <summary>Whether an import is armed and has not finished: true from
    /// registration until the pass has executed the actions AND the history
    /// entry has been appended (or the whole edit rolled back).</summary>
    public bool IsPending => _pending != null;

    /// <summary>
    /// Arms one plan, on one tick: capture every affected target, apply the
    /// reset scope and the model transform synchronously, register the
    /// per-bone file writes for the next pass. Ok means the import is armed
    /// and its actions are queued — an in-pass failure rolls the whole edit
    /// back and logs, it does not reach this return value.
    /// </summary>
    public GestureResult Begin(
        PoseImportPlan plan,
        string description,
        Action<bool>? onFinished = null,
        bool expression = false)
    {
        if (!_framework.IsInFrameworkUpdateThread)
            return GestureResult.Fail("Pose import must run on the framework thread.");
        if (_pending != null)
            return GestureResult.Fail("A pose import is already applying.");
        if (_ikBake.IsPending)
            return GestureResult.Fail("An IK bake is still applying.");
        // A live transform gesture owns bones right now; importing under it
        // would interleave two writers on the same targets.
        if (_gestures.ActiveGesture != null)
            return GestureResult.Fail("Finish the current transform gesture first.");
        if (plan.IsEmpty)
            return GestureResult.Fail("Nothing in this file applies to the chosen scope.");

        // A plan's bones all belong to one actor; any of them names it.
        var planActor = plan.ModelActor
            ?? (plan.Writes.Count > 0 ? plan.Writes[0].Bone.Skeleton.Actor
                : plan.Resets.Count > 0 ? plan.Resets[0].Skeleton.Actor
                : null);

        var import = new Import
        {
            Generation = ++_generation,
            Description = description,
            Slots = new List<SlotImport>(),
            Order = new List<TransformTargetId>(),
            Before = new Dictionary<TransformTargetId, TransformTargetState>(),
            Resets = new HashSet<TransformTargetId>(),
            OnFinished = onFinished,
            Expression = expression,
            PreviewTarget = planActor?.ActorKind == ActorKind.Preview,
        };

        // Resolve and capture EVERYTHING before mutating anything, so a
        // stale target fails synchronously with nothing to roll back.
        TransformPortResult captured;
        var resetBones = new List<(IBone Bone, TransformTargetId Target)>(plan.Resets.Count);
        foreach (var bone in plan.Resets)
        {
            // Virtual bones never carry stacks of their own and have no
            // stable binding — the bake's export predicate skips them too.
            if (bone is VirtualBone)
                continue;
            if (_bindings.GetBoneId(bone) is not { } resetId)
                return GestureResult.Fail(
                    $"Import target {bone.BoneName} could not be resolved.");
            var target = TransformTargetId.ForBone(resetId);
            resetBones.Add((bone, target));
            if (!import.Before.ContainsKey(target))
            {
                captured = _runtime.Capture(target);
                if (!captured.Success || captured.State is not { } state)
                    return GestureResult.Fail(
                        captured.Detail ?? $"Could not capture {target}.");
                import.Before[target] = state;
                import.Order.Add(target);
            }
            import.Resets.Add(target);
        }

        var slotMap = new Dictionary<ISkeleton, SlotImport>();
        foreach (var (bone, file, components) in plan.Writes)
        {
            if (bone is VirtualBone)
                continue;
            if (_bindings.GetBoneId(bone) is not { } writeId)
                return GestureResult.Fail(
                    $"Import target {bone.BoneName} could not be resolved.");
            var target = TransformTargetId.ForBone(writeId);
            if (!import.Before.ContainsKey(target))
            {
                captured = _runtime.Capture(target);
                if (!captured.Success || captured.State is not { } state)
                    return GestureResult.Fail(
                        captured.Detail ?? $"Could not capture {target}.");
                import.Before[target] = state;
                import.Order.Add(target);
            }
            if (!slotMap.TryGetValue(bone.Skeleton, out var slot))
            {
                slotMap[bone.Skeleton] = slot = new SlotImport
                {
                    Skeleton = bone.Skeleton,
                    Writes = new Dictionary<(int, int),
                        (TransformTargetId, Transform, TransformComponents)>(),
                };
                import.Slots.Add(slot);
            }
            slot.Writes[(bone.PartialId, bone.BoneIndex)] = (target, file, components);
            if (bone.Skeleton.Slot == PoseSlot.Character)
            {
                import.CharacterSkeleton ??= bone.Skeleton;
                if (bone.PartialId != 0)
                    import.WroteFacePartial = true;
                // The head's pre-import absolute — a SEED only: this cached
                // value predates the settle tick's LocalTime rewind, and
                // the apply pass replaces it with the bone's own in-pass
                // basis (HeadRestore.PreImport has the space math). Only
                // instances the plan writes restore: a file without j_kao
                // never moved the head, so unlike Brio's blind
                // RemoveLastStack (which would eat a USER head stack in
                // that case) the restore stage simply skips.
                if (expression && bone.BoneName == "j_kao")
                    (import.HeadRestores ??= new()).Add(new HeadRestore
                    {
                        Bone = bone,
                        Target = target,
                        PreImport = bone.LastRawTransform,
                    });
            }
        }

        (TransformTargetId Target, PoseTransform Desired)? model = null;
        if (plan.ModelActor is { } modelActor)
        {
            if (_bindings.GetActorId(modelActor) is not { } modelActorId)
                return GestureResult.Fail("The actor could not be resolved.");
            var target = TransformTargetId.ForActor(modelActorId);
            if (!import.Before.ContainsKey(target))
            {
                captured = _runtime.Capture(target);
                if (!captured.Success || captured.State is not { } state)
                    return GestureResult.Fail(
                        captured.Detail ?? $"Could not capture {target}.");
                import.Before[target] = state;
                import.Order.Add(target);
            }
            model = (target, new PoseTransform(
                plan.ModelTransform.Position,
                plan.ModelTransform.Rotation,
                plan.ModelTransform.Scale));
        }

        if (import.Order.Count == 0)
            return GestureResult.Fail("No target of this import could be bound.");

        // Reset-before-import, synchronously — the bake's STEP 2
        // (IkBakeCapture.cs:285-298): clear the scope's authored stacks so
        // the pass diffs against the reset basis. Named service layers stay;
        // their contribution remains in the pass's basis so the file deltas
        // exclude it instead of absorbing a value its owner will re-drive.
        foreach (var (bone, _) in resetBones)
        {
            _posing.GetPoseInfo(bone.Skeleton)
                .GetPoseInfo(bone.BoneName, bone.PartialId)
                .RestoreInteractiveStacks(Array.Empty<BonePoseTransformInfo>());
        }

        // The model transform is an actor edit, outside the skeleton pass —
        // applied synchronously exactly as the replaced ImportEdit did.
        if (model is { } modelEdit)
        {
            var applied = _runtime.ApplyAbsolute(
                import.Before[modelEdit.Target], modelEdit.Desired);
            if (!applied.Success)
            {
                Rollback(import);
                return GestureResult.Fail(
                    applied.Detail ?? "Could not apply the model transform.");
            }
            import.Written.Add(modelEdit.Target);
        }

        // A plan without file writes (reset-only, model-only) has no pass to
        // wait for; it completes here as the same single history entry.
        if (import.Slots.Count == 0)
        {
            var failure = AppendHistory(import);
            if (failure == null)
            {
                Notify(import, true);
                return GestureResult.Ok();
            }
            // Begin itself reports this failure, so the callback stays
            // silent — it fires only for imports Begin returned Ok for.
            _log.Warning($"Pose import failed: {failure}");
            Rollback(import);
            return GestureResult.Fail(failure);
        }

        // Register the per-bone file writes for the next pass — Brio's
        // ImportSkeletonPose (SkeletonPosingCapability.cs:62-66).
        _pending = import;
        foreach (var slot in import.Slots)
        {
            var scope = slot;
            _posing.RegisterTransitiveAction(
                scope.Skeleton,
                (bone, poseInfo) => ApplyBone(import, scope, bone, poseInfo));
        }

        _framework.RunOnTick(
            () => OnTimeout(import.Generation),
            delayTicks: CompletionTimeoutTicks);
        return GestureResult.Ok();
    }

    /// <summary>
    /// Brio's <c>PoseImporter.ApplyBone</c> (Game/Posing/PoseImporter.cs:9-87),
    /// running inside the apply pass. The plan supplies the file absolute and
    /// the component mask; the basis is <c>bone.LastRawTransform</c> exactly
    /// as the pass has just refreshed it; the delta is masked and appended as
    /// a stack the same pass applies immediately.
    ///
    /// Brio's near-identity early-out (PoseInfo.cs:100) is taken on the
    /// MASKED delta, caller-side like the bake's: a bone whose in-scope
    /// components already match its basis must not gain a stack.
    /// </summary>
    private void ApplyBone(
        Import import,
        SlotImport slot,
        IBone bone,
        BonePoseInfo poseInfo)
    {
        try
        {
            if (!slot.Writes.TryGetValue(
                    (bone.PartialId, bone.BoneIndex), out var entry))
                return;

            var desired = entry.File;
            var basis = bone.LastRawTransform;

            // Expression imports: re-express this head instance's restore
            // target in the pass's own basis — anim(rewound) ⊕ the
            // pre-import stacks the reset left on the head — BEFORE the
            // file's head lands. The Begin-time seed is pre-rewind;
            // restoring it re-baked the pause-frame-vs-LocalTime-0 offset
            // into the head on every apply (HeadRestore.PreImport).
            if (import.Stage == ImportStage.Apply &&
                import.HeadRestores is { } restores)
            {
                foreach (var restore in restores)
                {
                    if (ReferenceEquals(restore.Bone, bone))
                    {
                        restore.PreImport = basis;
                        break;
                    }
                }
            }

            var delta = BonePoseInfo.FilterDelta(
                BonePoseInfo.Diff(desired, basis), entry.Components);
            if (IsApproximatelyIdentity(delta))
                return;

            // Propagation stays All (Brio PoseImporter.cs:35, 3rd argument):
            // an imported bone carries its children with it exactly as the
            // pose it replaced did. The mask applies to the delta only.
            // forceNewStack matches Brio's PoseImporter (every call passes
            // true): each import write is its OWN stack entry, which is what
            // makes the expression head restore's RemoveLastStack pop
            // exactly the phase-1 head write and nothing else.
            if (poseInfo.Apply(
                    desired, basis,
                    Poser.Core.TransformComponents.All,
                    entry.Components,
                    forceNewStack: true) == null)
            {
                import.Failure ??=
                    $"{bone.BoneName} produced a non-finite import delta.";
                return;
            }
            import.Written.Add(entry.Target);
        }
        catch (Exception ex)
        {
            // A throw here is inside the physics detour; swallow it into the
            // import's own failure so the pass stays intact and the whole
            // edit rolls back on completion.
            import.Failure ??= $"{bone.BoneName}: {ex.Message}";
        }
    }

    /// <summary>Raised from the native hooks when the interval that owned a
    /// batch ends. Records only — the completion itself needs the framework
    /// thread.</summary>
    private void OnTransitiveActionsEnded(TransitiveActionOutcome outcome)
    {
        if (_pending is not { } import)
            return;
        var complete = true;
        var known = false;
        foreach (var slot in import.Slots)
        {
            if (ReferenceEquals(slot.Skeleton, outcome.Skeleton) && !slot.Ended)
            {
                slot.Ended = true;
                slot.Executed = outcome.Executed;
                known = true;
            }
            if (!slot.Ended)
                complete = false;
        }
        if (!known || !complete || import.Completing)
            return;
        import.Completing = true;
        switch (import.Stage)
        {
            case ImportStage.Apply:
                // The apply batches have run. An expression import restores
                // the head first — Brio schedules its phase 2 at +4 ticks
                // (PosingCapability.cs:249-250, the same delay its reconcile
                // uses); everything else goes straight to the reconcile
                // decision at the same delay.
                _framework.RunOnTick(
                    import.HeadRestores is { Count: > 0 }
                        ? () => BeginHeadRestore(import.Generation)
                        : () => BeginReconcile(import.Generation),
                    delayTicks: ReconcileDelayTicks);
                break;
            case ImportStage.HeadRestore:
                // Brio's phase 2 runs with generateSnapshot: true, so its
                // Snapshot — the reconcile driver — fires another 4 ticks
                // after the restore pass (PosingCapability.cs:308-309,
                // :249-250).
                _framework.RunOnTick(
                    () => BeginReconcile(import.Generation),
                    delayTicks: ReconcileDelayTicks);
                break;
            case ImportStage.Reconcile:
                FinishAfterReconcile(import);
                break;
            default:
                _framework.RunOnTick(() => Complete(import.Generation));
                break;
        }
    }

    /// <summary>Where the reconcile outcome goes next: a body import is
    /// done, an expression import continues into Brio's whole-pose flatten
    /// (its phase-2 Snapshot runs Reconcile(reset: true) — the phase-2
    /// ImportPose_Internal call leaves reset/reconcile at their TRUE
    /// defaults, PosingCapability.cs:308-309 vs the body path's
    /// reconcile: false at :156).</summary>
    private void FinishAfterReconcile(Import import)
    {
        if (import.Expression && import.Failure == null)
            _framework.RunOnTick(
                () => BeginFlatten(import.Generation),
                delayTicks: FlattenDelayTicks);
        else
            _framework.RunOnTick(() => Complete(import.Generation));
    }

    private void OnTimeout(long generation)
    {
        if (_pending is not { } import || import.Generation != generation)
            return;
        import.Failure ??= import.Stage switch
        {
            ImportStage.Apply => "The import never reached an apply pass.",
            ImportStage.HeadRestore =>
                "The head restore never reached an apply pass.",
            ImportStage.Flatten =>
                "The pose flatten never reached an apply pass.",
            _ => "The face reconcile never reached an apply pass.",
        };
        Complete(generation);
    }

    /// <summary>
    /// Brio's <c>Reconcile(reset: true)</c> (PosingCapability.cs:417-429),
    /// the expression chain's final stage: export the ENTIRE current pose
    /// (GeneratePoseData — per-slot post-reparent absolutes), clear every
    /// interactive stack (Reset), and re-import the export whole with every
    /// component. Whatever the head dance left behind is erased; the final
    /// state is one clean absolute re-expression of what is on screen.
    /// Brio's nested second round (Reset's own Snapshot → Reconcile(false))
    /// re-imports the same absolutes WITHOUT a reset, so every delta
    /// rejects as near-identity — one round is the entire effect. The model
    /// transform is skipped: Brio resets it and re-applies the exported
    /// difference onto the reset original, a net no-op an expression never
    /// disturbs.
    /// </summary>
    private void BeginFlatten(long generation)
    {
        if (_pending is not { } import || import.Generation != generation)
            return;

        var applied = import.Failure == null;
        if (applied)
        {
            foreach (var slot in import.Slots)
                applied &= slot.Executed;
        }
        if (!applied || import.CharacterSkeleton is not { } character)
        {
            Complete(generation);
            return;
        }

        var slots = _skeletons.GetSkeletons(character.Actor);
        if (slots.Count == 0)
        {
            Complete(generation);
            return;
        }
        var exported = _poseFiles.CreatePoseFile(slots);
        var options = new PoseImportOptions
        {
            ApplyRotation = true,
            ApplyPosition = true,
            ApplyScale = true,
            ApplyBody = true,
            ApplyFace = true,
            ApplyMainHand = true,
            ApplyOffHand = true,
            ApplyProp = true,
            ApplyOrnament = true,
            ApplyModelTransform = false,
            ResetBeforeImport = true,
        };
        var plan = _poseFiles.BuildImportPlan(slots, exported, options);

        // Mid-flight capture, the reconcile's pattern: any target the
        // flatten can touch that the earlier stages did not is captured
        // before it changes, so the one rollback covers every stage.
        var resetBones = new List<(IBone Bone, TransformTargetId Target)>(plan.Resets.Count);
        foreach (var bone in plan.Resets)
        {
            if (bone is VirtualBone)
                continue;
            if (_bindings.GetBoneId(bone) is not { } id)
                continue;
            var target = TransformTargetId.ForBone(id);
            if (!import.Before.ContainsKey(target))
            {
                var captured = _runtime.Capture(target);
                if (!captured.Success || captured.State is not { } state)
                    continue;
                import.Before[target] = state;
                import.Order.Add(target);
            }
            resetBones.Add((bone, target));
            import.Resets.Add(target);
        }

        var slotMap = new Dictionary<ISkeleton, SlotImport>();
        var flattenSlots = new List<SlotImport>();
        foreach (var (bone, file, components) in plan.Writes)
        {
            if (bone is VirtualBone)
                continue;
            if (_bindings.GetBoneId(bone) is not { } id)
                continue;
            var target = TransformTargetId.ForBone(id);
            if (!import.Before.ContainsKey(target))
            {
                var captured = _runtime.Capture(target);
                if (!captured.Success || captured.State is not { } state)
                    continue;
                import.Before[target] = state;
                import.Order.Add(target);
            }
            if (!slotMap.TryGetValue(bone.Skeleton, out var slot))
            {
                slotMap[bone.Skeleton] = slot = new SlotImport
                {
                    Skeleton = bone.Skeleton,
                    Writes = new Dictionary<(int, int),
                        (TransformTargetId, Transform, TransformComponents)>(),
                };
                flattenSlots.Add(slot);
            }
            slot.Writes[(bone.PartialId, bone.BoneIndex)] = (target, file, components);
        }

        if (flattenSlots.Count == 0)
        {
            Complete(generation);
            return;
        }

        // Brio's Reset before the re-import: every interactive stack goes;
        // named service layers stay and re-drive themselves.
        foreach (var (bone, _) in resetBones)
        {
            _posing.GetPoseInfo(bone.Skeleton)
                .GetPoseInfo(bone.BoneName, bone.PartialId)
                .RestoreInteractiveStacks(Array.Empty<BonePoseTransformInfo>());
        }

        import.Slots = flattenSlots;
        import.Stage = ImportStage.Flatten;
        import.Completing = false;
        foreach (var slot in flattenSlots)
        {
            var scope = slot;
            _posing.RegisterTransitiveAction(
                scope.Skeleton,
                (bone, poseInfo) => ApplyBone(import, scope, bone, poseInfo));
        }
    }

    /// <summary>
    /// Brio's expressionPhase2 (PosingCapability.cs:233-247 with
    /// PoseImporter.cs:11-26), the middle stage of its expression dance: the
    /// apply stage moved the head to the FILE's head so the face landed
    /// face-local; now the head comes back. Per written j_kao instance, the
    /// phase-1 head stack pops (RemoveLastStack — exact because import
    /// writes are forceNewStack, like Brio's), and a POSITION-only restore
    /// to the pre-import absolute registers as the next batch, diffed
    /// in-pass against the post-removal basis exactly like any import
    /// write. Head rotation reverts through the pop alone; the +4-tick
    /// reconcile then re-expresses the face against the restored head.
    /// </summary>
    private void BeginHeadRestore(long generation)
    {
        if (_pending is not { } import || import.Generation != generation)
            return;

        var applied = import.Failure == null;
        if (applied)
        {
            foreach (var slot in import.Slots)
                applied &= slot.Executed;
        }
        if (!applied || import.HeadRestores is not { Count: > 0 } restores ||
            import.CharacterSkeleton is not { } skeleton)
        {
            Complete(generation);
            return;
        }

        var writes = new Dictionary<(int, int),
            (TransformTargetId, Transform, TransformComponents)>(restores.Count);
        foreach (var headRestore in restores)
        {
            // Only an instance the apply stage actually wrote carries a
            // phase-1 stack to pop; the near-identity early-out means a
            // head already at the file's pose gained none. A written
            // instance's PreImport was re-expressed by that same pass in
            // its own basis (HeadRestore.PreImport), so the write below
            // diffs two values of the SAME space and lands the head back
            // on the pre-import authored state exactly.
            if (!import.Written.Contains(headRestore.Target))
                continue;
            _posing.GetPoseInfo(skeleton)
                .GetPoseInfo(headRestore.Bone.BoneName, headRestore.Bone.PartialId)
                .RemoveLastInteractiveStack();
            writes[(headRestore.Bone.PartialId, headRestore.Bone.BoneIndex)] =
                (headRestore.Target, headRestore.PreImport, TransformComponents.Position);
        }

        if (writes.Count == 0)
        {
            // No instance was moved — nothing to restore, straight to the
            // reconcile decision.
            BeginReconcile(generation);
            return;
        }

        var restore = new SlotImport { Skeleton = skeleton, Writes = writes };
        import.Slots = new List<SlotImport> { restore };
        import.Stage = ImportStage.HeadRestore;
        import.Completing = false;
        _posing.RegisterTransitiveAction(
            restore.Skeleton,
            (bone, poseInfo) => ApplyBone(import, restore, bone, poseInfo));
    }

    /// <summary>
    /// The reconcile decision point, on the framework thread after the apply
    /// batches ran and reparenting settled. Skips (completing the import
    /// as-is) when: the apply phase already failed or never executed; the
    /// plan wrote no face-partial bones (a body/weapon-only import's spaces
    /// agree — no second pass to burn); the actor has no j_kao (Brio
    /// ReconcileHead's null check, PosingCapability.cs:326-327); IK is armed
    /// (Brio Snapshot :316-317 runs ReconcileHead only when
    /// <c>PoseInfo.HasIKStacks</c> is false — Brio stores IK per stack,
    /// Poser per bone, so the mapped guard is
    /// <see cref="IBonePosingService.HasEnabledIk"/>); or neither j_kao nor
    /// any ancestor is overridden (:331-345 — without a posed head the
    /// pre/post-reparent spaces coincide and there is nothing to converge).
    /// Otherwise registers the subtree re-import as the second batch.
    /// </summary>
    private void BeginReconcile(long generation)
    {
        if (_pending is not { } import || import.Generation != generation)
            return;

        // A failed or unexecuted apply phase completes (and rolls back) via
        // Complete's own verdict on the still-current apply slots.
        var applied = import.Failure == null;
        if (applied)
        {
            foreach (var slot in import.Slots)
                applied &= slot.Executed;
        }
        if (!applied)
        {
            Complete(generation);
            return;
        }

        var reconcile = BuildReconcile(import);
        if (reconcile == null)
        {
            // Nothing to converge — an expression import still owes the
            // flatten (Brio's Snapshot runs Reconcile(reset) whether or not
            // ReconcileHead had work).
            FinishAfterReconcile(import);
            return;
        }

        import.Slots = new List<SlotImport> { reconcile };
        import.Stage = ImportStage.Reconcile;
        import.Completing = false;
        _posing.RegisterTransitiveAction(
            reconcile.Skeleton,
            (bone, poseInfo) => ApplyBone(import, reconcile, bone, poseInfo));
    }

    /// <summary>
    /// Brio's <c>ReconcileChildren(j_kao, clearFaceStacks: false)</c>
    /// (PosingCapability.cs:370-401): the j_kao subtree's POST-reparent
    /// <c>LastRawTransform</c> absolutes (:385, read on a framework tick like
    /// this one) become a partial re-import applied with
    /// <c>TransformComponents.All</c> (:380). Brio collapses the subtree
    /// into a name-keyed file and re-resolves per name; Poser's plan
    /// machinery is per instance, so each instance re-imports its OWN
    /// absolute — identical where instances agree (reparenting just snapped
    /// them together) and exact where they do not. Bones already consistent
    /// diff to identity in-pass and gain no stack. Null when a guard in
    /// <see cref="BeginReconcile"/>'s list says skip.
    /// </summary>
    private SlotImport? BuildReconcile(Import import)
    {
        if (!import.WroteFacePartial ||
            import.CharacterSkeleton is not { } skeleton)
            return null;
        if (_posing.HasEnabledIk(skeleton))
            return null;
        // First-built instance = partial 0's body head (Skeleton.cs:256),
        // matching Brio's Character-slot j_kao lookup; the face and hair
        // partial roots hang off it through the connected-parent attach.
        if (skeleton.GetBone("j_kao") is not { } head || head is VirtualBone)
            return null;

        var poseInfo = _posing.GetPoseInfo(skeleton);
        // Brio checks HasStacks on j_kao and each ancestor (:331-345). The
        // Poser analog of Brio's stacks is the interactive (unnamed) layers:
        // named layers are service-owned recomputed state Brio has no
        // equivalent of, and they re-drive themselves regardless.
        var overridden = HasInteractiveStacks(poseInfo, head);
        for (var ancestor = head.ParentBone;
             !overridden && ancestor != null;
             ancestor = ancestor.ParentBone)
        {
            overridden = ancestor is not VirtualBone &&
                         HasInteractiveStacks(poseInfo, ancestor);
        }
        if (!overridden)
            return null;

        var subtree = new List<IBone>();
        CollectSubtree(head, subtree, new HashSet<IBone>());

        var writes = new Dictionary<(int, int),
            (TransformTargetId, Transform, TransformComponents)>(subtree.Count);
        foreach (var bone in subtree)
        {
            // A subtree bone without a binding cannot be captured for
            // rollback, so it is not written either — Brio likewise only
            // re-applies what its name lookup finds.
            if (_bindings.GetBoneId(bone) is not { } id)
                continue;
            var target = TransformTargetId.ForBone(id);
            if (!import.Before.ContainsKey(target))
            {
                // Captured BEFORE the reconcile writes it. A bone the apply
                // phase never touched carries no stacks, so this mid-flight
                // capture equals its pre-import state and the one rollback
                // restores both phases.
                var captured = _runtime.Capture(target);
                if (!captured.Success || captured.State is not { } state)
                    continue;
                import.Before[target] = state;
                import.Order.Add(target);
            }
            writes[(bone.PartialId, bone.BoneIndex)] =
                (target, bone.LastRawTransform, TransformComponents.All);
        }

        if (writes.Count == 0)
            return null;
        return new SlotImport { Skeleton = skeleton, Writes = writes };
    }

    /// <summary>Brio's ExportFaceBone walk (PosingCapability.cs:383-390):
    /// the bone and every descendant, which crosses into the face and hair
    /// partials through the connected-parent attach.</summary>
    private static void CollectSubtree(
        IBone bone, List<IBone> into, HashSet<IBone> seen)
    {
        if (bone is VirtualBone || !seen.Add(bone))
            return;
        into.Add(bone);
        foreach (var child in bone.ChildBones)
            CollectSubtree(child, into, seen);
    }

    private static bool HasInteractiveStacks(
        SkeletonPoseInfo poseInfo, IBone bone)
    {
        foreach (var stack in poseInfo
                     .GetPoseInfo(bone.BoneName, bone.PartialId).Stacks)
        {
            if (stack.Layer == null)
                return true;
        }
        return false;
    }

    /// <summary>
    /// The framework-thread half: by now the pass has run the actions and
    /// every imported stack is in place. Capture the after-states of what
    /// actually changed and append ONE history entry — or, on any failure,
    /// restore every captured target and append nothing.
    /// </summary>
    private void Complete(long generation)
    {
        if (_pending is not { } import || import.Generation != generation)
            return;
        _pending = null;

        var failure = import.Failure;
        if (failure == null)
        {
            foreach (var slot in import.Slots)
            {
                if (!slot.Executed)
                {
                    failure = "The apply pass never ran the import.";
                    break;
                }
            }
        }

        if (failure == null)
            failure = AppendHistory(import);

        if (failure != null)
        {
            _log.Warning($"Pose import failed: {failure}");
            Rollback(import);
        }
        Notify(import, failure == null);
    }

    /// <summary>The callback runs application code (the facade's speed
    /// restore); a throw there must not escape into the framework tick or
    /// the pass bookkeeping.</summary>
    private void Notify(Import import, bool success)
    {
        try
        {
            import.OnFinished?.Invoke(success);
        }
        catch (Exception ex)
        {
            _log.Warning($"Pose import completion callback threw: {ex.Message}");
        }
    }

    /// <summary>
    /// One undoable entry covering exactly what the import changed: the
    /// bones whose authored stacks the reset cleared, and the targets an
    /// action or the model edit wrote. Unlike the bake, an import that
    /// changed nothing is a legitimate outcome (the file matched the pose)
    /// and appends no entry rather than failing.
    /// </summary>
    private string? AppendHistory(Import import)
    {
        // Preview-body imports happen once per browsed file: recording them
        // would bury the user's real edits under scenery entries.
        if (import.PreviewTarget)
            return null;

        var before = new List<TransformTargetState>();
        var after = new List<TransformTargetState>();
        foreach (var target in import.Order)
        {
            var state = import.Before[target];
            // HasOverride is set by the capture to "this target had authored
            // layers", i.e. exactly what the reset cleared.
            var wasReset = import.Resets.Contains(target) && state.HasOverride;
            if (!wasReset && !import.Written.Contains(target))
                continue;
            var captured = _runtime.Capture(target);
            if (!captured.Success || captured.State is not { } current)
                return captured.Detail ?? $"Could not capture {target}.";
            before.Add(state);
            after.Add(current);
        }

        if (before.Count > 0)
            _history.Append(new TransformPatch(import.Description, before, after));
        return null;
    }

    /// <summary>Puts back every captured state. Nothing of the import
    /// survives a failure.</summary>
    private void Rollback(Import import)
    {
        foreach (var target in import.Order)
        {
            try
            {
                var restored = _runtime.Restore(import.Before[target]);
                if (!restored.Success)
                    _log.Warning(
                        $"Pose import rollback: {restored.Detail ?? target.ToString()}");
            }
            catch (Exception ex)
            {
                _log.Warning($"Pose import rollback threw for {target}: {ex.Message}");
            }
        }
    }

    /// <summary>Brio's <c>Transform.IsApproximatelySame(Transform.Identity)</c>
    /// (Core/Transform.cs:96-101) on a stack delta: position and scale are
    /// additive, rotation multiplicative.</summary>
    private static bool IsApproximatelyIdentity(Transform delta)
    {
        const float tolerance = 0.000001f;
        return MathF.Abs(delta.Position.X) < tolerance &&
               MathF.Abs(delta.Position.Y) < tolerance &&
               MathF.Abs(delta.Position.Z) < tolerance &&
               MathF.Abs(delta.Scale.X) < tolerance &&
               MathF.Abs(delta.Scale.Y) < tolerance &&
               MathF.Abs(delta.Scale.Z) < tolerance &&
               MathF.Abs(MathF.Abs(delta.Rotation.W) - 1f) < tolerance;
    }

    /// <summary>A pending import never outlives the session: its registered
    /// actions die with the posing interval and the completion it was
    /// waiting for is dropped.</summary>
    public void Dispose()
    {
        _posing.TransitiveActionsEnded -= OnTransitiveActionsEnded;
        _pending = null;
    }
}
