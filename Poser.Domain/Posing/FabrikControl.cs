using System.Numerics;
using Poser.Domain.Identity;
using Poser.Domain.Transforms;

namespace Poser.Domain.Posing;

public enum FabrikControlMode { Forward, Reverse, Bidirectional }

/// <summary>Actor-model point, world point, or world offset from an exact anchor.</summary>
public sealed record FabrikTarget(
    IkTargetMode Mode, Vector3 Position, Quaternion Rotation,
    Vector3 AuthoredPosition, Quaternion AuthoredRotation,
    BoneId? Bone = null, SelectionId? Entity = null,
    bool HoldRotation = true);

public sealed record FabrikBonePose(
    string Name, int Partial, Vector3 Position, Quaternion Rotation,
    Vector3 AuthoredPosition, Quaternion AuthoredRotation);

/// <summary>The authored chain at entry, root first. Reused across direction changes.</summary>
public sealed record FabrikControl(
    FabrikBonePose[] Bones, FabrikTarget Root, FabrikTarget Tip, float SwivelBaseline)
{
    public string? Validate()
    {
        if (Bones is null || Bones.Length is < 2 or > IkChainConfig.MaxDepth + 1
            || Root is null || Tip is null || !float.IsFinite(SwivelBaseline))
            return "FABRIK requires 2–51 bones and two finite targets.";
        foreach (var bone in Bones)
            if (bone is null || string.IsNullOrWhiteSpace(bone.Name) || bone.Partial < 0
                || !TransformMath.IsFinite(bone.Position) || !RotationValid(bone.Rotation)
                || !TransformMath.IsFinite(bone.AuthoredPosition) || !RotationValid(bone.AuthoredRotation))
                return "FABRIK contains an invalid bone pose.";
        foreach (var target in new[] { Root, Tip })
            if (!Enum.IsDefined(target.Mode) || !TransformMath.IsFinite(target.Position)
                || !RotationValid(target.Rotation) || !TransformMath.IsFinite(target.AuthoredPosition)
                || !RotationValid(target.AuthoredRotation))
                return "FABRIK contains an invalid target.";
        return null;
    }

    private static bool RotationValid(Quaternion value) =>
        TransformMath.IsFinite(value) && value.LengthSquared() > 1e-8f;
}
