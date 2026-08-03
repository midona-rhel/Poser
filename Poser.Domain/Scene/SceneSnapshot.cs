using Poser.Domain.Identity;

namespace Poser.Domain.Scene;

public sealed record BoneDescriptor(
    BoneId Id,
    string DisplayName,
    BoneId? Parent,
    bool IsHidden = false);

public sealed record SkeletonDescriptor(
    SkeletonId Id,
    IReadOnlyList<BoneDescriptor> Bones)
{
    public PoseSlot Slot => Id.Slot;
}

/// <summary>
/// One scene actor owning a slot-indexed set of present skeletons. Slots are
/// never separate actors; absent auxiliary slots are normal.
/// </summary>
/// <param name="OwnerActor">The character this companion is attached to, when
/// the attachment resolves to another present actor. Companions remain their
/// own actors; the link is presentation lineage, not ownership of state.</param>
public sealed record ActorDescriptor(
    ActorId Id,
    string Name,
    IReadOnlyList<SkeletonDescriptor> Skeletons,
    bool IsPlayer = false,
    bool IsCompanion = false,
    bool IsHidden = false,
    ActorId? OwnerActor = null)
{
    /// <summary>The Character-slot skeleton; explicitly slot-scoped —
    /// callers needing another slot use <see cref="GetSkeleton"/>.</summary>
    public SkeletonDescriptor? CharacterSkeleton =>
        GetSkeleton(PoseSlot.Character);

    public SkeletonDescriptor? GetSkeleton(PoseSlot slot)
    {
        foreach (var skeleton in Skeletons)
        {
            if (skeleton.Id.Slot == slot)
                return skeleton;
        }
        return null;
    }
}

public sealed record SceneSnapshot(
    ulong Revision,
    IReadOnlyList<ActorDescriptor> Actors)
{
    public static SceneSnapshot Empty { get; } =
        new(0, Array.Empty<ActorDescriptor>());
}
