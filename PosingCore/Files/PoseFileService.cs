using System;
using System.Linq;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Plugin.Services;
using Poser.Core;
using Poser.Entities;
using Poser.Services;

namespace Poser.Files;

/// <summary>
/// Service for importing and exporting Brio-compatible pose files.
/// </summary>
public class PoseFileService : IPoseFileService
{
    private readonly IPluginLog _log;
    private readonly IBonePosingService _bonePosingService;
    private readonly IPosingService _posingService;

    public event Action<ISkeleton>? OnPoseImported;
    public event Action<ISkeleton, string>? OnPoseExported;

    public PoseImportOptions DefaultImportOptions { get; } = PoseImportOptions.Default;

    public PoseFileService(
        IPluginLog log,
        IBonePosingService bonePosingService,
        IPosingService posingService)
    {
        _log = log;
        _bonePosingService = bonePosingService;
        _posingService = posingService;
    }

    public PoseFile CreatePoseFile(ISkeleton skeleton)
    {
        var poseFile = new PoseFile();

        // Brio parity (SkeletonPosingCapability.ExportSkeletonPose): every partial's bones
        // (body partial 0 AND face/accessory partials 1+) go into the same Bones dictionary
        // as absolute model-space snapshots (LastRawTransform, the pre-reparent value).
        // Partial roots are skipped except the skeleton root, so a partial's attach bone
        // does not overwrite its partial-0 original.
        foreach (var bone in skeleton.Bones)
        {
            if (bone.IsPartialRoot && !bone.IsSkeletonRoot)
                continue;

            poseFile.Bones[bone.BoneName] = bone.LastRawTransform;
        }

        // Brio parity (ModelPosingCapability.ExportModelPose): actor transform as both a
        // difference vs the game-controlled transform and an absolute snapshot, plus the
        // legacy root-level copy for other pose tools.
        var actor = skeleton.Actor;
        var effective = _posingService.GetEffectiveTransform(actor);
        var original = _posingService.GetOriginalTransform(actor);
        poseFile.ModelDifference = effective.CalculateDiff(original);
        poseFile.ModelAbsoluteValues = effective;
        poseFile.Position = effective.Position;
        poseFile.Rotation = effective.Rotation;
        poseFile.Scale = effective.Scale;

        return poseFile;
    }

    public bool ExportPose(ISkeleton skeleton, string path)
    {
        try
        {
            var poseFile = CreatePoseFile(skeleton);
            if (poseFile.Save(path))
            {
                _log.Debug($"Exported pose to {path}");
                OnPoseExported?.Invoke(skeleton, path);
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

    public bool ImportPose(ISkeleton skeleton, string path, PoseImportOptions? options = null)
    {
        try
        {
            // Legacy CMTool .cmp: hex-encoded rotations/scales, NO positions —
            // convert to a PoseFile and force ApplyPosition off so nothing zeroes.
            if (path.EndsWith(".cmp", StringComparison.OrdinalIgnoreCase))
            {
                var cmp = CMToolPoseFile.Load(path);
                if (cmp == null)
                {
                    _log.Error($"Failed to load CMTool pose file from {path}");
                    return false;
                }

                var upgraded = cmp.Upgrade();
                var cmpOptions = (options ?? DefaultImportOptions).Clone();
                cmpOptions.ApplyPosition = false;
                return ImportPose(skeleton, upgraded, cmpOptions);
            }

            var poseFile = PoseFile.Load(path);
            if (poseFile == null)
            {
                _log.Error($"Failed to load pose file from {path}");
                return false;
            }

            // Sanitize bone names for Anamnesis compatibility
            poseFile.SanitizeBoneNames();

            return ImportPose(skeleton, poseFile, options);
        }
        catch (Exception ex)
        {
            _log.Error($"Failed to import pose: {ex.Message}");
            return false;
        }
    }

    public bool ImportPose(ISkeleton skeleton, PoseFile poseFile, PoseImportOptions? options = null)
    {
        options ??= DefaultImportOptions;

        try
        {
            // Brio parity: import does NOT wipe existing modifications (Brio's interactive
            // import path passes reset: false). File bones are absolute targets, so a
            // full-skeleton file fully determines the pose anyway; narrow imports (e.g.
            // face only) must not destroy body edits. Reset only on explicit request.
            if (options.ResetBeforeImport)
            {
                if (options.BoneFilter == null)
                {
                    _bonePosingService.ResetSkeleton(skeleton);
                }
                else
                {
                    foreach (var bone in skeleton.Bones.Where(bone => PassesBoneFilter(bone, options)))
                        _bonePosingService.ResetBone(bone);
                }
            }

            int bonesApplied = 0;

            // Apply body bones
            if (options.ApplyBody)
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

                    if (preDtFace && IsFaceBone(boneName) && options.ApplyPosition)
                    {
                        var stripped = options.Clone();
                        stripped.ApplyPosition = false;
                        ApplyBoneTransform(bone, boneData, stripped);
                        bonesApplied++;
                        continue;
                    }

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

                    ApplyBoneTransform(bone, boneData, options);
                    bonesApplied++;
                }
            }

            // Apply weapon bones
            if (options.ApplyMainHand && !options.AsExpression)
            {
                foreach (var (boneName, boneData) in poseFile.MainHand)
                {
                    var bone = skeleton.GetBone(boneName);
                    if (bone != null)
                    {
                        ApplyBoneTransform(bone, boneData, options);
                        bonesApplied++;
                    }
                }
            }

            if (options.ApplyOffHand && !options.AsExpression)
            {
                foreach (var (boneName, boneData) in poseFile.OffHand)
                {
                    var bone = skeleton.GetBone(boneName);
                    if (bone != null)
                    {
                        ApplyBoneTransform(bone, boneData, options);
                        bonesApplied++;
                    }
                }
            }

            // Brio parity (ModelPosingCapability.ImportModelPose, non-scene path):
            // current actor transform += ModelDifference. Reset first when requested,
            // mirroring Brio's `if (applyModelTransform && reset) ModelPosing.ResetTransform()`.
            if (options.ApplyModelTransform && !options.AsExpression)
            {
                var actor = skeleton.Actor;
                if (options.ResetBeforeImport)
                    _posingService.ClearTransformOverride(actor);

                var current = _posingService.GetEffectiveTransform(actor);
                Transform difference = poseFile.ModelDifference;
                _posingService.SetTransformOverride(actor, new Transform
                {
                    Position = current.Position + difference.Position,
                    Rotation = Quaternion.Normalize(current.Rotation * difference.Rotation),
                    Scale = current.Scale + difference.Scale
                });
            }

            // Re-anchor the face after body imports (rewrite of Brio's
            // ReconcileHead): re-apply the j_kao subtree's current raw transforms
            // as absolute targets so face stacks stay pinned to the moved head.
            // Skipped when IK is live anywhere — reconciling would fight the
            // solver (the exact failure Brio's own comment admits to).
            if (!options.AsExpression && options.ApplyFace && !_bonePosingService.HasEnabledIk(skeleton))
                ReconcileFace(skeleton);

            _log.Debug($"Imported pose: {bonesApplied} bones applied");
            OnPoseImported?.Invoke(skeleton);
            return true;
        }
        catch (Exception ex)
        {
            _log.Error($"Failed to import pose: {ex.Message}");
            return false;
        }
    }

    private PoseFile? _stashedPose;

    public DateTime? StashTime { get; private set; }

    public void StashPose(ISkeleton skeleton)
    {
        _stashedPose = CreatePoseFile(skeleton);
        StashTime = DateTime.UtcNow;
    }

    public bool ApplyStashedPose(ISkeleton skeleton, PoseImportOptions? options = null)
    {
        if (_stashedPose == null)
            return false;
        return ImportPose(skeleton, _stashedPose, options);
    }

    /// <summary>Selective-import filter: bone itself, or any ancestor, is in the set.</summary>
    private static bool PassesBoneFilter(Poser.Entities.IBone bone, PoseImportOptions options)
    {
        if (options.BoneFilter == null)
            return true;
        if (options.BoneFilter.Contains(bone.BoneName))
            return true;
        if (!options.FilterIncludesDescendants)
            return false;

        var ancestor = bone.ParentBone;
        int guard = 0;
        while (ancestor != null && guard++ < 256)
        {
            if (options.BoneFilter.Contains(ancestor.BoneName))
                return true;
            ancestor = ancestor.ParentBone;
        }
        return false;
    }

    private void ApplyBoneTransform(IBone bone, PoseFile.BoneData boneData, PoseImportOptions options)
    {
        // File bones are absolute raw (pre-reparent) snapshots, so the delta basis is
        // LastRawTransform — Brio passes bone.LastRawTransform to BonePoseInfo.Apply.
        // For partial-0 bones this equals LastTransform; for face partials it differs.
        var original = bone.LastRawTransform;

        // Build the new transform based on options
        var newTransform = new Transform
        {
            Position = options.ApplyPosition ? boneData.Position : original.Position,
            Rotation = options.ApplyRotation ? boneData.Rotation : original.Rotation,
            Scale = options.ApplyScale ? boneData.Scale : original.Scale
        };

        // Apply via the bone posing service
        _bonePosingService.ApplyTransform(bone, newTransform, original);
    }

    /// <summary>
    /// Re-applies the head subtree (j_kao + descendants) at its current raw
    /// transforms. Near-identity deltas are rejected by BonePoseInfo.Apply, so
    /// this is a no-op unless an import actually shifted the face's basis.
    /// </summary>
    private void ReconcileFace(ISkeleton skeleton)
    {
        var head = skeleton.GetBone("j_kao");
        if (head == null)
            return;

        foreach (var bone in skeleton.Bones)
        {
            if (!IsInSubtree(bone, head))
                continue;
            var raw = bone.LastRawTransform;
            _bonePosingService.ApplyTransform(bone, raw, raw);
        }
    }

    private static bool IsInSubtree(IBone bone, IBone root)
    {
        for (var b = bone; b != null; b = b.ParentBone)
            if (ReferenceEquals(b, root) || (b.BoneName == root.BoneName && b.PartialId == root.PartialId))
                return true;
        return false;
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

    // TODO(UI): file dialogs cannot be wired inside PosingCore. There is no dialog
    // infrastructure anywhere in the plugin yet — Poser/UI opens its own ImGui FileBrowser
    // (Poser/UI/Controls/TransformTabPane.cs) and calls ImportPose/ExportPose with the
    // chosen path, so these methods currently have zero callers. Implementing them needs
    // UI-side work: either Dalamud's FileDialogManager (whose Draw() must be pumped every
    // frame by the UI loop) injected via an abstraction, or routing through the existing
    // FileBrowser control. Until then they only log.
    public void ImportPoseWithDialog(ISkeleton skeleton, PoseImportOptions? options = null)
    {
        _log.Warning("ImportPoseWithDialog not yet implemented - use ImportPose with a path directly");
    }

    public void ExportPoseWithDialog(ISkeleton skeleton)
    {
        _log.Warning("ExportPoseWithDialog not yet implemented - use ExportPose with a path directly");
    }

    public void Dispose()
    {
        // Nothing to dispose
    }
}
