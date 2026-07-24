using System.Numerics;
using Poser.Application.Transforms;
using Poser.Application.Scene;
using Poser.Domain.Identity;
using Poser.Domain.Posing;
using Poser.Domain.Transforms;

namespace Poser.Game.Transforms;

/// <summary>Legacy UI bridge into the clean transform gesture application API.</summary>
public sealed class CleanTransformFacade
{
    private readonly SceneSession _scene;
    private readonly TransformGestureService _gestures;
    private readonly TransformCommandService _commands;

    public CleanTransformFacade(
        SceneSession scene,
        TransformGestureService gestures,
        TransformCommandService commands)
    {
        _scene = scene;
        _gestures = gestures;
        _commands = commands;
    }

    public TransformGestureId? ActiveGesture =>
        _gestures.ActiveGesture;
    public bool CanUndo => _gestures.History.CanUndo;
    public bool CanRedo => _gestures.History.CanRedo;
    public string? UndoDescription =>
        _gestures.History.UndoDescription;
    public string? RedoDescription =>
        _gestures.History.RedoDescription;

    /// <summary>
    /// Stable-id gesture entry: expands linked-bone and symmetry partners
    /// from the scene snapshot before <c>Begin</c>, then dispatches to the
    /// gesture service. Owns no state.
    /// </summary>
    public GestureResult Begin(
        IReadOnlyList<TransformTargetId> targetIds,
        TransformOperation operation,
        TransformSpace space,
        PivotMode pivotMode = PivotMode.PerTarget,
        Vector3? customPivot = null,
        string description = "Transform",
        bool includeLinkedBones = false,
        TransformDeltaMode? symmetry = null)
    {
        var targets = new List<TransformTargetId>(targetIds);
        if (includeLinkedBones)
            AddLinkedBoneTargets(targets);
        var targetModes = symmetry is { } symmetryMode
            ? AddSymmetryTargets(targets, symmetryMode)
            : null;
        return _gestures.Begin(new BeginTransformGesture(
            targets.Distinct().ToArray(),
            operation,
            space,
            pivotMode,
            customPivot,
            description,
            targetModes));
    }

    public GestureResult Update(
        TransformGestureId id,
        TransformDelta delta) =>
        _gestures.Update(id, delta);

    public GestureResult Commit(TransformGestureId id) =>
        _gestures.Commit(id);

    public GestureResult Cancel(TransformGestureId id) =>
        _gestures.Cancel(id);

    public GestureResult Undo() => _gestures.Undo();
    public GestureResult Redo() => _gestures.Redo();

    /// <summary>Stable-id atomic absolute write (non-interactive command).</summary>
    public GestureResult SetAbsolute(
        TransformTargetId target,
        PoseTransform desired,
        string description) =>
        _commands.SetAbsolute(target, desired, description);

    /// <summary>Stable-id actor override reset.</summary>
    public GestureResult ClearActorOverrides(
        IReadOnlyList<TransformTargetId> targets)
    {
        foreach (var target in targets)
        {
            if (target.Kind != TransformTargetKind.Actor)
                return GestureResult.Fail(
                    "Only actor targets can clear transform overrides.");
        }
        return _commands.ClearActorOverrides(
            targets,
            targets.Count == 1
                ? "Reset actor transform"
                : $"Reset {targets.Count} actor transforms");
    }

    private void AddLinkedBoneTargets(
        ICollection<TransformTargetId> targets)
    {
        foreach (var target in targets.ToArray())
        {
            if (target.Bone is not { } bone)
                continue;
            var actor = _scene.Snapshot.Actors.FirstOrDefault(
                candidate =>
                    candidate.Id == bone.Skeleton.Actor);
            var skeleton = actor?.Skeleton;
            if (skeleton == null)
                continue;

            var linkedNames = BoneLinkCatalog.GetLinked(
                bone.CanonicalName);
            foreach (var linkedName in linkedNames)
            {
                var linked = skeleton.Bones.FirstOrDefault(candidate =>
                    candidate.Id.Slot == bone.Slot &&
                    candidate.Id.PartialId == bone.PartialId &&
                    candidate.Id.CanonicalName.Equals(
                        linkedName,
                        StringComparison.Ordinal));
                if (linked != null)
                    targets.Add(TransformTargetId.ForBone(linked.Id));
            }
        }
    }

    private IReadOnlyDictionary<TransformTargetId, TransformDeltaMode>
        AddSymmetryTargets(
            ICollection<TransformTargetId> targets,
            TransformDeltaMode mode)
    {
        var modes =
            new Dictionary<TransformTargetId, TransformDeltaMode>();
        var existing = targets.ToHashSet();
        foreach (var target in targets.ToArray())
        {
            if (target.Bone is not { } bone)
                continue;
            var partnerName = MirrorName(bone.CanonicalName);
            if (partnerName == null)
                continue;
            var actor = _scene.Snapshot.Actors.FirstOrDefault(
                candidate =>
                    candidate.Id == bone.Skeleton.Actor);
            var partner = actor?.Skeleton?.Bones.FirstOrDefault(candidate =>
                candidate.Id.Slot == bone.Slot &&
                candidate.Id.PartialId == bone.PartialId &&
                candidate.Id.CanonicalName.Equals(
                    partnerName,
                    StringComparison.Ordinal));
            if (partner == null)
                continue;

            var partnerTarget =
                TransformTargetId.ForBone(partner.Id);
            if (!existing.Add(partnerTarget))
                continue;
            targets.Add(partnerTarget);
            modes[partnerTarget] = mode;
        }
        return modes;
    }

    private static string? MirrorName(string name)
    {
        if (name.EndsWith("_l", StringComparison.Ordinal))
            return string.Concat(name.AsSpan(0, name.Length - 2), "_r");
        if (name.EndsWith("_r", StringComparison.Ordinal))
            return string.Concat(name.AsSpan(0, name.Length - 2), "_l");
        return null;
    }
}
