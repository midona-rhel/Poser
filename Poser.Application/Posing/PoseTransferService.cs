using Poser.Domain.Identity;
using Poser.Domain.Posing;

namespace Poser.Application.Posing;

/// <summary>
/// Application-owned in-memory pose transfer slot. Capture and apply remain
/// stable-id commands; no legacy entity or native pointer is retained.
/// </summary>
public sealed class PoseTransferService
{
    private readonly PoseEditService _edits;
    private PortablePose? _stashedPose;

    public PoseTransferService(PoseEditService edits)
    {
        _edits = edits;
    }

    public bool HasStash => _stashedPose != null;
    public DateTimeOffset? StashedAt { get; private set; }

    /// <summary>Display label of the actor the stash was captured from —
    /// tooltip attribution only (Ktisis' StashedFrom), never identity.</summary>
    public string? StashedFrom { get; private set; }

    public PoseCaptureResult Capture(
        IReadOnlyList<TransformTargetId> targets) =>
        _edits.CapturePortable(targets);

    public PoseEditResult Apply(
        IReadOnlyList<TransformTargetId> targets,
        PortablePose pose,
        string description = "Apply copied pose") =>
        _edits.ApplyPortable(targets, pose, description);

    public PoseEditResult Stash(
        IReadOnlyList<TransformTargetId> targets,
        string sourceLabel)
    {
        var captured = Capture(targets);
        if (!captured.Success || captured.Pose == null)
            return PoseEditResult.Fail(
                captured.Detail ?? "Could not capture pose.");
        _stashedPose = captured.Pose;
        StashedAt = DateTimeOffset.UtcNow;
        StashedFrom = sourceLabel;
        return PoseEditResult.Ok(_stashedPose.Bones.Count);
    }

    public PoseEditResult ApplyStash(
        IReadOnlyList<TransformTargetId> targets) =>
        _stashedPose == null
            ? PoseEditResult.Fail("No pose has been stashed.")
            : Apply(targets, _stashedPose, "Apply stashed pose");
}
