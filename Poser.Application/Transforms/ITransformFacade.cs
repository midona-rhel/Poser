using System;
using System.Collections.Generic;
using System.Numerics;
using Poser.Domain.Actors;
using Poser.Domain.Identity;
using Poser.Domain.Operations;
using Poser.Domain.Posing;
using Poser.Domain.Scene;
using Poser.Domain.Transforms;

namespace Poser.Application.Transforms;

/// <summary>Moving, turning and scaling things: drags, typed values, undo and
/// redo.</summary>
public interface ITransformFacade
{
    TransformGestureId? ActiveGesture { get; }
    Vector3? ActivePivot { get; }
    PivotMode? ActivePivotMode { get; }
    Vector3? ParentPivotFor(BoneId bone);
    bool CanUndo { get; }
    bool CanRedo { get; }
    string? UndoDescription { get; }
    string? RedoDescription { get; }
    GestureResult Begin(IReadOnlyList<TransformTargetId> targetIds, TransformOperation operation, TransformSpace space, PivotMode pivotMode = PivotMode.PerTarget, Vector3? customPivot = null, string description = "Transform", bool includeLinkedBones = false, Func<string, TransformDeltaMode?>? symmetryFor = null, bool relativeSecondaryBones = false, GroupScaleMode groupScale = GroupScaleMode.SizesAndSpacing, bool groupTransform = false, IReadOnlyList<SelectionId>? selection = null);
    GestureResult BeginDrag(IReadOnlyList<TransformTargetId> targets, TransformOperation operation, TransformSpace space, IReadOnlyList<SelectionId>? selection = null);
    /// <summary>Moves the drag's targets by the total change since it began.
    /// When this fails, the drag has ended and its targets are back where they
    /// started, so the caller only forgets the drag. The one exception is a
    /// restore from an earlier failed change that is still waiting: then the
    /// drag is refused and keeps running until that restore finishes.</summary>
    GestureResult Update(TransformGestureId id, TransformDelta delta);
    GestureResult Commit(TransformGestureId id);
    GestureResult Cancel(TransformGestureId id);
    GestureResult Undo();
    GestureResult Redo();
    GestureResult UndoSelected();
    GestureResult RedoSelected();
    GestureResult SetAbsolute(TransformTargetId target, PoseTransform desired, string description);
    GestureResult ClearActorOverrides(IReadOnlyList<TransformTargetId> targets);
}
