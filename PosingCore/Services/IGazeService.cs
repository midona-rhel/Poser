using System.Numerics;
using Poser.Entities;

namespace Poser.Services;

/// <summary>
/// Target mode for gaze control.
/// </summary>
public enum GazeTargetMode
{
    /// <summary>No gaze override - use game default.</summary>
    None,
    /// <summary>Look at a computed point ahead of the actor.</summary>
    Forward,
    /// <summary>Look at the live camera position.</summary>
    Camera,
    /// <summary>Look at another actor, targeted by stable game-object id.</summary>
    Entity,
    /// <summary>Look at a fixed world point (Brio Position mode / Ktisis gizmo target).</summary>
    Position
}

/// <summary>
/// Which body parts should be affected by gaze control.
/// </summary>
[System.Flags]
public enum GazeTargetType
{
    None = 0,
    Body = 1,
    Head = 4,
    Eyes = 8,
    All = Body | Head | Eyes
}

/// <summary>
/// Read snapshot of an actor's managed gaze state. Durable identity is never
/// an <see cref="IActor"/> reference: the service keys state by the native
/// GameObjectId and the Entity target is a GameObjectId, both of which
/// survive ordinary actor-list refreshes.
/// </summary>
public class GazeState
{
    public GazeTargetMode Mode { get; set; } = GazeTargetMode.None;
    public GazeTargetType TargetType { get; set; } = GazeTargetType.All;

    /// <summary>The Entity-mode target's GameObjectId; 0 when unset.</summary>
    public ulong TargetId { get; set; }

    /// <summary>The shared Position-mode anchor — what the world gizmo grabs.</summary>
    public Vector3 Position { get; set; }

    /// <summary>The eyes' live target position.</summary>
    public Vector3 EyesPosition { get; set; }

    /// <summary>The head's live target position.</summary>
    public Vector3 HeadPosition { get; set; }

    /// <summary>The body's live target position.</summary>
    public Vector3 BodyPosition { get; set; }
}

/// <summary>
/// Service for controlling actor gaze (where they look). IActor parameters
/// are frame-scoped resolution inputs only — nothing retains them. Every
/// setter performs exactly one state transition.
/// </summary>
public interface IGazeService
{
    /// <summary>Snapshot of the actor's managed gaze state.</summary>
    GazeState GetGazeState(IActor actor);

    /// <summary>
    /// One mode transition. Entering a non-Off mode with no participating
    /// parts enables all three. Entity mode without a chosen target performs
    /// no native override until a target is set.
    /// </summary>
    void SetGazeMode(IActor actor, GazeTargetMode mode);

    /// <summary>
    /// Changes part participation only. Turning off the final active part
    /// returns the mode to Off.
    /// </summary>
    void SetGazeParts(IActor actor, GazeTargetType parts);

    /// <summary>
    /// Chooses the Entity-mode target and switches to Entity mode. The
    /// source actor itself is rejected.
    /// </summary>
    void SetGazeTarget(IActor actor, IActor target);

    /// <summary>
    /// The current Entity target's live address resolved at call time
    /// (for display matching); 0 when none or no longer present.
    /// </summary>
    nint GetGazeTargetAddress(IActor actor);

    /// <summary>
    /// Position mode only: moves the shared anchor and every enabled,
    /// unlocked part to <paramref name="position"/>. No-op in any other mode
    /// or when no entry exists.
    /// </summary>
    void SetGazePosition(IActor actor, Vector3 position);

    /// <summary>
    /// Position mode only: writes one part's target position explicitly.
    /// Works on locked parts too — an explicit user edit outranks a lock, and
    /// the lock flag itself is untouched. Does not move the anchor.
    /// </summary>
    void SetPartPosition(IActor actor, GazeTargetType part, Vector3 position);

    /// <summary>
    /// Brio's "set to camera value": <see cref="SetPartPosition"/> with the
    /// current camera position.
    /// </summary>
    void SnapPartToCamera(IActor actor, GazeTargetType part);

    /// <summary>
    /// Freezes/unfreezes one participating part at its actual current
    /// target. Does not change the mode, the participation mask, or other
    /// parts.
    /// </summary>
    void SetPartLock(IActor actor, GazeTargetType part, bool locked);

    /// <summary>Whether the given part is frozen.</summary>
    bool IsPartLocked(IActor actor, GazeTargetType part);

    /// <summary>Whether any Poser gaze override is active for the actor.</summary>
    bool IsGazeEnabled(IActor actor);

    /// <summary>Removes state and the native handle — full game default.</summary>
    void ResetGaze(IActor actor);
}
