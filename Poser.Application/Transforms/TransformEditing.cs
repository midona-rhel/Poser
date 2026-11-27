using System.Numerics;
using Poser.Application.Posing;
using Poser.Application.Scene;
using Poser.Application.Settings;
using Poser.Domain.Identity;
using Poser.Domain.Posing;
using Poser.Domain.Transforms;

namespace Poser.Application.Transforms;

/// <summary>The one way the interface moves things: drags, typed values,
/// undo and redo.</summary>
public sealed class TransformEditing : ITransformEditing
{
    private readonly SceneSession _scene;
    private readonly TransformGestureService _gestures;
    private readonly TransformCommands _commands;
    private readonly GroupTransformCoordinator _groups;

    private readonly UndoJournal _journal;
    private readonly ValueJournal _values;
    private readonly ConfigurationService _configuration;
    private readonly EditorState _editor;
    private readonly IPoseInteraction _interaction;
    private readonly Viewport.IViewportReads _viewport;

    public TransformEditing(
        SceneSession scene,
        TransformGestureService gestures,
        TransformCommands commands,
        UndoJournal journal,
        GroupTransformCoordinator groups,
        ValueJournal values,
        ConfigurationService configuration,
        EditorState editor,
        IPoseInteraction interaction,
        Viewport.IViewportReads viewport)
    {
        _viewport = viewport;
        _configuration = configuration;
        _editor = editor;
        _interaction = interaction;
        _scene = scene;
        _gestures = gestures;
        _commands = commands;
        _journal = journal;
        _groups = groups;
        _values = values;
    }

    public TransformGestureId? ActiveGesture =>
        _gestures.ActiveGesture;

    /// <summary>The running gesture's frozen pivot, for a surface that draws a
    /// handle on a target the pivot makes orbit.</summary>
    public Vector3? ActivePivot => _gestures.ActivePivot;

    public PivotMode? ActivePivotMode => _gestures.ActivePivotMode;

    public bool CanUndo => _journal.CanUndo;
    public bool CanRedo => _journal.CanRedo;
    public string? UndoDescription => _journal.UndoDescription;
    public string? RedoDescription => _journal.RedoDescription;

    /// <summary>
    /// Starts a drag from a gizmo. The caller says what is dragged and whether it
    /// moves, turns or scales. The rest is decided here, so every gizmo behaves
    /// the same: the pivot, whether linked and paired bones come along, how a
    /// group scales, and what the undo step is called.
    /// </summary>
    public GestureResult BeginDrag(
        IReadOnlyList<TransformTargetId> targets,
        TransformOperation operation,
        TransformSpace space,
        IReadOnlyList<SelectionId>? selection = null)
    {
        var settings = _configuration.Config;
        bool bones = targets[0].Kind == TransformTargetKind.Bone;
        var (pivotMode, customPivot) = PivotFor(targets, operation);
        return Begin(targets, operation, space, pivotMode, customPivot,
            description: DragName(targets),
            includeLinkedBones: bones && _interaction.LinkedBonesEnabled,
            symmetryFor: bones ? SymmetryFor : null,
            relativeSecondaryBones: bones && settings.RelativeSecondaryBones,
            groupScale: settings.Gizmo.GroupScale,
            selection: selection);
    }

    /// <summary>What a drag turns and scales around. A bone turns around its own
    /// joint, or, with the rotate tool and Parent chosen on the toolbar, around
    /// the joint above it, fixed where that joint stood when the drag began.
    /// Several things that are not bones turn and scale around their shared
    /// centre.</summary>
    private (PivotMode Mode, Vector3? Custom) PivotFor(
        IReadOnlyList<TransformTargetId> targets, TransformOperation operation)
    {
        if (targets[0].Bone is { } bone)
            return operation == TransformOperation.Rotate && ParentPivotFor(bone) is { } parent
                ? (PivotMode.Custom, parent)
                : (PivotMode.PerTarget, null);
        return targets.Count > 1 ? (PivotMode.Centroid, null) : (PivotMode.PerTarget, null);
    }

    /// <summary>Where the joint above the bone is, when the rotate tool and Parent
    /// are chosen on the toolbar, so a turn goes around that joint. Null when the
    /// bone turns around its own joint, or has no joint above it.</summary>
    public Vector3? ParentPivotFor(BoneId bone) =>
        _editor.TransformTool == Domain.Preferences.TransformTool.Rotate &&
        _editor.RotationPivot == Domain.Preferences.RotationPivot.Parent
            ? _viewport.GetParentModelTransform(bone)?.Position
            : null;

    /// <summary>How a bone's paired bone follows: its own setting when per-bone
    /// symmetry is on, otherwise the toolbar's symmetry mode.</summary>
    private TransformDeltaMode? SymmetryFor(string boneName)
    {
        var settings = _configuration.Config;
        return BoneSymmetry.EffectiveMode(
            settings.PerBoneSymmetry,
            settings.BoneSymmetryOverrides,
            settings.AutoLinkPairedBones,
            _editor.SymmetryMode,
            boneName) switch
        {
            Domain.Preferences.SymmetryMode.Copy => TransformDeltaMode.Direct,
            Domain.Preferences.SymmetryMode.Mirror => TransformDeltaMode.Mirrored,
            _ => null,
        };
    }

    /// <summary>The undo step's name for a drag, such as "Transform 3 bones".</summary>
    private static string DragName(IReadOnlyList<TransformTargetId> targets)
    {
        var kind = targets[0].Kind;
        string noun = targets.Any(target => target.Kind != kind)
            ? "thing"
            : kind switch
            {
                TransformTargetKind.Actor => "actor",
                TransformTargetKind.Light => "light",
                TransformTargetKind.Prop => "object",
                TransformTargetKind.WorldObject => "world object",
                TransformTargetKind.Collider => "IK collider",
                _ => "bone",
            };
        return $"Transform {targets.Count} {noun}{(targets.Count == 1 ? "" : "s")}";
    }

    /// <summary>
    /// Starts a transform. Adds the linked and paired bones that follow the
    /// dragged bones, then hands the gesture to the gesture service.
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
        GroupScaleMode groupScale = GroupScaleMode.SizesAndSpacing,
        IReadOnlyList<SelectionId>? selection = null)
    {
        // Several entities, or one entity of a multi-entity selection, move
        // as a group: around their shared centre, in world space, and only
        // when the whole selection can move.
        var selected = selection ?? _scene.Selection.Selected;
        var groupTransform = (targetIds.Count > 1
            || Poser.Application.Selection.EntitySelection.IsMultiEntity(selected))
            && targetIds.All(target => target.Kind != TransformTargetKind.Bone);
        Guid? groupId = null;
        if (groupTransform)
        {
            if (!_groups.Admit(selected, targetIds, groupScale, out groupId, out var refusal))
                return GestureResult.Fail(refusal!);
            pivotMode = PivotMode.Centroid;
            space = TransformSpace.World;
        }
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
            groupScale,
            groupId,
            groupTransform));
    }

    public GestureResult Update(
        TransformGestureId id,
        TransformDelta delta) =>
        _gestures.Update(id, delta);

    public GestureResult Commit(TransformGestureId id) =>
        _gestures.Commit(id);

    public GestureResult Cancel(TransformGestureId id) =>
        _gestures.Cancel(id);

    public GestureResult Undo() { _values.Seal(); return _journal.Undo(); }
    public GestureResult Redo() { _values.Seal(); return _journal.Redo(); }

    public GestureResult UndoSelected() => ReplaySelected(before: true);
    public GestureResult RedoSelected() => ReplaySelected(before: false);

    private GestureResult ReplaySelected(bool before)
    {
        _values.Seal();
        var selected = _scene.Selection.Selected;
        if (selected.Count == 0 || selected.Any(id => _scene.Resolve(id) != id))
            return GestureResult.Fail("Select a current entity to undo or redo its changes.");
        var entities = selected.Select(EditHistory.EntityOf).Distinct().ToArray();
        if (entities.Length != 1 || entities[0].Kind == SceneEntityKind.Bone)
            return GestureResult.Fail("Select one entity, or bones belonging to one actor. Use global undo for groups.");
        return before ? _journal.Undo(entities[0]) : _journal.Redo(entities[0]);
    }

    /// <summary>Writes one transform as a single undo step, such as a value
    /// typed in the inspector.</summary>
    public GestureResult SetAbsolute(
        TransformTargetId target,
        PoseTransform desired,
        string description) =>
        _commands.SetAbsolute(target, desired, description);

    /// <summary>Releases the placement Poser set on these actors, as one undo
    /// step.</summary>
    public GestureResult ClearActorOverrides(
        IReadOnlyList<TransformTargetId> targets) =>
        _commands.ClearActorOverrides(
            targets,
            targets.Count == 1
                ? "Reset actor transform"
                : $"Reset {targets.Count} actor transforms");

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
            // Asked for each dragged bone: with per-bone symmetry, one drag
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
