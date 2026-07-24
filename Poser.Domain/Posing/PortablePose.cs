using Poser.Domain.Identity;

namespace Poser.Domain.Posing;

/// <summary>
/// Actor-independent bone identity used by in-memory pose transfer and codecs.
/// Native indices and actor generations are deliberately excluded.
/// </summary>
public readonly record struct PortableBoneId(
    PoseSlot Slot,
    int PartialId,
    string CanonicalName)
{
    public bool IsValid =>
        PartialId >= 0 &&
        !string.IsNullOrWhiteSpace(CanonicalName);

    public static PortableBoneId From(BoneId bone) =>
        new(bone.Slot, bone.PartialId, bone.CanonicalName);
}

public readonly record struct PortableBonePose(
    PortableBoneId Bone,
    BonePose Pose);

/// <summary>
/// Immutable actor-independent pose snapshot. It contains every captured bone,
/// including bones with an empty pose, so applying a reset pose can clear a
/// destination override.
/// </summary>
public sealed class PortablePose
{
    private readonly PortableBonePose[] _bones;
    private readonly IReadOnlyDictionary<PortableBoneId, BonePose> _byBone;

    public PortablePose(IEnumerable<PortableBonePose> bones)
    {
        ArgumentNullException.ThrowIfNull(bones);
        _bones = bones
            .Select(item => item with
            {
                Pose = item.Pose.InteractiveOnly(),
            })
            .ToArray();
        if (_bones.Any(item => !item.Bone.IsValid))
            throw new ArgumentException(
                "Portable pose contains an invalid bone identity.",
                nameof(bones));
        if (_bones.Select(item => item.Bone).Distinct().Count() != _bones.Length)
            throw new ArgumentException(
                "Portable pose contains duplicate bone identities.",
                nameof(bones));

        _byBone = _bones.ToDictionary(
            item => item.Bone,
            item => item.Pose);
    }

    public IReadOnlyList<PortableBonePose> Bones => _bones;

    public bool TryGet(
        PortableBoneId bone,
        out BonePose pose) =>
        _byBone.TryGetValue(bone, out pose!);
}
