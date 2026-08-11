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

public enum LightKind
{
    Directional,
    Point,
    Spot,
    Area,
}

/// <summary>Who owns the native light. Spawned lights are plugin-created and
/// destroyable; GPose lights are the game's three camera lights (delist-only);
/// World lights are captured copies of overworld lights whose suppressed
/// original is restored on release.</summary>
public enum LightOwnership
{
    Spawned,
    GPose,
    World,
}

/// <summary>One scene light. Live light properties are read through
/// the lighting service; the descriptor carries only sidebar-visible state.</summary>
public sealed record LightDescriptor(
    LightId Id,
    string Name,
    LightKind Kind,
    bool IsOn = true,
    LightOwnership Ownership = LightOwnership.Spawned);

/// <summary>How a virtual camera drives the game camera. A Game camera is the
/// native orbit camera with overridden state; a Free camera replaces the view
/// matrix outright and flies on its own position and rotation.</summary>
public enum CameraKind
{
    Game,
    Free,
}

/// <summary>One virtual camera. Live camera properties are read through the
/// camera service; the descriptor carries only sidebar-visible state. IsLive
/// marks the one camera currently driving the game's view.</summary>
public sealed record CameraDescriptor(
    CameraId Id,
    string Name,
    CameraKind Kind,
    bool IsLive = false,
    bool IsDefault = false);

public sealed record SceneSnapshot(
    ulong Revision,
    IReadOnlyList<ActorDescriptor> Actors,
    IReadOnlyList<LightDescriptor> Lights,
    IReadOnlyList<CameraDescriptor> Cameras)
{
    public static SceneSnapshot Empty { get; } =
        new(
            0,
            Array.Empty<ActorDescriptor>(),
            Array.Empty<LightDescriptor>(),
            Array.Empty<CameraDescriptor>());
}
