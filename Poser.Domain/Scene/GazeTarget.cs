namespace Poser.Domain.Scene;

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
    /// <summary>Look at a fixed world-space point.</summary>
    Position,
    /// <summary>Clear game gaze targets so the eyes, head and body follow
    /// the animation or authored pose instead of a gaze target.</summary>
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
