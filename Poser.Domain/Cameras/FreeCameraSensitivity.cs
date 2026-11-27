namespace Poser.Domain.Cameras;

/// <summary>
/// How far a right-drag turns a free camera, as the camera's Sensitivity row
/// and the Settings window's default offer it; next to <see cref="FreeCameraSpeed"/>.
/// </summary>
public static class FreeCameraSensitivity
{
    /// <summary>The sensitivity a new free camera starts with (Brio's default).</summary>
    public const float Default = 0.1f;

    /// <summary>Least sensitivity the sliders offer; below it a drag barely turns the view.</summary>
    public const float Minimum = 0.001f;

    /// <summary>Most sensitivity the sliders offer; above it a small drag spins the view.</summary>
    public const float Maximum = 0.2f;
}
