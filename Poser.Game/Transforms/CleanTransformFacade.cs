using System.Numerics;
using Poser.Application.Transforms;
using Poser.Application.Scene;
using Poser.Domain.Identity;
using Poser.Domain.Posing;
using Poser.Domain.Transforms;
using Poser.Entities;
using Poser.Game.Bindings;

namespace Poser.Game.Transforms;

/// <summary>Legacy UI bridge into the clean transform gesture application API.</summary>
public sealed class CleanTransformFacade
{
    private readonly StableBindingRegistry _bindings;
    private readonly SceneSession _scene;
    private readonly TransformGestureService _gestures;
    private readonly TransformCommandService _commands;

    public CleanTransformFacade(
        StableBindingRegistry bindings,
        SceneSession scene,
        TransformGestureService gestures,
        TransformCommandService commands)
    {
        _bindings = bindings;
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

    public GestureResult Begin(
        IReadOnlyList<IEntity> entities,
        TransformOperation operation,
        TransformSpace space,
        PivotMode pivotMode = PivotMode.PerTarget,
        Vector3? customPivot = null,
        string description = "Transform",
        bool includeLinkedBones = false,
        TransformDeltaMode? symmetry = null)
    {
        var targets = new List<TransformTargetId>(entities.Count);
        foreach (var entity in entities)
        {
            var target = GetTarget(entity);
            if (target == null)
                return GestureResult.Fail(
                    $"Entity {entity.Name} has no stable transform binding.");
            targets.Add(target.Value);
        }
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

    public GestureResult SetAbsolute(
        IEntity entity,
        Poser.Transform transform,
        string description)
    {
        var target = GetTarget(entity);
        if (target == null)
            return GestureResult.Fail(
                $"Entity {entity.Name} has no stable transform binding.");
        if (!PoseTransform.TryCreate(
                transform.Position,
                transform.Rotation,
                transform.Scale,
                out var desired,
                out var error))
            return GestureResult.Fail(
                error ?? "Transform is invalid.");
        return _commands.SetAbsolute(
            target.Value,
            desired,
            description);
    }

    public GestureResult ClearActorOverrides(
        IReadOnlyList<IActor> actors)
    {
        var targets = actors
            .Select(GetTarget)
            .Where(target => target.HasValue)
            .Select(target => target!.Value)
            .ToArray();
        if (targets.Length != actors.Count)
            return GestureResult.Fail(
                "One or more actors have no stable transform binding.");
        return _commands.ClearActorOverrides(
            targets,
            targets.Length == 1
                ? "Reset actor transform"
                : $"Reset {targets.Length} actor transforms");
    }

    public TransformTargetId? GetTarget(IEntity entity) =>
        entity switch
        {
            IActor actor when _bindings.GetActorId(actor) is { } id =>
                TransformTargetId.ForActor(id),
            IBone bone when bone is not VirtualBone &&
                            _bindings.GetBoneId(bone) is { } id =>
                TransformTargetId.ForBone(id),
            _ => null,
        };

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
