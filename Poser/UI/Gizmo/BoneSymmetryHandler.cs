using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Poser.Core;
using Poser.Entities;
using Poser.Services;

namespace Poser.UI.Gizmo;

/// <summary>
/// Handles symmetry transformations for paired bones (left/right).
/// </summary>
public class BoneSymmetryHandler
{
    private readonly IEditorState _editorState;
    private readonly IBonePosingService _bonePosingService;

    public BoneSymmetryHandler(IEditorState editorState, IBonePosingService bonePosingService)
    {
        _editorState = editorState;
        _bonePosingService = bonePosingService;
    }

    /// <summary>
    /// Apply symmetry transform to the paired bone (if exists and not already selected).
    /// </summary>
    public void ApplySymmetryTransform(
        IBone bone,
        Skeleton skeleton,
        HashSet<string> selectedBoneNames,
        Transform newTransform,
        Transform oldTransform,
        TransformComponents propagate)
    {
        if (_editorState.SymmetryMode == SymmetryMode.Off)
            return;

        var pairedName = GetPairedBoneName(bone.BoneName);
        if (pairedName == null)
            return;

        // Skip if paired bone is already selected (will be transformed directly)
        if (selectedBoneNames.Contains(pairedName))
            return;

        var pairedBone = skeleton.Bones.FirstOrDefault(b => b.BoneName == pairedName);
        if (pairedBone == null)
            return;

        var positionDelta = newTransform.Position - oldTransform.Position;
        var rotationDelta = newTransform.Rotation * Quaternion.Inverse(oldTransform.Rotation);

        Transform symmetryTransform;
        if (_editorState.SymmetryMode == SymmetryMode.Copy)
        {
            // Copy: apply same delta to paired bone
            symmetryTransform = new Transform(
                pairedBone.LastTransform.Position + positionDelta,
                Quaternion.Normalize(rotationDelta * pairedBone.LastTransform.Rotation),
                pairedBone.LastTransform.Scale);
        }
        else // Mirror
        {
            // Mirror: invert X, Y, and Z deltas
            var mirroredPositionDelta = new Vector3(-positionDelta.X, -positionDelta.Y, -positionDelta.Z);
            var mirroredRotationDelta = new Quaternion(-rotationDelta.X, -rotationDelta.Y, -rotationDelta.Z, rotationDelta.W);

            symmetryTransform = new Transform(
                pairedBone.LastTransform.Position + mirroredPositionDelta,
                Quaternion.Normalize(mirroredRotationDelta * pairedBone.LastTransform.Rotation),
                pairedBone.LastTransform.Scale);
        }

        _bonePosingService.ApplyTransform(pairedBone, symmetryTransform, pairedBone.LastTransform, propagate, accumulate: true);
    }

    /// <summary>
    /// Get all paired bones for a list of selected bones.
    /// </summary>
    public List<IBone> GetPairedBones(IEnumerable<IBone> bones, Skeleton skeleton)
    {
        if (_editorState.SymmetryMode == SymmetryMode.Off)
            return new List<IBone>();

        var selectedNames = new HashSet<string>(bones.Select(b => b.BoneName));
        var pairedBones = new List<IBone>();

        foreach (var bone in bones)
        {
            var pairedName = GetPairedBoneName(bone.BoneName);
            if (pairedName != null && !selectedNames.Contains(pairedName))
            {
                var pairedBone = skeleton.Bones.FirstOrDefault(b => b.BoneName == pairedName);
                if (pairedBone != null)
                {
                    pairedBones.Add(pairedBone);
                    selectedNames.Add(pairedName); // Avoid duplicates
                }
            }
        }

        return pairedBones;
    }

    /// <summary>
    /// Get the paired bone name by swapping _l to _r suffix.
    /// Returns null if bone has no pair.
    /// </summary>
    public static string? GetPairedBoneName(string boneName)
    {
        if (boneName.EndsWith("_l"))
            return boneName[..^2] + "_r";
        if (boneName.EndsWith("_r"))
            return boneName[..^2] + "_l";
        return null;
    }
}
