using System.Numerics;
using Poser.Domain.Identity;
using Poser.Domain.Transforms;

namespace Poser.Domain.Posing;

/// <summary>Actor-model point, world point, or world offset from an exact anchor.</summary>
public sealed record FabrikTarget(
    IkTargetMode Mode, Vector3 Position, Quaternion Rotation,
    Vector3 AuthoredPosition, Quaternion AuthoredRotation,
    BoneId? Bone = null, SelectionId? Entity = null,
    bool HoldRotation = true);

public sealed record FabrikBonePose(
    string Name, int Partial, Vector3 Position, Quaternion Rotation,
    Vector3 AuthoredPosition, Quaternion AuthoredRotation);

public sealed record FabrikReferenceBone(FabrikBonePose Pose, FabrikTarget Anchor);

/// <summary>Root-first authored span, anchored at its far ends around one selected handle.</summary>
public sealed record FabrikControl(
    FabrikBonePose[] Bones, FabrikTarget Root, FabrikTarget Tip, float SwivelBaseline,
    int HandleIndex, FabrikTarget Handle)
{
    /// <summary>Bounded, unsolved capture of both potential spans. Depth changes
    /// select from this reference, never from the previous solver output.</summary>
    public FabrikReferenceBone[]? ReferenceBones { get; init; }

    public FabrikControl SelectSpan(IReadOnlyList<(string Name, int Partial)> members, int handleIndex)
    {
        var reference = ReferenceBones ?? throw new InvalidOperationException("The chain has no reference span.");
        var selected = members.Select(key => reference.Single(b =>
            b.Pose.Name == key.Name && b.Pose.Partial == key.Partial)).ToArray();
        return this with { Bones = selected.Select(b => b.Pose).ToArray(), HandleIndex = handleIndex,
            Root = selected[0].Anchor, Tip = selected[^1].Anchor };
    }

    public string? Validate()
    {
        if (Bones is null || Bones.Length is < 1 or > IkChainConfig.MaxDepth + 1
            || Root is null || Tip is null || Handle is null
            || HandleIndex < 0 || HandleIndex >= Bones.Length || !float.IsFinite(SwivelBaseline))
            return "The chain requires 1–51 bones and finite targets.";
        foreach (var bone in Bones)
            if (bone is null || string.IsNullOrWhiteSpace(bone.Name) || bone.Partial < 0
                || !TransformMath.IsFinite(bone.Position) || !RotationValid(bone.Rotation)
                || !TransformMath.IsFinite(bone.AuthoredPosition) || !RotationValid(bone.AuthoredRotation))
                return "FABRIK contains an invalid bone pose.";
        foreach (var target in new[] { Root, Tip, Handle })
            if (!Enum.IsDefined(target.Mode) || !TransformMath.IsFinite(target.Position)
                || !RotationValid(target.Rotation) || !TransformMath.IsFinite(target.AuthoredPosition)
                || !RotationValid(target.AuthoredRotation))
                return "FABRIK contains an invalid target.";
        if (ReferenceBones is { } reference)
        {
            if (reference.Length is < 1 or > IkChainConfig.MaxDepth * 2 + 1
                || reference.Any(b => b is null || b.Pose is null || b.Anchor is null)
                || reference.Select(b => (b.Pose.Name, b.Pose.Partial)).Distinct().Count() != reference.Length)
                return "FABRIK contains an invalid reference span.";
            foreach (var item in reference)
                if (item.Anchor.Mode != IkTargetMode.Actor
                    || new FabrikControl([item.Pose], item.Anchor, item.Anchor, 0, 0, item.Anchor).Validate() != null)
                    return "FABRIK contains an invalid reference pose.";
        }
        return null;
    }

    private static bool RotationValid(Quaternion value) =>
        TransformMath.IsFinite(value) && value.LengthSquared() > 1e-8f;
}
