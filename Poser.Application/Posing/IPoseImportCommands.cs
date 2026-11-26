using Poser.Domain.Identity;
using Poser.Domain.Operations;
using Poser.Domain.Posing;
using Poser.Documents.Files;

namespace Poser.Application.Posing;

/// <summary>Pose imports target exact actor generations; native planning stays in Game.</summary>
public interface IPoseImportCommands
{
    bool IsImportBusy { get; }
    bool HasPosableSkeleton(ActorId actor);
    PoseImportInspection? InspectPose(ActorId actor, PoseFile pose);

    /// <summary>Imports a pose onto the actor. A pose from a file is read
    /// first with <see cref="PoseFile.Read"/>; <paramref name="description"/>
    /// is the name of the undo step.</summary>
    PoseEditResult ImportPose(ActorId actor, PoseFile poseFile, PoseImportOptions options,
        string description, Action<OperationReceipt>? onReceipt = null,
        IReadOnlyList<BoneId>? selectedBones = null);
    PoseEditResult ApplyRestPose(ActorId actor, RestPose pose, Action<OperationReceipt>? onReceipt = null);
    PoseEditResult ApplyReferencePose(ActorId actor, Action<OperationReceipt>? onReceipt = null);
}
