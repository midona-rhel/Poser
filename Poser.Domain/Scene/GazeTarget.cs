namespace Poser.Domain.Scene;

public readonly record struct GazeResult(bool Success, string? Detail = null)
{
    public static GazeResult Ok() => new(true);
    public static GazeResult Refused(string detail) => new(false, detail);
}

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
    Position,
    /// <summary>No target on any part, every frame: the eyes, head and
    /// body stay where the animation or pose put them. A duplicate's gaze —
    /// a paused copy turned its head after the camera (2026-09-02).</summary>
    Detached
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
