using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;
using Dalamud.Plugin.Services;
using Poser.Application.Lifecycle;
using Poser.Application.Posing;
using Poser.Domain.Operations;
using Poser.Application.Scene;
using Poser.Application.Transforms;
using Poser.Domain.Posing;
using Poser.Domain.Identity;
using Poser.Domain.Scene;
using Poser.Domain.Transforms;
using Poser.Game.Bindings;
using Poser.Documents.Files;
using Poser.Game.Entities;
using Poser.Game.Services;

namespace Poser.Game.Posing;

/// <summary>
/// Turns a pose import's stages into batches: resolves the plan (or the live
/// pose, for the flatten, or the j_kao subtree, for the reconcile) against the
/// LIVE slot skeletons on the stage's own tick, and captures every target a
/// batch can touch before anything changes, so the import's one rollback
/// covers every stage; and lands each batch's writes inside the apply pass.
/// Holds no import and no skeleton across ticks.
/// </summary>
internal sealed class PoseImportPlanner
{
    private readonly StableBindingRegistry _bindings;
    private readonly IBonePosingService _posing;
    private readonly ITransformRuntimePort _runtime;
    private readonly IPoseFileService _poseFiles;
    private readonly ISkeletonService _skeletons;
    private readonly IPluginLog _log;

    public PoseImportPlanner(
        StableBindingRegistry bindings,
        IBonePosingService posing,
        ITransformRuntimePort runtime,
        IPoseFileService poseFiles,
        ISkeletonService skeletons,
        IPluginLog log)
    {
        _bindings = bindings;
        _posing = posing;
        _runtime = runtime;
        _poseFiles = poseFiles;
        _skeletons = skeletons;
        _log = log;
    }

    /// <summary>
    /// The apply stage's resolution, on the arm tick: every reset and write
    /// of the name-keyed plan resolved against the LIVE slot skeletons, every
    /// affected target captured into <paramref name="import"/>'s before-states,
    /// and the model transform's desired value. Nothing is mutated. Returns the
    /// refusal, or null when the import may proceed.
    /// </summary>
    public string? ResolveApply(
        PendingPoseImport import,
        IActor actor,
        PoseImportPlan plan,
        bool expression,
        out List<(IBone Bone, TransformTargetId Target)> resetBones,
        out Dictionary<PoseSlot, ISkeleton> skeletons,
        out (TransformTargetId Target, PoseTransform Desired)? model)
    {
        var planActorId = import.TargetActorId;
        resetBones = new List<(IBone Bone, TransformTargetId Target)>(plan.Resets.Count);
        skeletons = new Dictionary<PoseSlot, ISkeleton>();
        model = null;

        // Resolve and capture EVERYTHING before mutating anything, so a
        // stale target fails synchronously with nothing to roll back. The
        // plan is name-keyed (issue #78): every bone resolves against the
        // LIVE slot skeletons on THIS tick, so nothing planned at the arm
        // tick can have gone stale — a name that does not resolve here is
        // a bone no live skeleton of this actor carries, refused by name.
        TransformPortResult captured;
        var slotBones = new Dictionary<
            PoseSlot,
            (ISkeleton Skeleton, Dictionary<(int Partial, string Bone), IBone> Bones)>();

        (ISkeleton Skeleton, Dictionary<(int Partial, string Bone), IBone> Bones)?
            ResolveSlot(PoseSlot slot)
        {
            if (slotBones.TryGetValue(slot, out var entry))
                return entry;
            if (_skeletons.GetSkeleton(actor, slot) is not { } skeleton)
                return null;
            entry = (skeleton, MapBones(skeleton));
            slotBones[slot] = entry;
            return entry;
        }

        foreach (var reset in plan.Resets)
        {
            if (ResolveSlot(reset.Slot) is not { } resetSlot ||
                !resetSlot.Bones.TryGetValue(
                    (reset.Partial, reset.Bone), out var bone))
                return $"Import target {reset.Bone} does not resolve on the live {reset.Slot} skeleton.";
            if (_bindings.GetBoneId(bone) is not { } resetId)
                return $"Import target {reset.Bone} could not be resolved.";
            if (resetId.Skeleton.Actor != planActorId)
                return "A reset target belongs to a different actor generation.";
            var target = TransformTargetId.ForBone(resetId);
            resetBones.Add((bone, target));
            if (!import.Before.ContainsKey(target))
            {
                captured = _runtime.Capture(target);
                if (!captured.Success || captured.State is not { } state)
                    return captured.Detail ?? $"Could not capture {target}.";
                import.Before[target] = state;
                import.Order.Add(target);
            }
            import.Resets.Add(target);
        }

        var slotMap = new Dictionary<PoseSlot, PoseImportSlot>();
        foreach (var write in plan.Writes)
        {
            if (ResolveSlot(write.Slot) is not { } writeSlot ||
                !writeSlot.Bones.TryGetValue(
                    (write.Partial, write.Bone), out var bone))
                return $"Import target {write.Bone} does not resolve on the live {write.Slot} skeleton.";
            if (_bindings.GetBoneId(bone) is not { } writeId)
                return $"Import target {write.Bone} could not be resolved.";
            if (writeId.Skeleton.Actor != planActorId)
                return "A write target belongs to a different actor generation.";
            var target = TransformTargetId.ForBone(writeId);
            if (!import.Before.ContainsKey(target))
            {
                captured = _runtime.Capture(target);
                if (!captured.Success || captured.State is not { } state)
                    return captured.Detail ?? $"Could not capture {target}.";
                import.Before[target] = state;
                import.Order.Add(target);
            }
            if (!slotMap.TryGetValue(write.Slot, out var slot))
            {
                slotMap[write.Slot] = slot = new PoseImportSlot
                {
                    Slot = write.Slot,
                    Writes = new Dictionary<(int, string),
                        (TransformTargetId, Transform, TransformComponents)>(),
                };
                import.Slots.Add(slot);
            }
            slot.Writes[(write.Partial, write.Bone)] =
                (target, write.File, write.Components);
            if (write.Slot == PoseSlot.Character)
            {
                import.WroteCharacter = true;
                if (write.Partial != 0)
                    import.WroteFacePartial = plan.ReconcileFace;
                // The head's pre-import absolute — a SEED only: this cached
                // value predates the settle tick's LocalTime rewind, and
                // the apply pass replaces it with the bone's own in-pass
                // basis (HeadRestore.PreImport has the space math). Only
                // instances the plan writes restore: a file without j_kao
                // never moved the head, so unlike Brio's blind
                // RemoveLastStack (which would eat a USER head stack in
                // that case) the restore stage simply skips.
                if (expression && write.Bone == "j_kao")
                    (import.HeadRestores ??= new()).Add(new PoseImportHeadRestore
                    {
                        Partial = write.Partial,
                        Bone = write.Bone,
                        Target = target,
                        PreImport = bone.LastRawTransform,
                    });
            }
        }

        if (plan.HasModelTransform)
        {
            // The model transform belongs to the admitted target actor by
            // construction — the plan states no actor of its own.
            var target = TransformTargetId.ForActor(planActorId);
            if (!import.Before.ContainsKey(target))
            {
                captured = _runtime.Capture(target);
                if (!captured.Success || captured.State is not { } state)
                    return captured.Detail ?? $"Could not capture {target}.";
                import.Before[target] = state;
                import.Order.Add(target);
            }
            model = (target, new PoseTransform(
                plan.ModelTransform.Position,
                plan.ModelTransform.Rotation,
                plan.ModelTransform.Scale));
        }

        if (import.Order.Count == 0)
            return "No target of this import could be bound.";

        if (import.Order.Any(target => ActorFor(target) != planActorId))
            return "An import target belongs to a different actor generation.";
        import.Targets = import.Order.ToArray();
        foreach (var (slot, entry) in slotBones)
            skeletons[slot] = entry.Skeleton;
        return null;
    }

    /// <summary>
    /// The flatten stage's batch: the whole live pose exported, reset scope
    /// and writes resolved against the same live slots, and any target the
    /// earlier stages did not touch captured before it changes. Returns the
    /// refusal; with no refusal, null <paramref name="slots"/> means there is
    /// nothing to flatten and the import completes as it stands.
    /// </summary>
    public string? BuildFlatten(
        PendingPoseImport import,
        IActor actor,
        out List<(IBone Bone, TransformTargetId Target)> resetBones,
        out List<PoseImportSlot>? slots,
        out Dictionary<PoseSlot, ISkeleton> skeletons)
    {
        resetBones = new List<(IBone Bone, TransformTargetId Target)>();
        slots = null;
        skeletons = new Dictionary<PoseSlot, ISkeleton>();

        var live = _skeletons.GetSkeletons(actor);
        if (live.Count == 0)
            return null;
        var exported = _poseFiles.CreatePoseFile(live);
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
        if (_poseFiles.BuildImportPlan(live, exported, options) is not { } plan)
            return null;

        // The plan was just built from these same live slots, so every name
        // resolves against them; the maps exist to turn names back into the
        // bones the capture and reset need on this tick.
        var slotBones = new Dictionary<
            PoseSlot,
            (ISkeleton Skeleton, Dictionary<(int Partial, string Bone), IBone> Bones)>();
        foreach (var slotSkeleton in live)
            slotBones[slotSkeleton.Slot] = (slotSkeleton, MapBones(slotSkeleton));

        // Mid-flight capture, the reconcile's pattern: any target the
        // flatten can touch that the earlier stages did not is captured
        // before it changes, so the one rollback covers every stage.
        foreach (var reset in plan.Resets)
        {
            if (!slotBones.TryGetValue(reset.Slot, out var resetSlot) ||
                !resetSlot.Bones.TryGetValue(
                    (reset.Partial, reset.Bone), out var bone) ||
                _bindings.GetBoneId(bone) is not { } id)
                return $"Flatten reset target {reset.Bone} could not be resolved.";
            if (id.Skeleton.Actor != import.TargetActorId)
                return "A flatten reset target changed actor generation.";
            var target = TransformTargetId.ForBone(id);
            if (!import.Before.ContainsKey(target))
            {
                var captured = _runtime.Capture(target);
                if (!captured.Success || captured.State is not { } state)
                    return captured.Detail ?? $"Could not capture {target}.";
                import.Before[target] = state;
                import.Order.Add(target);
            }
            resetBones.Add((bone, target));
            import.Resets.Add(target);
        }

        var slotMap = new Dictionary<PoseSlot, PoseImportSlot>();
        var flattenSlots = new List<PoseImportSlot>();
        foreach (var write in plan.Writes)
        {
            if (!slotBones.TryGetValue(write.Slot, out var writeSlot) ||
                !writeSlot.Bones.TryGetValue(
                    (write.Partial, write.Bone), out var bone) ||
                _bindings.GetBoneId(bone) is not { } id)
                return $"Flatten write target {write.Bone} could not be resolved.";
            if (id.Skeleton.Actor != import.TargetActorId)
                return "A flatten write target changed actor generation.";
            var target = TransformTargetId.ForBone(id);
            if (!import.Before.ContainsKey(target))
            {
                var captured = _runtime.Capture(target);
                if (!captured.Success || captured.State is not { } state)
                    return captured.Detail ?? $"Could not capture {target}.";
                import.Before[target] = state;
                import.Order.Add(target);
            }
            if (!slotMap.TryGetValue(write.Slot, out var slot))
            {
                slotMap[write.Slot] = slot = new PoseImportSlot
                {
                    Slot = write.Slot,
                    Writes = new Dictionary<(int, string),
                        (TransformTargetId, Transform, TransformComponents)>(),
                };
                flattenSlots.Add(slot);
            }
            slot.Writes[(write.Partial, write.Bone)] =
                (target, write.File, write.Components);
        }

        if (flattenSlots.Count == 0)
            return null;

        foreach (var (slot, entry) in slotBones)
            skeletons[slot] = entry.Skeleton;
        slots = flattenSlots;
        return null;
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
    /// <c>BeginReconcile</c>'s list says skip.
    /// </summary>
    public (PoseImportSlot Batch, ISkeleton Skeleton)? BuildReconcile(PendingPoseImport import)
    {
        if (!import.WroteFacePartial)
            return null;
        // The subtree is read from the LIVE Character skeleton on this tick
        // — its post-reparent LastRawTransform absolutes are the reconcile's
        // whole payload, so any carried instance would be exactly wrong.
        if (_bindings.Resolve(import.TargetActorId) is not
                { Success: true, Value: { } actor } ||
            _skeletons.GetSkeleton(actor) is not { } skeleton)
        {
            import.Failure ??=
                "The Character skeleton is no longer present for the face reconcile.";
            return null;
        }
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

        var writes = new Dictionary<(int, string),
            (TransformTargetId, Transform, TransformComponents)>(subtree.Count);
        foreach (var bone in subtree)
        {
            // An explicitly restored partial-root scale is reapplied by
            // attachment every frame. Reconciling that root as another edit
            // scales its children, then attachment overwrites only the root:
            // the duplicate's face grows by rootScale / bodyHeadScale.
            if (bone.IsPartialRoot && !bone.IsSkeletonRoot && bone.PartialRootScale.HasValue)
                continue;
            // A subtree bone without a binding cannot be captured for
            // rollback, so it is not written either — Brio likewise only
            // re-applies what its name lookup finds.
            if (_bindings.GetBoneId(bone) is not { } id)
            {
                import.Failure ??= $"Reconcile target {bone.BoneName} could not be resolved.";
                return null;
            }
            if (id.Skeleton.Actor != import.TargetActorId)
            {
                import.Failure ??= "A reconcile target changed actor generation.";
                return null;
            }
            var target = TransformTargetId.ForBone(id);
            if (!import.Before.ContainsKey(target))
            {
                // Captured BEFORE the reconcile writes it. A bone the apply
                // phase never touched carries no stacks, so this mid-flight
                // capture equals its pre-import state and the one rollback
                // restores both phases.
                var captured = _runtime.Capture(target);
                if (!captured.Success || captured.State is not { } state)
                {
                    import.Failure ??= captured.Detail ?? $"Could not capture {target}.";
                    return null;
                }
                import.Before[target] = state;
                import.Order.Add(target);
            }
            writes[(bone.PartialId, bone.BoneName)] =
                (target, bone.LastRawTransform, TransformComponents.All);
        }

        if (writes.Count == 0)
            return null;
        RefreshTargets(import);
        return (
            new PoseImportSlot { Slot = PoseSlot.Character, Writes = writes },
            skeleton);
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
    public void ApplyBone(
        PendingPoseImport import,
        PoseImportSlot slot,
        IBone bone,
        BonePoseInfo poseInfo)
    {
        try
        {
            // This callback runs at the native boundary. It may only inspect
            // the active token; framework/session/binding validation belongs
            // to the deferred framework callback.
            if (!IsNativeLive(import))
                return;
            if (!slot.Writes.TryGetValue(
                    (bone.PartialId, bone.BoneName), out var entry))
                return;
            // The attachment owner restores this root, including its scale.
            // A file's same-named body-head value is not a second root edit.
            if (bone.IsPartialRoot && !bone.IsSkeletonRoot && bone.PartialRootScale.HasValue)
                return;

            // HeadRestore holds an in-pass raw basis, not a visible file
            // target. Every other stage carries post-reparent absolutes.
            var desired = import.Stage == PoseImportStage.HeadRestore
                ? entry.File : _posing.ToApplySpace(bone, entry.File);
            var basis = bone.LastRawTransform;

            // Expression imports: re-express this head instance's restore
            // target in the pass's own basis — anim(rewound) ⊕ the
            // pre-import stacks the reset left on the head — BEFORE the
            // file's head lands. The Begin-time seed is pre-rewind;
            // restoring it re-baked the pause-frame-vs-LocalTime-0 offset
            // into the head on every apply (PoseImportHeadRestore.PreImport). Head
            // restores only ever name Character-slot bones, so the slot
            // gate keeps a same-named auxiliary bone from re-seeding them.
            if (import.Stage == PoseImportStage.Apply &&
                slot.Slot == PoseSlot.Character &&
                import.HeadRestores is { } restores)
            {
                foreach (var restore in restores)
                {
                    if (restore.Partial == bone.PartialId &&
                        string.Equals(
                            restore.Bone, bone.BoneName,
                            StringComparison.Ordinal))
                    {
                        restore.PreImport = basis;
                        break;
                    }
                }
            }

            var delta = BonePoseInfo.FilterDelta(
                BonePoseInfo.Diff(desired, basis), entry.Components);
            if (TransformMath.IsApproximatelyIdentityDelta(delta))
                return;

            // Propagation stays All (Brio PoseImporter.cs:35, 3rd argument):
            // an imported bone carries its children with it exactly as the
            // pose it replaced did. The mask applies to the delta only.
            // forceNewStack matches Brio's PoseImporter (every call passes
            // true): each import write is its OWN stack entry, which is what
            // makes the expression head restore's RemoveLastStack pop
            // exactly the phase-1 head write and nothing else.
            // The posing provider owns the mutation; mark before entering it
            // so a partial write followed by a throw still rolls back.
            import.MutationStarted = true;
            if (poseInfo.Apply(
                    desired, basis,
                    TransformComponents.All,
                    entry.Components,
                    forceNewStack: true, drivesIk: false) == null)
            {
                // One degenerate bone (a zero-scaled prop helper such as
                // nf_handprop_k_l on an actor without a prop) is not the
                // pose: it is skipped and named, the rest lands. Failing the
                // whole import here left every duplicate of such an actor
                // idling (2026-09-02).
                _log.Warning(
                    $"Pose import: {bone.BoneName} produced a non-finite delta and was skipped.");
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

    /// <summary>The native boundary's only liveness read: the import's
    /// interlocked invalidation token.</summary>
    internal static bool IsNativeLive(PendingPoseImport import) =>
        !import.Invalidation.IsInvalidated;

    internal static ActorId ActorFor(TransformTargetId target) =>
        target.Actor ?? target.Bone?.Skeleton.Actor ?? default;

    internal static bool ApproximatelySame(PoseTransform left, PoseTransform right)
    {
        const float tolerance = TransformMath.ApproximateSameTolerance;
        return Vector3.DistanceSquared(left.Position, right.Position) < tolerance * tolerance &&
               Vector3.DistanceSquared(left.Scale, right.Scale) < tolerance * tolerance &&
               1f - MathF.Abs(Quaternion.Dot(
                   TransformMath.NormalizeRotation(left.Rotation),
                   TransformMath.NormalizeRotation(right.Rotation))) < tolerance;
    }

    internal static void RefreshTargets(PendingPoseImport import) =>
        import.Targets = import.Order.ToArray();

    /// <summary>The (partial, name)→bone map one stage resolves through,
    /// built from the live skeleton on that stage's own tick and discarded
    /// with it. Virtual bones carry no stacks and have no stable binding,
    /// so they are not addressable by an import.</summary>
    internal static Dictionary<(int Partial, string Bone), IBone> MapBones(
        ISkeleton skeleton)
    {
        var map = new Dictionary<(int Partial, string Bone), IBone>(
            skeleton.Bones.Count);
        foreach (var bone in skeleton.Bones)
        {
            if (bone is not VirtualBone)
                map[(bone.PartialId, bone.BoneName)] = bone;
        }
        return map;
    }
}
