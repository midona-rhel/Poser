namespace Poser.Config;

/// <summary>
/// How far one pixel of drag moves a numeric transform well. Brio's
/// "Transform Slider Speed" pair: a whole entity and a single bone are edited
/// at wildly different magnitudes, so they get separate speeds rather than one
/// compromise.
///
/// <para>Both defaults are the constant the rows were written with, so this
/// changes nothing until the user moves it. Rotation is deliberately absent:
/// degrees per pixel is not a magnitude that varies with the thing being
/// turned, and no reference exposes it.</para>
/// </summary>
public class TransformConfiguration
{
    /// <summary>Metres per pixel for an actor, prop, light or camera —
    /// position and scale alike.</summary>
    public float EntitySpeed { get; set; } = 0.005f;

    /// <summary>Metres per pixel for a single bone.</summary>
    public float BoneSpeed { get; set; } = 0.005f;

    /// <summary>The speed for the thing being edited. One call site's whole
    /// decision, so a row cannot pick the wrong one by writing the wrong
    /// field name.</summary>
    public float For(bool isBone) => isBone ? BoneSpeed : EntitySpeed;
}
