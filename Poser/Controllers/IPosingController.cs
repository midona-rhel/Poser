using System.Numerics;
using Poser.Entities;
using Poser.Services;

namespace Poser.Controllers;

/// <summary>
/// Controller for posing operations with automatic history tracking.
/// UI components should use this instead of calling services directly.
/// </summary>
public interface IPosingController
{
    #region Actor Transforms

    /// <summary>
    /// Sets an actor's transform with history tracking.
    /// </summary>
    void SetActorTransform(IActor actor, Transform transform);

    /// <summary>
    /// Sets an actor's position with history tracking.
    /// </summary>
    void SetActorPosition(IActor actor, Vector3 position);

    /// <summary>
    /// Sets an actor's rotation with history tracking.
    /// </summary>
    void SetActorRotation(IActor actor, Quaternion rotation);

    /// <summary>
    /// Resets an actor's transform override.
    /// </summary>
    void ResetActorTransform(IActor actor);

    #endregion

    #region Bone Transforms

    /// <summary>
    /// Applies a transform to a bone with history tracking.
    /// </summary>
    void ApplyBoneTransform(IBone bone, Transform delta, Transform? originalModification = null);

    /// <summary>
    /// Resets a bone to its original pose with history tracking.
    /// </summary>
    void ResetBone(IBone bone);

    /// <summary>
    /// Resets all bones in a skeleton with history tracking.
    /// </summary>
    void ResetSkeleton(ISkeleton skeleton);

    #endregion

    #region Animation Control

    /// <summary>
    /// Toggles animation freeze state with history tracking.
    /// </summary>
    void ToggleFreeze(IActor actor);

    /// <summary>
    /// Sets animation freeze state with history tracking.
    /// </summary>
    void SetFrozen(IActor actor, bool frozen);

    /// <summary>
    /// Toggles physics freeze state with history tracking.
    /// </summary>
    void TogglePhysicsFreeze(IActor actor);

    /// <summary>
    /// Sets physics freeze state with history tracking.
    /// </summary>
    void SetPhysicsFrozen(IActor actor, bool frozen);

    /// <summary>
    /// Sets animation speed with history tracking.
    /// </summary>
    void SetAnimationSpeed(IActor actor, float speed);

    /// <summary>
    /// Begins a speed change operation (for slider dragging).
    /// Call EndSpeedChange when the drag ends.
    /// </summary>
    void BeginSpeedChange(IActor actor);

    /// <summary>
    /// Ends a speed change operation and records history.
    /// </summary>
    void EndSpeedChange(IActor actor, float finalSpeed);

    /// <summary>
    /// Sets animation time position.
    /// </summary>
    void SetAnimationTime(IActor actor, float time);

    #endregion

    #region Gaze Control

    /// <summary>
    /// Sets gaze mode with history tracking.
    /// </summary>
    void SetGazeMode(IActor actor, GazeTargetMode mode);

    /// <summary>
    /// Sets gaze target type (body parts) with history tracking.
    /// </summary>
    void SetGazeTargetType(IActor actor, GazeTargetType targetType);

    /// <summary>
    /// Sets gaze target entity with history tracking.
    /// </summary>
    void SetGazeTarget(IActor actor, IActor target);

    /// <summary>
    /// Resets gaze to default with history tracking.
    /// </summary>
    void ResetGaze(IActor actor);

    #endregion

    #region Visibility

    /// <summary>
    /// Toggles actor visibility with history tracking.
    /// </summary>
    void ToggleActorVisibility(IActor actor);

    /// <summary>
    /// Sets actor visibility with history tracking.
    /// </summary>
    void SetActorVisibility(IActor actor, bool visible);

    #endregion
}
