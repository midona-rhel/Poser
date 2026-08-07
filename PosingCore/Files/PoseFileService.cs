using System;
using System.Linq;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Plugin.Services;
using Poser.Core;
using Poser.Domain.Identity;
using Poser.Entities;
using Poser.Services;

namespace Poser.Files;

/// <summary>
/// Brio-compatible .pose import/export over an actor's slot skeleton set.
/// Each slot maps to exactly one file collection; unknown or unavailable
/// slots are skipped and reported — never redirected by bone name.
/// </summary>
public class PoseFileService : IPoseFileService
{
    private readonly IPluginLog _log;
    private readonly IPosingService _posingService;

    public PoseImportOptions DefaultImportOptions { get; } = PoseImportOptions.Default;

    public PoseFileService(
        IPluginLog log,
        IPosingService posingService)
    {
        _log = log;
        _posingService = posingService;
    }

    private static Dictionary<string, PoseFile.BoneData>? CollectionFor(
        PoseFile poseFile,
        PoseSlot slot) => slot switch
    {
        PoseSlot.Character => poseFile.Bones,
        PoseSlot.MainHand => poseFile.MainHand,
        PoseSlot.OffHand => poseFile.OffHand,
        PoseSlot.Prop => poseFile.Prop,
        PoseSlot.Ornament => poseFile.Ornament,
        _ => null,
    };

    private static bool SlotEnabled(PoseSlot slot, PoseImportOptions options) => slot switch
    {
        PoseSlot.MainHand => options.ApplyMainHand,
        PoseSlot.OffHand => options.ApplyOffHand,
        PoseSlot.Prop => options.ApplyProp,
        PoseSlot.Ornament => options.ApplyOrnament,
        _ => false,
    };

    public PoseFile CreatePoseFile(IReadOnlyList<ISkeleton> slots)
    {
        var poseFile = new PoseFile();
        IActor? actor = null;

        // Brio parity (SkeletonPosingCapability.ExportSkeletonPose): every
        // partial's bones go into the slot's matching collection as absolute
        // model-space snapshots (LastRawTransform). Partial roots are skipped
        // except the skeleton root.
        foreach (var skeleton in slots)
        {
            actor ??= skeleton.Actor;
            var collection = CollectionFor(poseFile, skeleton.Slot);
            if (collection == null)
                continue;
            foreach (var bone in skeleton.Bones)
            {
                if (bone.IsPartialRoot && !bone.IsSkeletonRoot)
                    continue;

                collection[bone.BoneName] = bone.LastRawTransform;
            }
        }

        // Brio parity (ModelPosingCapability.ExportModelPose): the OWNING
        // ACTOR's transform, written once regardless of slot count.
        if (actor != null)
        {
            var effective = _posingService.GetEffectiveTransform(actor);
            var original = _posingService.GetOriginalTransform(actor);
            poseFile.ModelDifference = effective.CalculateDiff(original);
            poseFile.ModelAbsoluteValues = effective;
            poseFile.Position = effective.Position;
            poseFile.Rotation = effective.Rotation;
            poseFile.Scale = effective.Scale;
        }

        return poseFile;
    }

    public bool ExportPose(IReadOnlyList<ISkeleton> slots, string path)
    {
        try
        {
            var poseFile = CreatePoseFile(slots);
            if (poseFile.Save(path))
            {
                _log.Debug($"Exported pose ({slots.Count} slots) to {path}");
                return true;
            }
            return false;
        }
        catch (Exception ex)
        {
            _log.Error($"Failed to export pose: {ex.Message}");
            return false;
        }
    }

    public PoseImportPlan? BuildImportPlan(IReadOnlyList<ISkeleton> slots, string path, PoseImportOptions? options = null)
    {
        try
        {
            // Legacy CMTool .cmp: hex-encoded rotations/scales, NO positions —
            // convert to a PoseFile and drop Position from the delta mask so
            // nothing zeroes. .cmp is Character-only by format.
            if (path.EndsWith(".cmp", StringComparison.OrdinalIgnoreCase))
            {
                var cmp = CMToolPoseFile.Load(path);
                if (cmp == null)
                {
                    _log.Error($"Failed to load CMTool pose file from {path}");
                    return null;
                }

                return BuildImportPlan(
                    slots,
                    cmp.Upgrade(),
                    options,
                    TransformComponents.Rotation | TransformComponents.Scale);
            }

            var poseFile = PoseFile.Load(path);
            if (poseFile == null)
            {
                _log.Error($"Failed to load pose file from {path}");
                return null;
            }

            // Sanitize bone names for Anamnesis compatibility
            poseFile.SanitizeBoneNames();

            return BuildImportPlan(slots, poseFile, options);
        }
        catch (Exception ex)
        {
            _log.Error($"Failed to import pose: {ex.Message}");
            return null;
        }
    }

    public PoseImportPlan BuildImportPlan(IReadOnlyList<ISkeleton> slots, PoseFile poseFile, PoseImportOptions? options = null) =>
        BuildImportPlan(slots, poseFile, options, TransformComponents.All);

    private PoseImportPlan BuildImportPlan(
        IReadOnlyList<ISkeleton> slots,
        PoseFile poseFile,
        PoseImportOptions? options,
        TransformComponents maskLimit)
    {
        options ??= DefaultImportOptions;
        // Component selection is a DELTA mask (Brio PoseImporter.cs:35, the
        // 4th Apply argument), applied inside the apply pass — never an
        // absolute-write emulation against a stale basis.
        var components = ComponentMask(options) & maskLimit;
        var plan = new PoseImportPlan();

        var bySlot = slots
            .Where(s => s.Slot != PoseSlot.Unknown)
            .ToDictionary(s => s.Slot);
        bySlot.TryGetValue(PoseSlot.Character, out var character);

        // Brio parity: import does NOT wipe existing modifications (Brio's
        // interactive import passes reset: false). Reset only on explicit
        // request and only within the chosen scope.
        if (options.ResetBeforeImport)
            PlanResetScope(plan, bySlot, character, poseFile, options);

        // Character collection → Character slot only.
        if (options.ApplyBody && character != null)
            PlanCharacterCollection(plan, character, poseFile, options, components);

        // Each auxiliary collection imports only into its matching live
        // slot; a missing slot is reported, never redirected by name.
        if (!options.AsExpression)
        {
            foreach (var slot in new[]
                     {
                         PoseSlot.MainHand,
                         PoseSlot.OffHand,
                         PoseSlot.Prop,
                         PoseSlot.Ornament,
                     })
            {
                var collection = CollectionFor(poseFile, slot)!;
                if (collection.Count == 0 || !SlotEnabled(slot, options))
                    continue;
                if (!bySlot.TryGetValue(slot, out var slotSkeleton))
                {
                    _log.Info($"Pose import: {slot} collection skipped — slot not present on this actor.");
                    continue;
                }
                foreach (var (boneName, boneData) in collection)
                {
                    var bone = slotSkeleton.GetBone(boneName);
                    if (bone == null || !PassesBoneFilter(bone, options))
                        continue;
                    PlanBoneTransform(plan, bone, boneData, components);
                    plan.FileBoneCount++;
                }
            }
        }

        // Brio parity (ModelPosingCapability.ImportModelPose, non-scene
        // path): current actor transform += ModelDifference, applied ONCE
        // to the owning actor. Reset-before-import bases the sum on the
        // original transform, exactly what clearing-then-reading produced.
        if (options.ApplyModelTransform && !options.AsExpression &&
            slots.Count > 0)
        {
            var actor = slots[0].Actor;
            var current = options.ResetBeforeImport
                ? _posingService.GetOriginalTransform(actor)
                : _posingService.GetEffectiveTransform(actor);
            Transform difference = poseFile.ModelDifference;
            plan.ModelActor = actor;
            plan.ModelTransform = new Transform
            {
                Position = current.Position + difference.Position,
                Rotation = Quaternion.Normalize(current.Rotation * difference.Rotation),
                Scale = current.Scale + difference.Scale
            };
        }

        // No face-reconcile pass: within one atomic same-frame edit the
        // pre-import raw transforms it would re-apply are exactly the
        // near-identity no-op the delta rejection filters, while the
        // stack restore hidden inside a duplicate absolute write would
        // erase the file's own facial edits. Every target gets exactly
        // one deterministic final state.

        return plan;
    }

    /// <summary>Reset-before-import touches EXACTLY what the importer could
    /// apply, bone by bone: Expression resets applicable face bones but
    /// never j_kao; Body (face off) resets only non-face bones; Full resets
    /// everything present in the file; Selected follows the slot-qualified
    /// filter. A slot resets ONLY under the same collection-present/enabled
    /// gate the application loop uses — a selected auxiliary bone is never
    /// erased when nothing from its slot can apply.</summary>
    private void PlanResetScope(
        PoseImportPlan plan,
        IReadOnlyDictionary<PoseSlot, ISkeleton> bySlot,
        ISkeleton? character,
        PoseFile poseFile,
        PoseImportOptions options)
    {
        bool InCharacterScope(IBone bone)
        {
            if (!PassesBoneFilter(bone, options))
                return false;
            if (options.AsExpression)
                return IsFaceBone(bone.BoneName) && bone.BoneName != "j_kao";
            if (!options.ApplyFace && IsFaceBone(bone.BoneName))
                return false;
            return true;
        }

        if (options.ApplyBody && character != null && poseFile.Bones.Count > 0)
        {
            foreach (var bone in character.Bones.Where(InCharacterScope))
                plan.Resets.Add(bone);
        }

        if (options.AsExpression)
            return;
        foreach (var (slot, skeleton) in bySlot)
        {
            // Same gate as application: enabled AND its collection is
            // actually present in the file.
            if (slot == PoseSlot.Character ||
                !SlotEnabled(slot, options) ||
                CollectionFor(poseFile, slot) is not { Count: > 0 })
                continue;
            foreach (var bone in skeleton.Bones.Where(bone => PassesBoneFilter(bone, options)))
                plan.Resets.Add(bone);
        }
    }

    private void PlanCharacterCollection(
        PoseImportPlan plan,
        ISkeleton skeleton,
        PoseFile poseFile,
        PoseImportOptions options,
        TransformComponents components)
    {

        // Pre-Dawntrail face heuristic (Anamnesis): files with face bones
        // but no tongue bone predate the DT face rework — importing their
        // face POSITIONS onto a DT face deforms it. Strip positions for
        // face bones and log once.
        bool preDtFace = poseFile.Bones.Keys.Any(IsFaceBone)
            && !poseFile.Bones.ContainsKey("j_f_bero_01")
            && skeleton.GetBone("j_f_bero_01") != null;
        if (preDtFace)
            _log.Warning("PoseFileService: pre-Dawntrail face detected (no tongue bone) — face positions skipped to protect the DT face");

        foreach (var (rawBoneName, boneData) in poseFile.Bones)
        {
            // old Anamnesis files carry legacy bone names on plain .pose
            // too — run the conversion table whenever the raw name misses
            var boneName = rawBoneName;
            var bone = skeleton.GetBone(boneName);
            if (bone == null)
            {
                var modern = AnamnesisBoneNameConverter.ToGame(rawBoneName);
                if (modern != null)
                {
                    boneName = modern;
                    bone = skeleton.GetBone(boneName);
                }
            }
            if (bone == null)
                continue;

            // Scope decides FIRST; the pre-Dawntrail protection can only
            // modify how an accepted bone applies, never smuggle an
            // out-of-scope bone past AsExpression/ApplyFace/the filter.

            // Expression import (rewritten, single-phase): only face bones,
            // and NEVER the head — the file's face orientations land while
            // the posed head stays put. Equivalent end state to Brio's
            // apply-then-restore without its 4-tick resync hack.
            if (options.AsExpression)
            {
                if (boneName == "j_kao" || !IsFaceBone(boneName))
                    continue;
            }
            // Filter by face bones if needed
            else if (!options.ApplyFace && IsFaceBone(boneName))
                continue;

            // Selective import (Ktisis/Anamnesis parity): only filtered
            // bones (+ descendants when requested)
            if (!PassesBoneFilter(bone, options))
                continue;

            // Pre-DT protection folds into the delta mask: a protected face
            // bone's position component contributes nothing.
            var boneComponents = preDtFace && IsFaceBone(boneName)
                ? components & ~TransformComponents.Position
                : components;

            PlanBoneTransform(plan, bone, boneData, boneComponents);
            plan.FileBoneCount++;
        }
    }

    /// <summary>Selective-import filter: the slot-qualified bone itself, or
    /// any same-slot ancestor, is in the set.</summary>
    private static bool PassesBoneFilter(Poser.Entities.IBone bone, PoseImportOptions options)
    {
        if (options.BoneFilter == null)
            return true;
        var slot = bone.Skeleton.Slot;
        if (options.BoneFilter.Contains((slot, bone.BoneName)))
            return true;
        if (!options.FilterIncludesDescendants)
            return false;

        var ancestor = bone.ParentBone;
        int guard = 0;
        while (ancestor != null && guard++ < 256)
        {
            if (options.BoneFilter.Contains((slot, ancestor.BoneName)))
                return true;
            ancestor = ancestor.ParentBone;
        }
        return false;
    }

    private static void PlanBoneTransform(
        PoseImportPlan plan, IBone bone, PoseFile.BoneData boneData, TransformComponents components)
    {
        // The FILE transform verbatim: file bones are absolute raw
        // (pre-reparent) snapshots whose only valid delta basis is the apply
        // pass's own just-refreshed bone.LastRawTransform (Brio
        // PoseImporter.cs:35) — a basis read here, outside the pass, would
        // predate the parents' deltas the same pass propagates. Excluded
        // components are masked on the DELTA (Brio PoseInfo.cs:108), so the
        // bone's live values stay put without being re-asserted.
        plan.Writes.Add((bone, new Transform
        {
            Position = boneData.Position,
            Rotation = boneData.Rotation,
            Scale = boneData.Scale
        }, components));
    }

    /// <summary>Brio PoseWindow's TransformComponents assembly from the three
    /// toggles — the options' component switches as one delta mask.</summary>
    private static TransformComponents ComponentMask(PoseImportOptions options)
    {
        var mask = TransformComponents.None;
        if (options.ApplyPosition)
            mask |= TransformComponents.Position;
        if (options.ApplyRotation)
            mask |= TransformComponents.Rotation;
        if (options.ApplyScale)
            mask |= TransformComponents.Scale;
        return mask;
    }

    private static bool IsFaceBone(string boneName)
    {
        // Face bone detection based on common naming patterns
        return boneName.StartsWith("j_f_") ||    // Face bones
               boneName.StartsWith("j_ago") ||   // Jaw
               boneName.StartsWith("j_hana") ||  // Nose
               boneName.StartsWith("j_mimi") ||  // Ears
               boneName.StartsWith("j_kami") ||  // Hair
               boneName.Contains("_eye") ||
               boneName.Contains("_mayu") ||     // Eyebrows
               boneName.Contains("_hoho") ||     // Cheeks
               boneName.Contains("_kuti");       // Mouth
    }

    public void Dispose()
    {
        // Nothing to dispose
    }
}
