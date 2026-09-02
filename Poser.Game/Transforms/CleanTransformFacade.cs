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

    /// <summary>The running gesture's frozen pivot, for a surface that draws a
    /// handle on a target the pivot makes orbit.</summary>
    public Vector3? ActivePivot => _gestures.ActivePivot;

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
        Func<string, TransformDeltaMode?>? symmetryFor = null,
        bool relativeSecondaryBones = false,
        GroupScaleMode groupScale = GroupScaleMode.SizesAndSpacing)
    {
        var targets = new List<TransformTargetId>(targetIds);
        if (includeLinkedBones)
            AddLinkedBoneTargets(targets);
        var targetModes = symmetryFor != null
            ? AddSymmetryTargets(targets, symmetryFor)
            : null;
        return _gestures.Begin(new BeginTransformGesture(
            targets.Distinct().ToArray(),
            operation,
            space,
            pivotMode,
            customPivot,
            description,
            targetModes,
            relativeSecondaryBones,
            groupScale));
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
            // Linked lookup never crosses a slot boundary: partners resolve
            // only inside the source bone's own slot skeleton.
            var skeleton = actor?.GetSkeleton(bone.Slot);
            if (skeleton == null)
                continue;

            var linkedNames = BoneLinkCatalog.GetLinked(
                bone.CanonicalName);
            foreach (var linkedName in linkedNames)
            {
                var linked = skeleton.Bones.FirstOrDefault(candidate =>
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
            Func<string, TransformDeltaMode?> symmetryFor)
    {
        var modes =
            new Dictionary<TransformTargetId, TransformDeltaMode>();
        var existing = targets.ToHashSet();
        foreach (var target in targets.ToArray())
        {
            if (target.Bone is not { } bone)
                continue;
            // Resolved PER SOURCE BONE: with per-bone symmetry, one drag
            // can mirror one bone, link another, and leave a third alone.
            if (symmetryFor(bone.CanonicalName) is not { } mode)
                continue;
            var partnerName = MirrorName(bone.CanonicalName);
            if (partnerName == null)
                continue;
            var actor = _scene.Snapshot.Actors.FirstOrDefault(
                candidate =>
                    candidate.Id == bone.Skeleton.Actor);
            // Symmetry pairing happens within the source slot only.
            var partner = actor?.GetSkeleton(bone.Slot)?.Bones
                .FirstOrDefault(candidate =>
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
