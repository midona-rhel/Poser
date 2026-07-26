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
        ISkeletonService skeletons,
        IExpressionService expressions,
        IGazeService gaze,
        Poser.Application.Animation.AnimationSession animation,
        Poser.Application.Presentation.ActorPresentationSession presentation,
        IPluginLog log)
    {
        _bindings = bindings;
        _edits = edits;
        _transfers = transfers;
        _bonePosing = bonePosing;
        _skeletons = skeletons;
        _expressions = expressions;
        _gaze = gaze;
        _animation = animation;
        _presentation = presentation;
        _log = log;
    }

    private readonly ISkeletonService _skeletons;

    private readonly IBonePosingService _bonePosing;
    private readonly IExpressionService _expressions;
    private readonly IGazeService _gaze;
    private readonly Poser.Application.Animation.AnimationSession _animation;
    private readonly Poser.Application.Presentation.ActorPresentationSession _presentation;
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
    public PoseEditResult ResetAll(IActor actor)
    {
        var failures = new List<string>();

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

        var pose = Reset(actor, PoseRegion.All);
        if (!pose.Success && pose.Detail is { } poseDetail)
            failures.Add(poseDetail);

        foreach (var slotSkeleton in _skeletons.GetSkeletons(actor))
            _bonePosing.ClearIkConfigurations(slotSkeleton);

        // Animation and physics restore LAST: the steps above move bones,
        // and an actor left frozen would hide the result of its own reset.
        if (_bindings.GetActorId(actor) is { } animationActor)
        {
            var animation = _animation.ResetActor(animationActor);
            if (!animation.Success && animation.Detail is { } animationDetail)
                failures.Add($"animation reset failed: {animationDetail}");

            var presentation = _presentation.ResetActor(animationActor);
            if (!presentation.Success && presentation.Detail is { } presentationDetail)
                failures.Add($"appearance reset failed: {presentationDetail}");
        }

        if (failures.Count == 0)
            return pose;
        var detail = string.Join(" | ", failures);
        _log.Warning($"Reset All completed partially: {detail}");
        return PoseEditResult.Fail(detail);
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
        else if (!string.IsNullOrEmpty(result.Detail))
            _log.Information($"Pose edit '{description}': {result.Detail}");
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
        IActor actor,
        PoseRegion region)
    {
        // Body/Face/Hair regions are Character-only; only All spans every
        // present slot.
        var targets = region == PoseRegion.All
            ? Targets(actor)
            : CharacterTargets(actor);
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
    /// layers, across every present slot; pairing stays within each slot.</summary>
    public PoseEditResult Mirror(IActor actor) =>
        Report("Mirror edits", _edits.Mirror(Targets(actor), "Mirror edits"));

    /// <summary>Whether any bone of any present slot carries a
    /// Poser-authored (unnamed) layer — the "Mirror edits" predicate.</summary>
    public bool HasAuthoredEdits(IActor actor) =>
        _skeletons.GetSkeletons(actor).Any(skeleton =>
            _bonePosing.GetPoseInfo(skeleton).AllPoses
                .Any(pose => pose.Stacks.Any(stack => stack.Layer == null)));

    public PoseCaptureResult Copy(IActor actor) =>
        _transfers.Capture(Targets(actor));

    public PoseEditResult Paste(
        IActor actor,
        PortablePose pose) =>
        Report("Paste pose", _transfers.Apply(Targets(actor), pose));

    public PoseEditResult Stash(IActor actor) =>
        Report("Stash pose", _transfers.Stash(Targets(actor)));

    public PoseEditResult ApplyStash(IActor actor) =>
        Report("Apply stash", _transfers.ApplyStash(Targets(actor)));

    /// <summary>Concrete bone targets across every present slot skeleton.</summary>
    private IReadOnlyList<TransformTargetId> Targets(IActor actor) =>
        _skeletons.GetSkeletons(actor)
            .SelectMany(SkeletonTargets)
            .ToArray();

    private IReadOnlyList<TransformTargetId> CharacterTargets(IActor actor) =>
        _skeletons.GetSkeleton(actor) is { } character
            ? SkeletonTargets(character)
            : Array.Empty<TransformTargetId>();

    private IReadOnlyList<TransformTargetId> SkeletonTargets(
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
