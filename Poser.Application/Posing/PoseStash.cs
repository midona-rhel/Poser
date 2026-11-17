using Poser.Application.Presentation;
using Poser.Application.Scene;
using Poser.Application.Settings;
using Poser.Documents.Files;
using Poser.Domain.Identity;
using Poser.Domain.Operations;
using Poser.Domain.Posing;

namespace Poser.Application.Posing;

/// <summary>
/// The stash: one pose kept while Poser runs, so it can be put on any actor.
/// Every Stash button and menu item fills it, and every apply button reads it.
/// It holds the pose as it looks on screen, the same capture Export writes to a
/// file: every bone of the body, face and weapons, and the actor's placement.
/// It is not saved to disk, and stashing again replaces it.
/// </summary>
public sealed class PoseStash(
    IPoseFileCapture capture,
    IPoseImportCommands imports,
    IUserNotices notices,
    SceneSession scene,
    ConfigurationService configuration)
{
    public PoseFile? Pose { get; private set; }
    public DateTimeOffset? StashedAt { get; private set; }

    /// <summary>The name of the actor the pose came from, as the sidebar shows it.</summary>
    public string? StashedFrom { get; private set; }

    /// <summary>
    /// Captures the actor's pose. The capture finishes on a later frame, after
    /// the game has updated the skeleton, so the stash changes then.
    /// </summary>
    public PoseEditResult Stash(ActorId actor)
    {
        var name = configuration.GetDisplayName(
            actor.LogicalId, scene.Snapshot.FindActor(actor)?.Name ?? string.Empty);
        var armed = capture.CapturePoseFile(actor, pose =>
        {
            // No pose when the actor redrew or left between the click and the capture.
            if (pose == null)
            {
                notices.Failed("Stash: the pose could not be captured.");
                return;
            }
            Pose = pose;
            StashedAt = DateTimeOffset.UtcNow;
            StashedFrom = name;
            notices.Done("Pose stashed.");
        });
        if (!armed.Success)
            notices.Failed($"Stash: {armed.Detail}");
        return armed;
    }

    /// <summary>
    /// Apply stash: puts the whole stashed pose on the actor in one click, as
    /// one undo step, through the same import as a pose file. Like Ktisis'
    /// Apply stash it sets every bone's rotation and position, on the body,
    /// the face and the weapons, leaves scale alone and does not move the
    /// actor. The buttons that call this are disabled until something is stashed.
    /// </summary>
    public PoseEditResult Apply(ActorId actor)
    {
        var applied = imports.ImportPose(
            actor,
            Pose!,
            new PoseImportOptions { ApplyPosition = true },
            "Apply stashed pose",
            receipt =>
            {
                if (receipt.State is not (OperationReceiptState.Pending or OperationReceiptState.Applied))
                    notices.Failed($"Apply stash: {receipt.Detail ?? receipt.State.ToString()}.");
            });
        if (!applied.Success)
            notices.Failed($"Apply stash: {applied.Detail}");
        return applied;
    }
}
