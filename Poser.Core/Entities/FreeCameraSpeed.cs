using System;

namespace Poser.Entities;

/// <summary>
/// The free camera's fly speed as the mouse wheel drives it. Pure
/// arithmetic on purpose: the input detour owns the wheel and the overlay
/// owns the ink, and neither of them owns the curve.
///
/// Neither reference project steers the fly speed with the wheel. Brio steps
/// the SAME MovementSpeed off held modifiers instead (Ctrl ×3, Alt ×0.3 —
/// VirtualCameraManager.Update) and edits it on a 0.005..0.3 slider
/// (CameraEditor); Ktisis multiplies its WorkcamMoveSpeed by Shift/Ctrl
/// factors (WorkCamera.UpdateKeyboard). Those Brio slider ends are the clamp
/// here — a wheel must not take the speed anywhere the Speed row cannot show
/// it back — and the step is geometric so every notch is worth the same
/// PROPORTION of the speed it started from, at 0.03 as at 0.3.
/// </summary>
public static class FreeCameraSpeed
{
    /// <summary>Brio's DefaultFreeCameraMovementSpeed. Also the unit the
    /// readout counts in: a camera at this speed reads 1×.</summary>
    public const float Default = 0.03f;

    /// <summary>Brio's movement-speed slider floor.</summary>
    public const float Minimum = 0.005f;

    /// <summary>Brio's movement-speed slider ceiling.</summary>
    public const float Maximum = 0.3f;

    /// <summary>What one notch is worth.</summary>
    public const float NotchFactor = 1.15f;

    /// <summary>Windows' WHEEL_DELTA: one detent of a standard wheel.</summary>
    public const int WheelDelta = 120;

    /// <summary>
    /// How many detents a frame's raw scroll value carries. The game's input
    /// frame reports the wheel in the units its device handed over, which is
    /// WHEEL_DELTA per detent on every ordinary mouse — but a frame that
    /// reports a plain ±1 must still count as a notch rather than round away,
    /// so the whole-detent division falls back to the sign. Dividing (rather
    /// than signing outright) keeps a frame that batched several detents
    /// worth all of them.
    /// </summary>
    public static int Notches(int scrollValue)
    {
        if (scrollValue == 0)
            return 0;
        int detents = scrollValue / WheelDelta;
        return detents != 0 ? detents : Math.Sign(scrollValue);
    }

    /// <summary>
    /// The speed after <paramref name="notches"/> detents, clamped to the
    /// editable range. The clamp applies at zero notches too: a speed that
    /// arrived out of range (an old scene document, a hand-edited value) is
    /// pulled back the first time the wheel touches it rather than being
    /// stepped further out.
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

    /// <summary>The speed as a multiple of the default — what the readout
    /// says.</summary>
    public static float Multiplier(float speed) => speed / Default;

    /// <summary>The readout itself: the multiplier, two decimals at most,
    /// with no trailing zeroes to read past.</summary>
    public static string Format(float speed) =>
        Multiplier(speed).ToString(
            "0.##×", System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>
/// What the last wheel notch left on screen: the speed it produced and the
/// moment it happened. The readout is a NOTICE rather than a live gauge —
/// it answers a change the user just made, holds long enough to be read, and
/// leaves. Times are milliseconds on any monotonic clock; the input detour
/// stamps <see cref="Environment.TickCount64"/> and the overlay asks with
/// the same clock.
/// </summary>
public readonly record struct FreeCameraSpeedNotice(float Speed, long ChangedAtMs)
{
    /// <summary>Full opacity for this long after the change.</summary>
    public const long HoldMs = 700;

    /// <summary>Then this long to fade out — one second in total.</summary>
    public const long FadeMs = 300;

    /// <summary>What the notice reads.</summary>
    public string Text => FreeCameraSpeed.Format(Speed);

    /// <summary>
    /// The notice's opacity now, 0 once it has gone. A time BEFORE the stamp
    /// reads as fresh rather than expired: the alternative is a readout that
    /// blinks out on any clock the caller reads out of order, and a notice
    /// that lingers one frame too long is the harmless failure.
    /// </summary>
    public float Opacity(long nowMs)
    {
        long age = nowMs - ChangedAtMs;
        if (age <= HoldMs)
            return 1f;
        long faded = age - HoldMs;
        return faded >= FadeMs ? 0f : 1f - (float)faded / FadeMs;
    }

    /// <summary>Whether the notice still puts anything on screen.</summary>
    public bool IsVisible(long nowMs) => Opacity(nowMs) > 0f;
}
