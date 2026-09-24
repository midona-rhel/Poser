using Poser.Domain.Identity;
using Poser.Domain.Operations;
using Poser.Domain.Posing;
using Poser.Files;

namespace Poser.Application.Posing;

/// <summary>Pose imports target exact actor generations; native planning stays in Game.</summary>
public interface IPoseImportCommands
{
    bool IsImportBusy { get; }
    bool HasPosableSkeleton(ActorId actor);
    PoseEditResult ImportPose(ActorId actor, string path, PoseImportOptions options,
        IReadOnlyList<BoneId>? selectedBones = null, Action<OperationReceipt>? onReceipt = null);
    PoseEditResult ImportPose(ActorId actor, PoseFile poseFile, PoseImportOptions options,
        string description, Action<OperationReceipt>? onReceipt = null,
        IReadOnlyList<BoneId>? selectedBones = null);
    PoseEditResult ApplyRestPose(ActorId actor, RestPose pose, Action<OperationReceipt>? onReceipt = null);
    PoseEditResult ApplyReferencePose(ActorId actor, Action<OperationReceipt>? onReceipt = null);
}
