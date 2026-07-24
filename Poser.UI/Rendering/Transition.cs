using System;

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

    // Control points when Ease == Easing.CubicBezier (CSS cubic-bezier(x1,y1,x2,y2)).
    public readonly float BezierX1, BezierY1, BezierX2, BezierY2;

    public Transition(float durationSeconds, Easing ease = Easing.Linear)
    {
        DurationSeconds = durationSeconds;
        Ease = ease;
        BezierX1 = BezierY1 = BezierX2 = BezierY2 = 0f;
    }

    private Transition(float durationSeconds, float x1, float y1, float x2, float y2)
    {
        DurationSeconds = durationSeconds;
        Ease = Easing.CubicBezier;
        BezierX1 = Math.Clamp(x1, 0f, 1f);
        BezierY1 = y1;
        BezierX2 = Math.Clamp(x2, 0f, 1f);
        BezierY2 = y2;
    }

    /// <summary>CSS cubic-bezier(x1, y1, x2, y2) timing function.</summary>
    public static Transition CubicBezier(float durationSeconds, float x1, float y1, float x2, float y2)
        => new(durationSeconds, x1, y1, x2, y2);

    /// <summary>Evaluates this transition's easing at linear progress <paramref name="t"/> (0..1).</summary>
    public float Evaluate(float t) => Ease switch
    {
        Easing.EaseOut     => 1f - (1f - t) * (1f - t),
        Easing.EaseIn      => t * t,
        Easing.EaseInOut   => t < 0.5f ? 2f * t * t : 1f - 2f * (1f - t) * (1f - t),
        Easing.CubicBezier => SampleBezier(t),
        _ => t,
    };

    /// <summary>
    /// Solves y for the x = t on the CSS timing curve: find u where bezierX(u) = t
    /// (Newton–Raphson with bisection fallback), then return bezierY(u).
    /// </summary>
    private float SampleBezier(float t)
    {
        if (t <= 0f) return 0f;
        if (t >= 1f) return 1f;

        float u = t; // good initial guess for near-linear curves
        for (int i = 0; i < 8; i++)
        {
            float x = Cubic(u, BezierX1, BezierX2) - t;
            if (MathF.Abs(x) < 0.0005f)
                return Cubic(u, BezierY1, BezierY2);
            float dx = CubicDerivative(u, BezierX1, BezierX2);
            if (MathF.Abs(dx) < 1e-6f) break;
            u = Math.Clamp(u - x / dx, 0f, 1f);
        }

        // Bisection fallback (bezierX is monotonic for clamped x1/x2).
        float lo = 0f, hi = 1f;
        for (int i = 0; i < 20; i++)
        {
            u = (lo + hi) * 0.5f;
            if (Cubic(u, BezierX1, BezierX2) < t) lo = u; else hi = u;
        }
        return Cubic(u, BezierY1, BezierY2);
    }

    // Cubic bezier component with P0=0, P3=1: 3(1-u)²u·p1 + 3(1-u)u²·p2 + u³
    private static float Cubic(float u, float p1, float p2)
    {
        float inv = 1f - u;
        return 3f * inv * inv * u * p1 + 3f * inv * u * u * p2 + u * u * u;
    }

    private static float CubicDerivative(float u, float p1, float p2)
    {
        float inv = 1f - u;
        return 3f * inv * inv * p1 + 6f * inv * u * (p2 - p1) + 3f * u * u * (1f - p2);
    }

    public static readonly Transition Fast    = new(Theme.Duration.Fast);
    public static readonly Transition Default = new(Theme.Duration.Default);
    public static readonly Transition Slow    = new(Theme.Duration.Slow);

    /// <summary>picto --ease-default: cubic-bezier(0.4, 0, 0.22, 1) at --duration-normal (200ms).</summary>
    public static readonly Transition PictoDefault = CubicBezier(0.2f, 0.4f, 0f, 0.22f, 1f);

    /// <summary>picto --ease-default at --duration-fast (50ms).</summary>
    public static readonly Transition PictoFast = CubicBezier(0.05f, 0.4f, 0f, 0.22f, 1f);
}

public enum Easing
{
    Linear,
    EaseOut,    // fast start, slow end (good for show-up)
    EaseIn,     // slow start, fast end
    EaseInOut,
    CubicBezier, // control points carried on the Transition (CSS cubic-bezier)
}
