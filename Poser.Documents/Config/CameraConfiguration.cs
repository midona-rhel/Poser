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

    /// <summary>The fly speed a new free camera is created with — Brio's
    /// <c>DefaultFreeCameraMovementSpeed</c>. A per-camera Speed row still
    /// overrides it, and the wheel still steps that row.</summary>
    public float DefaultMovementSpeed { get; set; } = FreeCameraSpeed.Default;

    /// <summary>The look sensitivity a new free camera is created with —
    /// Brio's <c>DefaultFreeCameraMouseSensitivity</c>.</summary>
    public float DefaultMouseSensitivity { get; set; } = 0.1f;

    /// <summary>What holding Ctrl multiplies the fly speed by (Brio's ×3;
    /// Ktisis calls the same knob <c>WorkcamFastMulti</c>).</summary>
    public float FastMultiplier { get; set; } = 3f;

    /// <summary>What holding Alt multiplies the fly speed by (Brio's ×0.3,
    /// Ktisis's <c>WorkcamSlowMulti</c>).</summary>
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
