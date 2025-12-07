using Poser.Entities;

namespace Poser.Services;

/// <summary>
/// Target mode for gaze control.
/// </summary>
public enum GazeTargetMode
{
    /// <summary>No gaze override - use game default.</summary>
    None,
    /// <summary>Look straight ahead (frozen gaze).</summary>
    Forward,
    /// <summary>Look at the camera position.</summary>
    Camera,
    /// <summary>Look at a specific entity.</summary>
    Entity
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
/// Gaze state for an actor.
/// </summary>
public class GazeState
{
    public GazeTargetMode Mode { get; set; } = GazeTargetMode.None;
    public GazeTargetType TargetType { get; set; } = GazeTargetType.All;
    public IActor? TargetEntity { get; set; }

    /// <summary>
    /// Creates a copy of this gaze state.
    /// </summary>
    public GazeState Clone() => new()
    {
        Mode = Mode,
        TargetType = TargetType,
        TargetEntity = TargetEntity
    };
}

/// <summary>
/// Service for controlling actor gaze (where they look).
/// </summary>
public interface IGazeService
{
    /// <summary>
    /// Gets the current gaze state for an actor.
    /// </summary>
    GazeState GetGazeState(IActor actor);

    /// <summary>
    /// Sets the gaze mode for an actor.
    /// </summary>
    void SetGazeMode(IActor actor, GazeTargetMode mode);

    /// <summary>
    /// Sets which body parts are affected by gaze control.
    /// </summary>
    void SetGazeTargetType(IActor actor, GazeTargetType targetType);

    /// <summary>
    /// Sets an entity for the actor to look at.
    /// </summary>
    void SetGazeTarget(IActor actor, IActor target);

    /// <summary>
    /// Resets gaze to game default for an actor.
    /// </summary>
    void ResetGaze(IActor actor);

    /// <summary>
    /// Sets the complete gaze state for an actor.
    /// Used for history/undo support.
    /// </summary>
    void SetGazeState(IActor actor, GazeState state);
}
