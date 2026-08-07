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
public sealed record ActorDescriptor(
    ActorId Id,
    string Name,
    IReadOnlyList<SkeletonDescriptor> Skeletons,
    bool IsPlayer = false,
    bool IsCompanion = false,
    bool IsHidden = false)
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

public enum LightKind
{
    Directional,
    Point,
    Spot,
    Area,
}

/// <summary>One spawned scene light. Live light properties are read through
/// the lighting service; the descriptor carries only sidebar-visible state.</summary>
public sealed record LightDescriptor(
    LightId Id,
    string Name,
    LightKind Kind,
    bool IsOn = true);

public sealed record SceneSnapshot(
    ulong Revision,
    IReadOnlyList<ActorDescriptor> Actors,
    IReadOnlyList<LightDescriptor> Lights)
{
    public static SceneSnapshot Empty { get; } =
        new(0, Array.Empty<ActorDescriptor>(), Array.Empty<LightDescriptor>());
}
