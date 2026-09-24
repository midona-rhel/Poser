namespace Poser.Domain.Scene;

/// <summary>Where loaded content lands. Values retain their persisted numbering.</summary>
public enum ObjectPlacementMode
{
    AsSaved = 0,
    /// <summary>Preserve the saved camera offset, turned only by the change in yaw.</summary>
    RelativeToCamera = 1,
    /// <summary>The same placement rule anchored on the selected actor.</summary>
    RelativeToSelectedActor = 2,
    /// <summary>Place the content centroid in front of the current camera without turning it.</summary>
    InFrontOfCamera = 3,
}
