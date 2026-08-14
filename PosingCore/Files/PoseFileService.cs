using System;
using System.Linq;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Plugin.Services;
using Poser.Core;
using Poser.Domain.Posing;
using Poser.Domain.Identity;
using Poser.Domain.Transforms;
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
    private readonly IActorSpawnService? _spawn;

    public PoseImportOptions DefaultImportOptions { get; } = PoseImportOptions.Default;

    /// <summary>The spawn service is optional plumbing for the Smart Import
    /// ModelId hint; without it exports simply carry 0, Brio's own default.</summary>
    public PoseFileService(
        IPluginLog log,
        IPosingService posingService,
        IActorSpawnService? spawn = null)
    {
        _log = log;
        _posingService = posingService;
        _spawn = spawn;
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

    public PoseFile CreatePoseFile(
        IReadOnlyList<ISkeleton> slots, Func<IBone, bool>? include = null)
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
                if (include != null && !include(bone))
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

            // Smart Import hint (Brio MetadataModal.cs:199-202): the actor's
            // current model id, so a creature pose re-imported through Brio
            // can redraw a human target first. Exports run on the framework
            // thread (PoseExportCapture.Complete via RunOnTick), which the
            // spawn service's read requires; off-thread callers get 0 —
            // Brio's own "no hint" default.
            if (_spawn != null)
                poseFile.ModelId = _spawn.GetModelCharaId(actor);
        }

        return poseFile;
    }

    public bool ExportPose(IReadOnlyList<ISkeleton> slots, string path)
    {
        try
        {
            var poseFile = CreatePoseFile(slots);
            var written = AtomicPoseFileStore.Default.Write(poseFile, path);
            if (written.Succeeded)
            {
                _log.Debug($"Exported pose ({slots.Count} slots) to {path}");
                return true;
            }
            _log.Error($"Failed to export pose: {written.Failure?.Detail ?? "unknown persistence failure"}");
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

            var read = AtomicPoseFileStore.Default.Read(path);
            if (!read.Succeeded || read.Pose == null)
            {
                _log.Error($"Failed to load pose file from {path}: {read.Failure?.Detail ?? "unknown persistence failure"}");
                return null;
            }
            var poseFile = read.Pose;

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

        // Character collection → Character slot only. A live selective
        // filter re-enters a disabled body scope: the direct bones bypass
        // the mode gates inside (Ktisis parity), so the call-site gate must
        // not eat them first.
        if (character != null && (options.ApplyBody || options.BoneFilter != null))
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
                if (collection.Count == 0)
                    continue;
                // A disabled slot still admits DIRECTLY selected bones —
                // the slot enables are mode gates like the type strip, and
                // Ktisis' explicit selection ignores them all; descendants
                // keep respecting them.
                bool slotGated = !SlotEnabled(slot, options);
                if (slotGated && options.BoneFilter == null)
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
                        var match = ClassifyBoneFilter(bone, options);
                        if (match == BoneFilterMatch.Excluded)
                            continue;
                        if (slotGated && match != BoneFilterMatch.Direct)
                            continue;
                        var writeComponents = AnchorMask(match, options, components);
                        if (writeComponents == TransformComponents.None)
                            continue;
                        PlanBoneTransform(plan, bone, boneData, writeComponents);
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
                Rotation = TransformMath.NormalizeRotation(current.Rotation * difference.Rotation),
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
            var match = ClassifyBoneFilter(bone, options);
            if (match == BoneFilterMatch.Excluded)
                return false;
            // A DIRECT selection resets under the same bypass it applies
            // under — reset touching less than the import writes would
            // leave a reset/apply mismatch. Only the AsExpression j_kao
            // carve survives the bypass: it is engine mechanics (the head
            // restore stage reverts to the pre-import stacks), not a gate.
            bool direct = match == BoneFilterMatch.Direct;
            if (options.AsExpression)
                // The reset matches the apply scope MINUS the head: the
                // head's pre-import stacks must survive because the file's
                // head lands only transiently and the engine's head-restore
                // stage reverts to exactly those.
                return (direct ||
                        (IsExpressionScopeBone(bone.BoneName) &&
                         !IsExcludedByCategories(bone.BoneName, options))) &&
                       bone.BoneName != "j_kao";
            if (direct)
                return true;
            if (IsExcludedByCategories(bone.BoneName, options))
                return false;
            if (!options.ApplyFace && IsFaceBone(bone.BoneName))
                return false;
            return options.ApplyBody;
        }

        if (character != null && poseFile.Bones.Count > 0 &&
            (options.ApplyBody || options.BoneFilter != null))
        {
            foreach (var bone in character.Bones.Where(InCharacterScope))
                plan.Resets.Add(bone);
        }

        if (options.AsExpression)
            return;
        foreach (var (slot, skeleton) in bySlot)
        {
            // Same gate as application: the collection must be present in
            // the file, and a disabled slot resets only DIRECTLY selected
            // bones — exactly the set the apply loop can write.
            if (slot == PoseSlot.Character ||
                CollectionFor(poseFile, slot) is not { Count: > 0 })
                continue;
            bool slotGated = !SlotEnabled(slot, options);
            if (slotGated && options.BoneFilter == null)
                continue;
            foreach (var bone in skeleton.Bones)
            {
                var match = ClassifyBoneFilter(bone, options);
                if (match == BoneFilterMatch.Excluded)
                    continue;
                if (slotGated && match != BoneFilterMatch.Direct)
                    continue;
                plan.Resets.Add(bone);
            }
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
            && !poseFile.Bones.ContainsKey(DawntrailFaceMarkerBone)
            && IsDawntrailSkeleton(skeleton);
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

            // The MODE gates, folded into one verdict because a DIRECTLY
            // selected bone bypasses all of them (Ktisis: ApplyToBones
            // applies the explicit selection with no partial-mode gating;
            // modes gate only descendant expansion). A selection the user
            // made bone by bone must never partially and silently drop
            // under a narrowed type strip or category filter.
            //
            // Expression import scope: Brio's ExpressionOptions — head,
            // ears, hair, face, eyes, lips, jaw (PosingService.cs:77-86) —
            // INCLUDING j_kao. The file's face was authored around the
            // file's OWN head, so the head must move to the file's space for
            // the face deltas to be face-local; the import engine's head
            // restore stage then reverts the head, Brio's expressionPhase2
            // (PosingCapability.cs:238-247, PoseImporter.cs:11-26). The
            // earlier "skip j_kao, single phase" shortcut silently baked the
            // exporter-vs-target head offset into every face position delta
            // and flung imported faces (user 2026-08-08).
            //
            // Category exclusions: the bone-filter menu (Brio's category
            // filter, BoneFilter.IsBoneValidUncached): disabled category
            // prefixes never apply; with "Other" off, neither does anything
            // no category claims. Then the face gate, then the call site's
            // own ApplyBody gate, which the filtered call re-enters.
            bool modeGated =
                (options.AsExpression && !IsExpressionScopeBone(boneName))
                || IsExcludedByCategories(boneName, options)
                || (!options.ApplyFace && IsFaceBone(boneName))
                || !options.ApplyBody;

            // Pre-DT protection folds into the delta mask: a protected face
            // bone's position component contributes nothing — for DIRECT
            // selections too: it is data protection, not a scope gate.
            var boneComponents = preDtFace && IsFaceBone(boneName)
                ? components & ~TransformComponents.Position
                : components;

            bool applied = false;
            foreach (var bone in bones)
            {
                // Selective import (Ktisis parity): direct selections apply
                // even mode-gated; descendants and the unfiltered walk
                // respect the gates.
                var match = ClassifyBoneFilter(bone, options);
                if (match == BoneFilterMatch.Excluded)
                    continue;
                if (modeGated && match != BoneFilterMatch.Direct)
                    continue;
                var writeComponents = AnchorMask(match, options, boneComponents);
                if (writeComponents == TransformComponents.None)
                    continue;
                PlanBoneTransform(plan, bone, boneData, writeComponents);
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

    /// <summary>How a bone relates to the selective-import filter. The
    /// distinction carries semantics (Ktisis PoseContainer.ApplyToBones vs
    /// Apply): a DIRECTLY selected bone applies regardless of the mode gates
    /// — type strip, category exclusions, the face gate, slot enables — while
    /// a bone reached only through descendant expansion still respects every
    /// one of them, exactly like Ktisis' modes gating expansion but never the
    /// explicit selection.</summary>
    private enum BoneFilterMatch
    {
        /// <summary>A filter is active and the bone is outside it.</summary>
        Excluded,
        /// <summary>The slot-qualified bone itself is in the set.</summary>
        Direct,
        /// <summary>Reached through a same-slot ancestor with descendants
        /// requested.</summary>
        Descendant,
        /// <summary>No filter — the ordinary full-scope import.</summary>
        Unfiltered,
    }

    /// <summary>
    /// Ktisis' "Anchor group positions" (PosingManager.ApplyPoseFile:254-265):
    /// after the selective apply, Ktisis restores the selection's pre-import
    /// POSITIONS — descendants included, GetSelectedBones(false,
    /// includeDescendants) — inside the same MultipleMemento. Poser plans the
    /// equivalent by withholding the position component from every filtered
    /// bone's write. Gated exactly like Ktisis (selective active AND a
    /// position component applying); a write masked to nothing is dropped, so
    /// an anchor that empties the whole plan surfaces as the existing typed
    /// "nothing applies" refusal instead of a silent no-op arm.
    ///
    /// <para>Anchoring by never-writing lands the SAME net pose as Ktisis'
    /// do-then-undo on the body partial, and in one history patch instead of
    /// two mementos. Ktisis' restore is relative (PoseContainer.cs:200-207):
    /// the bone's pre-import offset from its parent, carried by that parent's
    /// rigid motion. A masked bone reaches the identical place from the other
    /// side — ApplyBoneTransform reads model space with propagation already
    /// applied (BonePosingService.AccessBoneModelSpace(..., Propagate)), so
    /// the bone sits exactly where its ancestors' motion put it. Neither has
    /// an observable transient: Ktisis' two passes share one
    /// RunOnFrameworkThread tick, so the mask's advantage is the single patch,
    /// not the absence of a flicker.</para>
    ///
    /// <para>Two characterized divergences, neither an equivalence claim.
    /// FACE AND HAIR PARTIALS — Ktisis' TryGetRelativeParent
    /// (PoseContainer.cs:236-238) forces parentIndex 0 for partialIndex 1 or
    /// 2, so it anchors every face/hair bone to the PARTIAL ROOT (j_kao)
    /// rather than to the bone's own parent: a deep face bone does not swing
    /// with a rotating intermediate parent there. The mask leaves the bone
    /// riding its true parent (ClassifyBoneFilter walks bone.ParentBone), so
    /// the net poses genuinely differ — INTENTIONALLY. "Anchor" means a bone
    /// follows its own parent; Ktisis' behaviour is a by-product of the
    /// partial-root shortcut its selective rotation math uses, and this is the
    /// coherent reading, not a port gap. SCALE — Poser propagates with
    /// TransformComponents.All (PoseImportCapture, exact Brio parity,
    /// PoseImporter.cs:35) while Ktisis' Propagate carries pos/rot only
    /// (HavokPosing.cs:175-178), so a propagated scale can displace an
    /// anchored descendant where Ktisis would have restored it. Inherited from
    /// the Brio-vs-Ktisis propagation difference, not introduced here, and it
    /// is the one place the anchor's promise leaks — only while Scale is also
    /// importing.</para>
    /// </summary>
    private static TransformComponents AnchorMask(
        BoneFilterMatch match,
        PoseImportOptions options,
        TransformComponents components)
    {
        if ((match is BoneFilterMatch.Direct or BoneFilterMatch.Descendant)
            && options.AnchorSelectedPositions
            && components.HasFlag(TransformComponents.Position))
            return components & ~TransformComponents.Position;
        return components;
    }

    /// <summary>Selective-import filter: the slot-qualified bone itself, or
    /// any same-slot ancestor when descendants are requested.</summary>
    private static BoneFilterMatch ClassifyBoneFilter(
        Poser.Entities.IBone bone, PoseImportOptions options)
    {
        if (options.BoneFilter == null)
            return BoneFilterMatch.Unfiltered;
        var slot = bone.Skeleton.Slot;
        if (options.BoneFilter.Contains((slot, bone.BoneName)))
            return BoneFilterMatch.Direct;
        if (!options.FilterIncludesDescendants)
            return BoneFilterMatch.Excluded;

        var ancestor = bone.ParentBone;
        int guard = 0;
        while (ancestor != null && guard++ < 256)
        {
            if (options.BoneFilter.Contains((slot, ancestor.BoneName)))
                return BoneFilterMatch.Descendant;
            ancestor = ancestor.ParentBone;
        }
        return BoneFilterMatch.Excluded;
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
            Rotation = TransformMath.NormalizeRotation(boneData.Rotation),
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

    /// <summary>
    /// Brio's ExpressionOptions bone scope, read straight off the shipped
    /// catalog: the union of the head, ears, hair, face, eyes, lips and jaw
    /// category prefixes, which is precisely the set its DisableAll +
    /// EnableCategory run leaves allowed (PosingService.cs:77-86) evaluated
    /// through BoneFilter's prefix test (BoneFilter.cs:127).
    ///
    /// <para>Replaces a hand-rolled predicate that approximated the same set
    /// from <see cref="IsFaceBone"/>: it was too WIDE (every j_f_* name, so
    /// the legacy and ex rows Brio leaves disabled rode along, as did bare
    /// j_ago and j_hana) and too NARROW (j_kao matched only exactly, where
    /// Brio's entry is a prefix). The catalog is now the only statement of
    /// this scope.</para>
    /// </summary>
    internal static bool IsExpressionScopeBone(string boneName) =>
        ImportBoneCategories.IsInCategories(
            boneName, ImportBoneCategories.ExpressionCategories);

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
    /// (:382-386): a file carrying one of the body-only TAGS (Brio's :374
    /// token list, Contains-matched exactly as the expression list is), or
    /// one whose Character bones include NO face bone, is a body pose —
    /// smart routing keeps the face untouched for it. The tag decides on its
    /// own, before the bones are looked at, because Brio's <c>bodyOnlyTag</c>
    /// is the first half of an <c>||</c>.</summary>
    public static bool IsBodyOnlyPose(PoseFile poseFile)
    {
        if (poseFile.Tags is { Count: > 0 } tags)
        {
            foreach (var tag in tags)
            {
                if (tag == null)
                    continue;
                if (tag.Contains("body-only", StringComparison.OrdinalIgnoreCase) ||
                    tag.Contains("body_only", StringComparison.OrdinalIgnoreCase) ||
                    tag.Contains("bodyonly", StringComparison.OrdinalIgnoreCase) ||
                    tag.Contains("body only", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }

        if (poseFile.Bones.Count == 0)
            return false;
        foreach (var boneName in poseFile.Bones.Keys)
        {
            if (IsSmartImportFaceBone(boneName))
                return false;
        }
        return true;
    }

    /// <summary>The tongue bone Dawntrail's face rework added — Brio's own
    /// Dawntrail marker on both sides of its expression gate: the file half
    /// (FileUIHelpers.cs:361) and the actor half
    /// (SkeletonPosingCapability.cs:229 <c>CharacterIsDawntrail</c>).</summary>
    public const string DawntrailFaceMarkerBone = "j_f_bero_01";

    /// <summary>Brio's expression gate, FILE half (FileUIHelpers.cs:392):
    /// the pose either names the Dawntrail tongue bone or carries a
    /// "dawntrail"/"dt" tag.</summary>
    public static bool IsLikelyDawntrailPose(PoseFile poseFile)
    {
        foreach (var boneName in poseFile.Bones.Keys)
        {
            if (boneName.Equals(
                    DawntrailFaceMarkerBone, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        if (poseFile.Tags is { Count: > 0 } tags)
        {
            foreach (var tag in tags)
            {
                if (tag == null)
                    continue;
                if (tag.Contains("dawntrail", StringComparison.OrdinalIgnoreCase) ||
                    tag.Contains("dt", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }
        return false;
    }

    /// <summary>Brio's expression gate, ACTOR half
    /// (SkeletonPosingCapability.cs:229): the live skeleton has the
    /// Dawntrail tongue bone.</summary>
    public static bool IsDawntrailSkeleton(ISkeleton skeleton) =>
        skeleton.GetBone(DawntrailFaceMarkerBone) != null;

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
