using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace Poser.UI;

/// <summary>
/// Per-id animator state: holds the last interpolated value of each animatable
/// property so that on the next frame we can lerp toward the new target.
///
/// Element keyed by <see cref="ElementProps.Id"/>. Elements without an id don't
/// animate (animator can't track them across frames).
/// </summary>
internal static class Animator
{
    [ThreadStatic]
    private static Dictionary<string, State>? _states;
    [ThreadStatic]
    private static int _lastEvictionFrame;

    private const int EvictionAfterFrames = 60;

    private struct State
    {
        public Vector4? BackgroundColor;
        public Vector4? BorderColor;
        public Vector4? Color;
        public float? Opacity;
        public float? BorderRadius;
        public float? BorderWidth;
        public float? Top, Right, Bottom, Left;
        public int LastSeenFrame;
    }

    /// <summary>
    /// Apply a transition to the resolved style. Returns a new style with values
    /// lerped from the last frame's interpolated state toward the target.
    /// </summary>
    public static ElementStyle Step(string? id, in ElementStyle target, in Transition transition)
    {
        if (string.IsNullOrEmpty(id) || transition.DurationSeconds <= 0f) return target;

        _states ??= new Dictionary<string, State>();
        int currentFrame = ImGui.GetFrameCount();
        EvictStale(currentFrame);

        if (!_states.TryGetValue(id, out var s))
        {
            s = new State
            {
                BackgroundColor = target.BackgroundColor,
                BorderColor = target.BorderColor,
                Color = target.Color,
                Opacity = target.Opacity,
                BorderRadius = target.BorderRadius,
                BorderWidth = target.BorderWidth,
                Top = target.Top,
                Right = target.Right,
                Bottom = target.Bottom,
                Left = target.Left,
                LastSeenFrame = currentFrame,
            };
            _states[id] = s;
            return target;
        }
        s.LastSeenFrame = currentFrame;

        float dt = ImGui.GetIO().DeltaTime;
        float linearT = MathF.Min(dt / transition.DurationSeconds, 1f);
        float t = transition.Evaluate(linearT);

        var result = target;

        if (target.BackgroundColor.HasValue)
        {
            var to = target.BackgroundColor.Value;
            var from = s.BackgroundColor ?? to;
            var lerped = Vector4.Lerp(from, to, t);
            result.BackgroundColor = lerped;
            s.BackgroundColor = lerped;
        }
        if (target.BorderColor.HasValue)
        {
            var to = target.BorderColor.Value;
            var from = s.BorderColor ?? to;
            var lerped = Vector4.Lerp(from, to, t);
            result.BorderColor = lerped;
            s.BorderColor = lerped;
        }
        if (target.Color.HasValue)
        {
            var to = target.Color.Value;
            var from = s.Color ?? to;
            var lerped = Vector4.Lerp(from, to, t);
            result.Color = lerped;
            s.Color = lerped;
        }
        if (target.Opacity.HasValue)
        {
            float to = target.Opacity.Value;
            float from = s.Opacity ?? to;
            float lerped = Lerp(from, to, t);
            result.Opacity = lerped;
            s.Opacity = lerped;
        }
        if (target.BorderRadius.HasValue)
        {
            float to = target.BorderRadius.Value;
            float from = s.BorderRadius ?? to;
            float lerped = Lerp(from, to, t);
            result.BorderRadius = lerped;
            s.BorderRadius = lerped;
        }
        if (target.BorderWidth.HasValue)
        {
            float to = target.BorderWidth.Value;
            float from = s.BorderWidth ?? to;
            float lerped = Lerp(from, to, t);
            result.BorderWidth = lerped;
            s.BorderWidth = lerped;
        }
        if (target.Top.HasValue)    { var to = target.Top.Value;    var from = s.Top    ?? to; var v = Lerp(from, to, t); result.Top    = v; s.Top    = v; }
        if (target.Right.HasValue)  { var to = target.Right.Value;  var from = s.Right  ?? to; var v = Lerp(from, to, t); result.Right  = v; s.Right  = v; }
        if (target.Bottom.HasValue) { var to = target.Bottom.Value; var from = s.Bottom ?? to; var v = Lerp(from, to, t); result.Bottom = v; s.Bottom = v; }
        if (target.Left.HasValue)   { var to = target.Left.Value;   var from = s.Left   ?? to; var v = Lerp(from, to, t); result.Left   = v; s.Left   = v; }

        _states[id] = s;
        return result;
    }

    private static void EvictStale(int currentFrame)
    {
        if (currentFrame == _lastEvictionFrame || _states == null || _states.Count == 0) return;
        _lastEvictionFrame = currentFrame;

        List<string>? stale = null;
        foreach (var kv in _states)
        {
            if (currentFrame - kv.Value.LastSeenFrame > EvictionAfterFrames)
                (stale ??= new List<string>()).Add(kv.Key);
        }
        if (stale == null) return;
        foreach (var key in stale) _states.Remove(key);
    }

    private static float Lerp(float a, float b, float t) => a + (b - a) * t;
}
