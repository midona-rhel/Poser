using System;
using System.Collections.Generic;
using System.Numerics;
using Poser.Core;
using Poser.Domain.Actors;
using Poser.Domain.Identity;
using Poser.Domain.Operations;
using Poser.Domain.Posing;
using Poser.Domain.Scene;
using Poser.Domain.Transforms;
using Poser.Entities;
using Poser.Files;
using Poser.Scene;

namespace Poser.Services;

/// <summary>Legacy native file/whole-actor operations; scene pose edits use IPoseCommands.</summary>
public interface IPoseFacade
{
    bool IsImportBusy { get; }
    bool HasPosableSkeleton(IActor actor);
    ActorId? GetActorId(IActor actor);
    PoseEditResult ExportPose( IActor actor, string path, Action<bool>? onFinished = null);
    PoseEditResult CapturePoseFile( IActor actor, Action<PoseFile?> onCaptured, bool authoredOnly = false);
    PoseEditResult ImportPose( IActor actor, string path, PoseImportOptions options, IReadOnlyList<BoneId>? selectedBones = null, Action<OperationReceipt>? onReceipt = null);
    PoseEditResult ImportPose( IActor actor, PoseFile poseFile, PoseImportOptions options, string description, Action<OperationReceipt>? onReceipt = null, IReadOnlyList<BoneId>? selectedBones = null);
    PoseEditResult ApplyRestPose( IActor actor, RestPose pose, Action<OperationReceipt>? onReceipt = null);
    PoseEditResult ApplyReferencePose( IActor actor, Action<OperationReceipt>? onReceipt = null);
    PoseEditResult ResetAll(IActor actor);
}
