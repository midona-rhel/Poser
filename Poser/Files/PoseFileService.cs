using System;
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

    public event Action<ISkeleton>? OnPoseImported;
    public event Action<ISkeleton, string>? OnPoseExported;

    public PoseImportOptions DefaultImportOptions { get; } = PoseImportOptions.Default;

    public PoseFileService(
        IPluginLog log,
        IBonePosingService bonePosingService)
    {
        _log = log;
        _bonePosingService = bonePosingService;
    }

    public PoseFile CreatePoseFile(ISkeleton skeleton)
    {
        var poseFile = new PoseFile();
        var poseInfo = _bonePosingService.GetPoseInfo(skeleton);

        foreach (var bone in skeleton.Bones)
        {
            var bonePoseInfo = poseInfo.GetPoseInfo(bone.BoneName, bone.PartialId);

            // Get the combined transform delta
            var modification = _bonePosingService.GetModification(bone);
            if (modification != null)
            {
                var transform = modification.Value;
                var boneData = new PoseFile.BoneData
                {
                    Position = transform.Position,
                    Rotation = transform.Rotation,
                    Scale = transform.Scale
                };

                // Store in appropriate dictionary based on partial ID
                // Partial 0 = body bones, Partial 1+ = face/accessories
                if (bone.PartialId == 0)
                {
                    poseFile.Bones[bone.BoneName] = boneData;
                }
            }
            else
            {
                // Even bones without modifications should be captured with their current transforms
                // for full pose preservation (like Brio does)
                var boneData = new PoseFile.BoneData
                {
                    Position = bone.LastTransform.Position,
                    Rotation = bone.LastTransform.Rotation,
                    Scale = bone.LastTransform.Scale
                };

                if (bone.PartialId == 0)
                {
                    poseFile.Bones[bone.BoneName] = boneData;
                }
            }
        }

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
            // Reset skeleton first if importing fresh
            _bonePosingService.ResetSkeleton(skeleton);

            int bonesApplied = 0;

            // Apply body bones
            if (options.ApplyBody)
            {
                foreach (var (boneName, boneData) in poseFile.Bones)
                {
                    var bone = skeleton.GetBone(boneName);
                    if (bone == null)
                        continue;

                    // Filter by face bones if needed
                    if (!options.ApplyFace && IsFaceBone(boneName))
                        continue;

                    ApplyBoneTransform(bone, boneData, options);
                    bonesApplied++;
                }
            }

            // Apply weapon bones
            if (options.ApplyMainHand)
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

            if (options.ApplyOffHand)
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

    private void ApplyBoneTransform(IBone bone, PoseFile.BoneData boneData, PoseImportOptions options)
    {
        // Get the original transform for delta calculation
        var original = bone.LastTransform;

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

    public void ImportPoseWithDialog(ISkeleton skeleton, PoseImportOptions? options = null)
    {
        // File dialog integration would go here
        // For now, this is a placeholder - will be implemented when UI is updated
        _log.Warning("ImportPoseWithDialog not yet implemented - use ImportPose with a path directly");
    }

    public void ExportPoseWithDialog(ISkeleton skeleton)
    {
        // File dialog integration would go here
        // For now, this is a placeholder - will be implemented when UI is updated
        _log.Warning("ExportPoseWithDialog not yet implemented - use ExportPose with a path directly");
    }

    public void Dispose()
    {
        // Nothing to dispose
    }
}
