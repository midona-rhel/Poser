using Dalamud.Plugin.Services;
using Poser.Application.Posing;
using Poser.Domain.Identity;
using Poser.Domain.Posing;
using Poser.Entities;
using Poser.Game.Bindings;
using Poser.Services;

namespace Poser.Game.Posing;

/// <summary>Legacy IEntity presentation bridge into stable-id pose commands.</summary>
public sealed class CleanPoseFacade
{
    private readonly StableBindingRegistry _bindings;
    private readonly PoseEditService _edits;
    private readonly PoseTransferService _transfers;

    public CleanPoseFacade(
        StableBindingRegistry bindings,
        PoseEditService edits,
        PoseTransferService transfers,
        IBonePosingService bonePosing,
        IExpressionService expressions,
        IGazeService gaze,
        IPluginLog log)
    {
        _bindings = bindings;
        _edits = edits;
        _transfers = transfers;
        _bonePosing = bonePosing;
        _expressions = expressions;
        _gaze = gaze;
        _log = log;
    }

    private readonly IBonePosingService _bonePosing;
    private readonly IExpressionService _expressions;
    private readonly IGazeService _gaze;
    private readonly IPluginLog _log;

    /// <summary>
    /// The one actor-level reset operation behind the Pose section's
    /// **Reset All**: clears manual pose transforms for all regions,
    /// expression weights and their layer, every Poser gaze mode / part /
    /// target / lock (restoring the captured native look-at), and actor-local
    /// IK arming including the Live IK session switch. It deliberately
    /// preserves the actor's world/model placement, the pose stash, tool and
    /// Local/World choices, and tree disclosure. Steps run in an order that
    /// cannot leave managed expression/gaze state claiming a layer that its
    /// native pose no longer has: expression weights clear before the pose
    /// stacks, gaze releases through its native restore path, and every step
    /// runs even when an earlier one fails. A partial failure is aggregated
    /// into one reported result and logged.
    /// </summary>
    public PoseEditResult ResetAll(ISkeleton skeleton)
    {
        var failures = new List<string>();
        var actor = skeleton.Actor;

        try
        {
            _expressions.ResetExpression(actor);
        }
        catch (Exception ex)
        {
            failures.Add($"expression reset failed: {ex.Message}");
        }

        try
        {
            _gaze.ResetGaze(actor);
        }
        catch (Exception ex)
        {
            failures.Add($"gaze reset failed: {ex.Message}");
        }

        var pose = Reset(skeleton, PoseRegion.All);
        if (!pose.Success && pose.Detail is { } poseDetail)
            failures.Add(poseDetail);

        _bonePosing.SetAllIk(skeleton, false);

        if (failures.Count == 0)
            return pose;
        var detail = string.Join(" | ", failures);
        _log.Warning($"Reset All completed partially: {detail}");
        return PoseEditResult.Fail(detail);
    }

    /// <summary>
    /// Stable-id IK arming for the next gesture. IK configuration is session
    /// state owned by the runtime; the id resolves inside this facade and no
    /// entity reaches the caller. Arming is limited to the supported chain
    /// ends (hands + feet) so no gesture path can ever arm an unsupported
    /// bone; disarming always passes through.
    /// </summary>
    public void ConfigureIk(TransformTargetId target, bool enabled)
    {
        if (target.Bone is not { } boneId)
            return;
        var bone = _bindings.Resolve(boneId);
        if (!bone.Success)
            return;
        var arm = enabled && Core.BoneIKInfo.IsSupportedChainEnd(boneId.CanonicalName);
        var ik = arm
            ? Core.BoneIKInfo.CalculateDefault(boneId.CanonicalName)
            : Core.BoneIKInfo.Disabled;
        ik.Enabled = arm;
        _bonePosing.SetBoneIK(bone.Value!, ik);
    }

    public bool HasStash => _transfers.HasStash;
    public DateTimeOffset? StashedAt => _transfers.StashedAt;

    /// <summary>
    /// Every UI-facing pose edit reports through here: a failed edit is never
    /// a silent no-op — the reason ("A transform gesture is active.", stale
    /// binding, ...) lands in the log with the attempted description.
    /// </summary>
    private PoseEditResult Report(string description, PoseEditResult result)
    {
        if (!result.Success)
            _log.Warning($"Pose edit '{description}' failed: {result.Detail}");
        return result;
    }

    /// <summary>Stable-id bone reset (selection/transform identity path).</summary>
    public PoseEditResult ResetBone(TransformTargetId target, string boneName) =>
        Report($"Reset {boneName}", _edits.Reset(
            new[] { target },
            PoseRegion.All,
            $"Reset {boneName}"));

    /// <summary>Stable-id bone flip.</summary>
    public PoseEditResult FlipBone(TransformTargetId target, string boneName) =>
        Report($"Flip {boneName}", _edits.Flip(target, $"Flip {boneName}"));

    public PoseEditResult ResetBone(IBone bone)
    {
        var concrete = bone is VirtualBone group
            ? group.PivotBone
            : bone;
        if (concrete == null || Target(concrete) is not { } target)
            return Report($"Reset {bone.Name}", PoseEditResult.Fail(
                $"Bone {bone.Name} has no stable pose binding."));
        return Report($"Reset {bone.Name}", _edits.Reset(
            new[] { target },
            PoseRegion.All,
            $"Reset {bone.Name}"));
    }

    public PoseEditResult Reset(
        ISkeleton skeleton,
        PoseRegion region)
    {
        var targets = Targets(skeleton);
        var description = region == PoseRegion.All
            ? "Reset pose"
            : $"Reset {region.ToString().ToLowerInvariant()}";
        return Report(description, _edits.Reset(targets, region, description));
    }

    public PoseEditResult FlipBone(IBone bone)
    {
        var concrete = bone is VirtualBone group
            ? group.PivotBone
            : bone;
        if (concrete == null || Target(concrete) is not { } target)
            return Report($"Flip {bone.Name}", PoseEditResult.Fail(
                $"Bone {bone.Name} has no stable pose binding."));
        return Report($"Flip {bone.Name}", _edits.Flip(target, $"Flip {bone.Name}"));
    }

    /// <summary>Animation-safe "Mirror edits": mirrors only Poser-authored
    /// layers (correction 3A).</summary>
    public PoseEditResult Mirror(ISkeleton skeleton) =>
        Report("Mirror edits", _edits.Mirror(Targets(skeleton), "Mirror edits"));

    /// <summary>Whether any bone carries a Poser-authored (unnamed) layer —
    /// the "Mirror edits" availability predicate.</summary>
    public bool HasAuthoredEdits(ISkeleton skeleton) =>
        _bonePosing.GetPoseInfo(skeleton).AllPoses
            .Any(pose => pose.Stacks.Any(stack => stack.Layer == null));

    public PoseCaptureResult Copy(ISkeleton skeleton) =>
        _transfers.Capture(Targets(skeleton));

    public PoseEditResult Paste(
        ISkeleton skeleton,
        PortablePose pose) =>
        Report("Paste pose", _transfers.Apply(Targets(skeleton), pose));

    public PoseEditResult Stash(ISkeleton skeleton) =>
        Report("Stash pose", _transfers.Stash(Targets(skeleton)));

    public PoseEditResult ApplyStash(ISkeleton skeleton) =>
        Report("Apply stash", _transfers.ApplyStash(Targets(skeleton)));

    private IReadOnlyList<TransformTargetId> Targets(
        ISkeleton skeleton) =>
        skeleton.Bones
            .Where(bone => bone is not VirtualBone)
            .Select(Target)
            .Where(target => target.HasValue)
            .Select(target => target!.Value)
            .ToArray();

    private TransformTargetId? Target(IBone bone) =>
        _bindings.GetBoneId(bone) is { } id
            ? TransformTargetId.ForBone(id)
            : null;
}
