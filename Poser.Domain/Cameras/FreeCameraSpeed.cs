using System;

namespace Poser.Domain.Cameras;

/// <summary>
/// Calculates free-camera speed changes and their display text. Each wheel
/// notch changes speed by the same proportion, within the editable range.
/// </summary>
public static class FreeCameraSpeed
{
    /// <summary>The default speed, displayed as 1×.</summary>
    public const float Default = 0.03f;

    /// <summary>Minimum editable movement speed.</summary>
    public const float Minimum = 0.005f;

    /// <summary>Maximum editable movement speed.</summary>
    public const float Maximum = 0.3f;

    /// <summary>Speed multiplier for one upward wheel notch.</summary>
    public const float NotchFactor = 1.15f;

    /// <summary>Windows' WHEEL_DELTA: one detent of a standard wheel.</summary>
    public const int WheelDelta = 120;

    /// <summary>
    /// Converts raw wheel input to whole notches. A nonzero value smaller
    /// than 120 counts as one notch; larger values can contain several.
    /// </summary>
    public static int Notches(int scrollValue)
    {
        if (scrollValue == 0)
            return 0;
        int detents = scrollValue / WheelDelta;
        return detents != 0 ? detents : Math.Sign(scrollValue);
    }

    /// <summary>
    /// Changes speed by the requested number of notches and clamps it to the
    /// editable range, even for zero notches. Nonfinite input uses Default.
    /// </summary>
    public static float Step(float speed, int notches)
    {
        if (!float.IsFinite(speed))
            return Default;
        float stepped = notches == 0
            ? speed
            : speed * MathF.Pow(NotchFactor, notches);
        return Math.Clamp(stepped, Minimum, Maximum);
    }

    /// <summary>Returns speed as a multiple of Default.</summary>
    public static float Multiplier(float speed) => speed / Default;

    /// <summary>Formats the multiplier with up to two decimal places.</summary>
    public static string Format(float speed) =>
        Multiplier(speed).ToString(
            "0.##×", System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>
/// A temporary speed-change notification. ChangedAtMs and Opacity's nowMs
/// must use the same monotonic clock, measured in milliseconds.
/// </summary>
public readonly record struct FreeCameraSpeedNotice(float Speed, long ChangedAtMs)
{
    /// <summary>Full opacity for this long after the change.</summary>
    public const long HoldMs = 700;

    /// <summary>Then this long to fade out — one second in total.</summary>
    public const long FadeMs = 300;

    /// <summary>The formatted speed multiplier.</summary>
    public string Text => FreeCameraSpeed.Format(Speed);

    /// <summary>
    /// Returns full opacity during the hold period, then fades to zero.
    /// A timestamp earlier than ChangedAtMs also returns full opacity.
    /// </summary>
    public float Opacity(long nowMs)
    {
        long age = nowMs - ChangedAtMs;
        if (age <= HoldMs)
            return 1f;
        long faded = age - HoldMs;
        return faded >= FadeMs ? 0f : 1f - (float)faded / FadeMs;
    }

    /// <summary>Whether the notification has any opacity left.</summary>
    public bool IsVisible(long nowMs) => Opacity(nowMs) > 0f;
}
