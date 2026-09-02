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

/// <summary>The pose verbs a surface issues: import, export, capture, reset, flip, mirror, stash.</summary>
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
    bool HasStash { get; }
    DateTimeOffset? StashedAt { get; }
    string? StashedFrom { get; }
    PoseEditResult ResetBone(TransformTargetId target, string boneName);
    PoseEditResult FlipBone(TransformTargetId target, string boneName);
    PoseEditResult ResetBone(IBone bone);
    PoseEditResult ResetBones( IReadOnlyList<TransformTargetId> targets, string description);
    PoseEditResult Reset( IActor actor, PoseRegion region);
    PoseEditResult FlipBone(IBone bone);
    PoseEditResult Mirror(IActor actor);
    bool HasAuthoredEdits(IActor actor);
    PoseEditResult Stash(IActor actor, string sourceLabel);
    PoseEditResult ApplyStash(IActor actor);
}
