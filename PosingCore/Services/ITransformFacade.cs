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

/// <summary>Transform gestures, absolute writes, undo and redo.</summary>
public interface ITransformFacade
{
    TransformGestureId? ActiveGesture { get; }
    Vector3? ActivePivot { get; }
    bool CanUndo { get; }
    bool CanRedo { get; }
    string? UndoDescription { get; }
    string? RedoDescription { get; }
    GestureResult Begin( IReadOnlyList<TransformTargetId> targetIds, TransformOperation operation, TransformSpace space, PivotMode pivotMode = PivotMode.PerTarget, Vector3? customPivot = null, string description = "Transform", bool includeLinkedBones = false, Func<string, TransformDeltaMode?>? symmetryFor = null, bool relativeSecondaryBones = false, GroupScaleMode groupScale = GroupScaleMode.SizesAndSpacing);
    GestureResult Update( TransformGestureId id, TransformDelta delta);
    GestureResult Commit(TransformGestureId id);
    GestureResult Cancel(TransformGestureId id);
    GestureResult Undo();
    GestureResult Redo();
    GestureResult SetAbsolute( TransformTargetId target, PoseTransform desired, string description);
    GestureResult ClearActorOverrides( IReadOnlyList<TransformTargetId> targets);
}
