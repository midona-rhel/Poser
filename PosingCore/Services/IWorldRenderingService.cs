namespace Poser.Services;

/// <summary>
/// World render toggles that are not part of the environment state (Brio's
/// WorldRenderingService). The freeze is a suppressed update, not a stored
/// value: releasing it hands the surface straight back to the game.
/// </summary>
public interface IWorldRenderingService
{
    /// <summary>Stops the water renderer's update, freezing the surface.</summary>
    bool IsWaterFrozen { get; set; }

    /// <summary>False when the water hook is unavailable; the freeze cannot be
    /// engaged and reports itself off.</summary>
    bool IsWaterFreezeAvailable { get; }

    bool ResetWaterOnGPoseExit { get; set; }
}
