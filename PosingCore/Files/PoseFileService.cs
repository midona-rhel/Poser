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
        // Expression imports apply EVERY component regardless of the
        // Translation/Rotation/Scale toggles — Brio's ExpressionOptions is
        // TransformComponents.All (PosingService.cs:77) while its toggles
        // feed only the body path. Dawntrail faces are posed through bone
        // POSITIONS, so the rotation-only default landed face imports wrong
        // (user 2026-08-08). The mask limit still governs: a .cmp carries no
        // positions to force.
        if (options.AsExpression)
            components = TransformComponents.All & maskLimit;
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
                var slotInstances = InstancesByName(slotSkeleton);
                foreach (var (boneName, boneData) in collection)
                {
                    if (IsThrowBone(boneName) ||
                        !slotInstances.TryGetValue(boneName, out var bones))
                        continue;
                    bool applied = false;
                    foreach (var bone in bones)
                    {
                        if (!PassesBoneFilter(bone, options))
                            continue;
                        PlanBoneTransform(plan, bone, boneData, components);
                        applied = true;
                    }
                    if (applied)
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

        // The face reconcile is NOT plan-time: file data for non-zero
        // partials is post-reparent while the apply pass's basis is
        // pre-reparent, and only the application engine can re-export the
        // converged subtree between passes — PoseImportCapture's reconcile
        // stage (Brio PosingCapability.cs:249-250, :316-317, :370-401).

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
            if (IsExcludedByCategories(bone.BoneName, options))
                return false;
            if (options.AsExpression)
                // The reset matches the apply scope MINUS the head: the
                // head's pre-import stacks must survive because the file's
                // head lands only transiently and the engine's head-restore
                // stage reverts to exactly those.
                return IsExpressionScopeBone(bone.BoneName) &&
                       bone.BoneName != "j_kao";
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

        var instances = InstancesByName(skeleton);
        foreach (var (rawBoneName, boneData) in poseFile.Bones)
        {
            // old Anamnesis files carry legacy bone names on plain .pose
            // too — run the conversion table whenever the raw name misses
            var boneName = rawBoneName;
            if (!instances.TryGetValue(boneName, out var bones))
            {
                var modern = AnamnesisBoneNameConverter.ToGame(rawBoneName);
                if (modern == null || !instances.TryGetValue(modern, out bones))
                    continue;
                boneName = modern;
            }
            if (IsThrowBone(boneName))
                continue;

            // Scope decides FIRST; the pre-Dawntrail protection can only
            // modify how an accepted bone applies, never smuggle an
            // out-of-scope bone past AsExpression/ApplyFace/the filter.

            // Expression import: Brio's ExpressionOptions scope — head,
            // ears, hair, face, eyes, lips, jaw (PosingService.cs:77-86) —
            // INCLUDING j_kao. The file's face was authored around the
            // file's OWN head, so the head must move to the file's space for
            // the face deltas to be face-local; the import engine's head
            // restore stage then reverts the head, Brio's expressionPhase2
            // (PosingCapability.cs:238-247, PoseImporter.cs:11-26). The
            // earlier "skip j_kao, single phase" shortcut silently baked the
            // exporter-vs-target head offset into every face position delta
            // and flung imported faces (user 2026-08-08).
            if (options.AsExpression && !IsExpressionScopeBone(boneName))
                continue;

            // The bone-filter menu's exclusions (Brio's category filter,
            // BoneFilter.IsBoneValidUncached): disabled category prefixes
            // never apply; with "Other" off, neither does anything no
            // category claims.
            if (IsExcludedByCategories(boneName, options))
                continue;
            // Filter by face bones if needed
            else if (!options.ApplyFace && IsFaceBone(boneName))
                continue;

            // Pre-DT protection folds into the delta mask: a protected face
            // bone's position component contributes nothing.
            var boneComponents = preDtFace && IsFaceBone(boneName)
                ? components & ~TransformComponents.Position
                : components;

            bool applied = false;
            foreach (var bone in bones)
            {
                // Selective import (Ktisis/Anamnesis parity): only filtered
                // bones (+ descendants when requested)
                if (!PassesBoneFilter(bone, options))
                    continue;
                PlanBoneTransform(plan, bone, boneData, boneComponents);
                applied = true;
            }
            if (applied)
                plan.FileBoneCount++;
        }
    }

    /// <summary>
    /// Every live instance of each bone name across the skeleton's partials.
    /// Brio applies the file bone to EVERY partial's instance — its per-bone
    /// transitive action re-runs the name lookup at each visited bone
    /// (PoseImporter.cs:33) — so j_kao lands on the body head AND on the
    /// face/hair partial roots. A single-instance lookup leaves the extra
    /// partial roots at their animated rotation, and the reparented face
    /// assembles against a root the import never turned.
    /// </summary>
    private static Dictionary<string, List<IBone>> InstancesByName(ISkeleton skeleton)
    {
        var byName = new Dictionary<string, List<IBone>>(StringComparer.Ordinal);
        foreach (var bone in skeleton.Bones)
        {
            if (bone is VirtualBone)
                continue;
            if (!byName.TryGetValue(bone.BoneName, out var list))
                byName[bone.BoneName] = list = new List<IBone>(1);
            list.Add(bone);
        }
        return byName;
    }

    /// <summary>Brio hard-excludes n_throw from every import (BoneFilter.cs:37-38,
    /// the constructor's default excluded prefix): the throw bone is animation
    /// plumbing, and a file value on it drags the whole model.</summary>
    private static bool IsThrowBone(string boneName) =>
        boneName.StartsWith("n_throw", StringComparison.Ordinal);

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
        // The FILE transform verbatim: file bones are LastRawTransform
        // snapshots taken AFTER the update phase's post-reparent refresh
        // (CreatePoseFile above; Brio SkeletonService.cs:243). The delta
        // basis is the apply pass's own just-refreshed bone.LastRawTransform
        // (Brio PoseImporter.cs:35) — a basis read here, outside the pass,
        // would predate the parents' deltas the same pass propagates. For
        // partial-0 bones the two spaces coincide; for non-zero partials the
        // file data is post-reparent while the pass basis is pre-reparent,
        // and PoseImportCapture's reconcile stage is what converges the face
        // after that wrong-space first diff (Brio PosingCapability.cs:
        // 316-317, :370-401). Excluded components are masked on the DELTA
        // (Brio PoseInfo.cs:108), so the bone's live values stay put without
        // being re-asserted.
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

    /// <summary>
    /// Brio's Smart Import file classifier (FileUIHelpers.ResolveSmartImport:
    /// 355-386): a file is an expression when it carries one of the
    /// expression tags, or when every Character bone it names is a face bone
    /// — the head included, per Brio's own smart-import predicate (:405-419),
    /// which is WIDER than the import scope's <see cref="IsFaceBone"/>. Such
    /// a file can never land through the body path (Dawntrail faces are
    /// posed through bone POSITIONS the body path masks), so surfaces
    /// without an import-type control route it as an expression.
    /// </summary>
    public static bool IsExpressionOnlyPose(PoseFile poseFile)
    {
        if (poseFile.Tags is { Count: > 0 } tags)
        {
            foreach (var tag in tags)
            {
                if (tag == null)
                    continue;
                // Brio :373 token list, Contains-matched.
                if (tag.Contains("expression", StringComparison.OrdinalIgnoreCase) ||
                    tag.Contains("facial expression", StringComparison.OrdinalIgnoreCase) ||
                    tag.Contains("facial-expression", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }

        if (poseFile.Bones.Count == 0)
            return false;
        foreach (var boneName in poseFile.Bones.Keys)
        {
            if (!IsSmartImportFaceBone(boneName))
                return false;
        }
        return true;
    }

    /// <summary>Brio's ExpressionOptions bone scope (PosingService.cs:77-86:
    /// the head, ears, hair, face, eyes, lips and jaw categories enabled):
    /// the import-scope face test plus j_kao (head), j_zer/n_ear_ (Viera and
    /// accessory ears) and j_ex_h/j_ex_met_va (ex-hair strands) —
    /// BoneCategories.json members <see cref="IsFaceBone"/> does not cover.</summary>
    internal static bool IsExpressionScopeBone(string boneName) =>
        boneName == "j_kao" ||
        IsFaceBone(boneName) ||
        boneName.StartsWith("j_zer", StringComparison.Ordinal) ||
        boneName.StartsWith("n_ear_", StringComparison.Ordinal) ||
        boneName.StartsWith("j_ex_h", StringComparison.Ordinal) ||
        boneName.StartsWith("j_ex_met_va", StringComparison.Ordinal);

    /// <summary>The bone-filter menu's verdict (Brio
    /// BoneFilter.IsBoneValidUncached, as an exclusion): a disabled
    /// category's prefix bans the bone; with the "Other" row off, so is
    /// anything no category claims.</summary>
    private static bool IsExcludedByCategories(
        string boneName, PoseImportOptions options)
    {
        if (options.ExcludedBonePrefixes is { Count: > 0 } excluded)
        {
            foreach (var prefix in excluded)
            {
                if (boneName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }
        if (options.ExcludeUncategorizedBones &&
            !ImportBoneCategories.IsCategorized(boneName))
            return true;
        return false;
    }

    /// <summary>The other half of Brio's Smart Import classifier
    /// (:382-386): a file whose Character bones include NO face bone is a
    /// body pose — smart routing keeps the face untouched for it.</summary>
    public static bool IsBodyOnlyPose(PoseFile poseFile)
    {
        if (poseFile.Bones.Count == 0)
            return false;
        foreach (var boneName in poseFile.Bones.Keys)
        {
            if (IsSmartImportFaceBone(boneName))
                return false;
        }
        return true;
    }

    /// <summary>Brio ResolveSmartImport's local IsFaceBone (:405-419):
    /// j_kao plus the j_f_/j_eye/j_may/j_ago/j_lip/j_bero prefixes.</summary>
    private static bool IsSmartImportFaceBone(string boneName) =>
        boneName.Equals("j_kao", StringComparison.OrdinalIgnoreCase) ||
        boneName.StartsWith("j_f_", StringComparison.OrdinalIgnoreCase) ||
        boneName.StartsWith("j_eye", StringComparison.OrdinalIgnoreCase) ||
        boneName.StartsWith("j_may", StringComparison.OrdinalIgnoreCase) ||
        boneName.StartsWith("j_ago", StringComparison.OrdinalIgnoreCase) ||
        boneName.StartsWith("j_lip", StringComparison.OrdinalIgnoreCase) ||
        boneName.StartsWith("j_bero", StringComparison.OrdinalIgnoreCase);

    public void Dispose()
    {
        // Nothing to dispose
    }
}
