using Poser.Domain.Cameras;

namespace Poser.Documents.Config;

/// <summary>
/// Defaults for newly created free cameras and options that control how
/// camera input interacts with the game.
/// </summary>
public class CameraConfiguration
{
    /// <summary>Updates the game's GPose orbit point when the selected actor moves.
    /// This is deferred while the camera is locked or being dragged.</summary>
    public bool UpdateOrbitWithActorPosition { get; set; } = true;

    private float _defaultMovementSpeed = FreeCameraSpeed.Default;
    private float _defaultMouseSensitivity = FreeCameraSensitivity.Default;

    /// <summary>The fly speed a new free camera is created with (Brio's
    /// <c>DefaultFreeCameraMovementSpeed</c>). The camera's own Speed row and
    /// the mouse wheel change it afterwards. Kept within the Speed row's
    /// range, so a value from an edited settings file can't create a camera
    /// whose speed its own row can't show or reach.</summary>
    public float DefaultMovementSpeed
    {
        get => _defaultMovementSpeed;
        set => _defaultMovementSpeed = Math.Clamp(value, FreeCameraSpeed.Minimum, FreeCameraSpeed.Maximum);
    }

    /// <summary>The look sensitivity a new free camera is created with
    /// (Brio's <c>DefaultFreeCameraMouseSensitivity</c>), kept within the
    /// Sensitivity row's range for the same reason.</summary>
    public float DefaultMouseSensitivity
    {
        get => _defaultMouseSensitivity;
        set => _defaultMouseSensitivity = Math.Clamp(value, FreeCameraSensitivity.Minimum, FreeCameraSensitivity.Maximum);
    }

    /// <summary>What holding Shift multiplies the fly speed by (Brio's ×3;
    /// Ktisis calls it <c>WorkcamFastMulti</c>).</summary>
    public float FastMultiplier { get; set; } = 3f;

    /// <summary>What holding Ctrl multiplies the fly speed by (Brio's ×0.3;
    /// Ktisis calls it <c>WorkcamSlowMulti</c>).</summary>
    public float SlowMultiplier { get; set; } = 0.3f;

    /// <summary>
    /// Whether Poser prevents the game from receiving Space, Shift, Ctrl, and
    /// Alt while a free camera is active. When disabled, the game may respond
    /// to a modifier that is also being used to move the camera.
    /// </summary>
    public bool ConsumeModifiersWhileFlying { get; set; } = true;

    /// <summary>
    /// Whether the game is prevented from receiving a key combination that
    /// Poser handles. This avoids both applications acting on the same chord.
    /// </summary>
    public bool KeepBoundKeysFromGame { get; set; } = true;

    /// <summary>
    /// Whether the lateral and vertical fly keys invert once the camera is
    /// rolled past a quarter turn — Brio's <c>FlipKeyBindsPastNinety</c>, off
    /// by default. Matches what the game's own camera does when you fly
    /// upside down: the key that moved you screen-left keeps moving you
    /// screen-left.
    /// </summary>
    public bool FlipBindsPastNinety { get; set; }

    /// <summary>Whether selecting a camera also switches the game view to it.</summary>
    public bool LookThroughSelectedCamera { get; set; }
}
