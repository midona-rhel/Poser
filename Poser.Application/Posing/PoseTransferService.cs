using Poser.Domain.Identity;
using Poser.Domain.Posing;

namespace Poser.Application.Posing;

/// <summary>
/// Holds one copied pose, the stash, so it can be applied to another actor.
/// It lasts for the session; stashing again replaces it.
/// </summary>
public sealed class PoseTransferService(PoseEditService edits)
{
    private PortablePose? _stashedPose;

    public bool HasStash => _stashedPose != null;
    public DateTimeOffset? StashedAt { get; private set; }

    /// <summary>Name of the actor the stash came from, shown in the Apply stash tooltip.</summary>
    public string? StashedFrom { get; private set; }

    public PoseEditResult Stash(
        IReadOnlyList<TransformTargetId> bones,
        string sourceLabel)
    {
        if (edits.CapturePortable(bones, out var pose) is { } failed)
            return failed;
        _stashedPose = pose!;
        StashedAt = DateTimeOffset.UtcNow;
        StashedFrom = sourceLabel;
        return PoseEditResult.Ok(_stashedPose.Bones.Count);
    }

    /// <summary>
    /// Applies the stash. The Apply stash button is disabled, and the menu
    /// item hidden, until something has been stashed.
    /// </summary>
    public PoseEditResult ApplyStash(
        IReadOnlyList<TransformTargetId> bones) =>
        edits.ApplyPortable(bones, _stashedPose!, "Apply stashed pose");
}
