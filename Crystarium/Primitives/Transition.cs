namespace Poser.UI;

/// <summary>
/// CSS-shaped transition: how long it takes for animatable properties
/// (BackgroundColor, BorderColor, Color, Opacity, BorderRadius, BorderWidth,
/// Top/Right/Bottom/Left) to interpolate when state changes.
/// </summary>
public readonly struct Transition
{
    public readonly float DurationSeconds;
    public readonly Easing Ease;

    public Transition(float durationSeconds, Easing ease = Easing.Linear)
    {
        DurationSeconds = durationSeconds;
        Ease = ease;
    }

    public static readonly Transition Fast    = new(Theme.Duration.Fast);
    public static readonly Transition Default = new(Theme.Duration.Default);
    public static readonly Transition Slow    = new(Theme.Duration.Slow);
}

public enum Easing
{
    Linear,
    EaseOut,    // fast start, slow end (good for show-up)
    EaseIn,     // slow start, fast end
    EaseInOut,
}
