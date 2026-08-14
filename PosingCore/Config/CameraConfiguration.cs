using Poser.Entities;

namespace Poser.Config;

/// <summary>
/// The camera decisions that are the USER's rather than any one camera's:
/// what a newly created free camera starts out flying like, how much the
/// speed modifiers are worth, and how much of the game's own input Poser
/// takes off the table while a camera is live.
///
/// <para>Every default here is what Poser already did before the setting
/// existed, so a config written by an older build changes nothing.</para>
/// </summary>
public class CameraConfiguration
{
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
    /// Whether Space, Shift, Ctrl and Alt are eaten while a live free camera
    /// is flying — Brio's <c>EnableKeyHandlingOnKeyMod</c>, on by default in
    /// both tools. Off hands them back to the game, which is what a user who
    /// wants to jump or sprint between shots is asking for; the cost is that
    /// descending also holds a modifier down for the game.
    /// </summary>
    public bool ConsumeModifiersWhileFlying { get; set; } = true;

    /// <summary>
    /// Whether every key is eaten while in GPose — Brio's
    /// <c>EnableConsumeAllInput</c>, off by default. Escape and Enter are
    /// never taken: they are how a user leaves a game dialog, and a plugin
    /// that swallows them strands them.
    /// </summary>
    public bool ConsumeAllGameInput { get; set; }

    /// <summary>
    /// Whether the lateral and vertical fly keys invert once the camera is
    /// rolled past a quarter turn — Brio's <c>FlipKeyBindsPastNinety</c>, off
    /// by default. Matches what the game's own camera does when you fly
    /// upside down: the key that moved you screen-left keeps moving you
    /// screen-left.
    /// </summary>
    public bool FlipBindsPastNinety { get; set; }
}
