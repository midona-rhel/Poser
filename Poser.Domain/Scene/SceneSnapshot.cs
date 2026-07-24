using Poser.Domain.Identity;

namespace Poser.Domain.Scene;

public sealed record BoneDescriptor(
    BoneId Id,
    string DisplayName,
    BoneId? Parent,
    bool IsHidden = false);

public sealed record SkeletonDescriptor(
    SkeletonId Id,
    IReadOnlyList<BoneDescriptor> Bones);

public sealed record ActorDescriptor(
    ActorId Id,
    string Name,
    SkeletonDescriptor? Skeleton,
    bool IsPlayer = false,
    bool IsCompanion = false,
    bool IsHidden = false);

public sealed record SceneSnapshot(
    ulong Revision,
    IReadOnlyList<ActorDescriptor> Actors)
{
    public static SceneSnapshot Empty { get; } =
        new(0, Array.Empty<ActorDescriptor>());
}
