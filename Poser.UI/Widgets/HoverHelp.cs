using System;
using System.Numerics;
using System.Text;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using static Poser.UI.Widgets.TextWidgets;
using static Poser.UI.Widgets.Themes;

namespace Poser.UI.Widgets;

public static class HoverHelp
{
    /// <summary>The hovered help candidate and the card on screen.</summary>
    internal sealed class HelpState
    {
        internal Candidate? Candidate;
        internal uint? PendingId;
        internal double PendingSince;
        internal Phase Phase = Phase.Hidden;
        internal Candidate Card;
        internal double PhaseStart;
    }

    private static HelpState State => UiContext.Current.Help;

    private static Transition PopEase =>
        Transition.CubicBezier(ActiveTheme.Motion.HoverPop,
            0.25f, 0.1f, 0.25f, 1f);

    internal enum Phase { Hidden, Entering, Open, Exiting }
    internal readonly record struct Candidate(
        uint Id, Vector2 Min, Vector2 Max, string Text,
        string? Shortcut, HoverHelpSide Side, bool Instant,
        bool Animated, InteractionOwner Owner, Vector2? Position = null,
        float Alpha = 1f, bool Mono = false);

    private static float PopDuration =>
        State.Card.Animated ? ActiveTheme.Motion.HoverPop : 0f;
    internal static BoxStyle SurfaceStyle
    {
        get
        {
            var secondary = ActiveTheme.Border;
            var help = ActiveTheme.HoverHelp;
            return new BoxStyle
            {
                BackgroundColor = GlassChrome.OpaqueBackgroundColor,
                BorderRadius = ActiveTheme.Radii.Medium,
                BorderWidth = help.BorderWidth,
                BorderTopColor = ActiveTheme.Glass.BorderTop,
                BorderLeftColor = secondary,
                BorderRightColor = secondary,
                BorderBottomColor = secondary,
                BoxShadow = ActiveTheme.Shadows.HoverHelp,
            };
        }
    }
    public static void Explain(string id, Vector2 targetMin, Vector2 targetMax,
        string text, string? shortcut = null, HoverHelpSide side = HoverHelpSide.Bottom,
        bool animated = true)
    {
        if (text.Length == 0)
            return;
        State.Candidate = new Candidate(
            ImGui.GetID(id), targetMin, targetMax, text, shortcut, side,
            Instant: false, animated, Interactive.CurrentOwner);
    }
    public static void Preview(string id, Vector2 targetMin, Vector2 targetMax,
        string text, HoverHelpSide side = HoverHelpSide.Bottom,
        bool animated = true)
    {
        if (text.Length == 0)
            return;
        State.Candidate = new Candidate(
            ImGui.GetID(id), targetMin, targetMax, text, null, side,
            Instant: true, animated, Interactive.CurrentOwner);
    }
    public static void Readout(Vector2 min, string text, float opacity = 1f)
    {
        if (text.Length == 0 || opacity <= 0f)
            return;
        // Numbers wiggle in the text face; a readout is numbers.
        var readout = new Candidate(
            0, min, min, text, null, HoverHelpSide.Bottom,
            Instant: true, Animated: false, Owner: Interactive.CurrentOwner,
            Position: min, Alpha: opacity, Mono: true);
        Draw(readout, 1f);
    }

    public static Vector2 ReadoutSize(string text)
    {
        float scale = ImGuiHelpers.GlobalScale;
        var help = ActiveTheme.HoverHelp;
        var style = ContentStyle with { Family = FontFamily.Mono };
        var textSize = MeasureText(text, style);
        float border = help.BorderWidth * scale;
        return new Vector2(
            2f * border + 2f * help.PaddingX * scale + textSize.X,
            help.CardHeight * scale);
    }
    public static bool HelpHovered(Vector2 min, Vector2 max) =>
        !Interactive.PointerOccluded() &&
        ImGui.IsMouseHoveringRect(min, max) &&
        ImGui.IsWindowHovered(ImGuiHoveredFlags.ChildWindows |
            ImGuiHoveredFlags.AllowWhenBlockedByActiveItem);
    internal static bool Gate(
        in InteractionResult hit,
        bool disabled,
        Vector2 min,
        Vector2 max) =>
        hit.Hovered || (disabled && HelpHovered(min, max));
    public static void Render()
    {
        var candidate = State.Candidate;
        State.Candidate = null;
        if (candidate is { } registered
            && Interactive.PointerOccluded(
                registered.Owner,
                ImGui.GetMousePos()))
            candidate = null;
        double now = ImGui.GetTime();

        if (candidate is { } c)
        {
            if (State.PendingId != c.Id)
            {
                State.PendingId = c.Id;
                State.PendingSince = now;
            }

            bool ready = c.Instant || now - State.PendingSince >= ActiveTheme.Motion.HoverOpenDelay;
            if (ready)
            {
                if (State.Phase == Phase.Hidden || State.Card.Id != c.Id)
                {
                    State.Card = c;
                    State.Phase = Phase.Entering;
                    State.PhaseStart = now;
                }
                else
                {
                    var wasExiting = State.Phase == Phase.Exiting;
                    float inness = CurrentInness(now);
                    State.Card = c;
                    if (wasExiting)
                    {
                        State.Phase = Phase.Entering;
                        State.PhaseStart = now - InverseProgress(inness) * PopDuration;
                    }
                }
            }
            else if (State.Phase is Phase.Entering or Phase.Open)
            {
                BeginExit(now);
            }
        }
        else
        {
            State.PendingId = null;
            if (State.Phase is Phase.Entering or Phase.Open)
                BeginExit(now);
        }

        if (State.Phase == Phase.Entering && now - State.PhaseStart >= PopDuration)
            State.Phase = Phase.Open;
        if (State.Phase == Phase.Exiting && now - State.PhaseStart >= PopDuration)
            State.Phase = Phase.Hidden;
        if (State.Phase == Phase.Hidden)
            return;

        Draw(State.Card, CurrentInness(now));
    }
    private static void BeginExit(double now)
    {
        float inness = CurrentInness(now);
        State.Phase = Phase.Exiting;
        State.PhaseStart = now - InverseProgress(1f - inness) * PopDuration;
    }
    private static float CurrentInness(double now)
    {
        float duration = PopDuration;
        float p = duration <= 0f
            ? 1f
            : (float)Math.Clamp((now - State.PhaseStart) / duration, 0.0, 1.0);
        return State.Phase switch
        {
            Phase.Entering => PopEase.Evaluate(p),
            Phase.Open => 1f,
            Phase.Exiting => 1f - PopEase.Evaluate(p),
            _ => 0f,
        };
    }
    private static float InverseProgress(float eased)
    {
        // Twenty bisection steps invert the monotonic easing curve without a closed-form inverse.
        float lo = 0f, hi = 1f;
        for (int i = 0; i < 20; i++)
        {
            float mid = (lo + hi) * 0.5f;
            if (PopEase.Evaluate(mid) < eased) lo = mid; else hi = mid;
        }
        return (lo + hi) * 0.5f;
    }
    private static TextStyle ContentStyle => new()
    {
        Size = ActiveTheme.Typography.BodySize,
        Weight = FontWeight.Regular,
        Family = FontFamily.Default,
        Color = ActiveTheme.Text,
    };
    private static TextStyle BadgeStyle => new()
    {
        Size = ActiveTheme.Typography.ShortcutSize,
        Weight = FontWeight.Regular,
        Family = FontFamily.Default,
        Color = ActiveTheme.Text,
    };
    private static void DrawRun(
        ImDrawListPtr drawList, Vector2 position, string text, in TextStyle style)
    {
        float size = style.Size ?? ActiveTheme.Typography.BodySize;
        var font = FontRegistry.Resolve(
            style.Family, style.Weight ?? FontWeight.Regular, size);
        bool pushed = font is { Available: true };
        if (pushed) font!.Push();
        try
        {
            drawList.AddText(
                position,
                ImGui.ColorConvertFloat4ToU32(ColorEx.ApplyAlpha(
                    style.Color ?? ActiveTheme.Text)),
                text.IsNormalized(NormalizationForm.FormC)
                    ? text
                    : text.Normalize(NormalizationForm.FormC));
        }
        finally
        {
            if (pushed) font!.Pop();
        }
    }

    private static void Draw(in Candidate c, float inness)
    {
        float scale = ImGuiHelpers.GlobalScale;
        var help = ActiveTheme.HoverHelp;
        var contentStyle = c.Mono
            ? ContentStyle with { Family = FontFamily.Mono }
            : ContentStyle;
        var badgeStyle = BadgeStyle;
        var textSize = MeasureText(c.Text, contentStyle);

        string[] keys = string.IsNullOrEmpty(c.Shortcut)
            ? Array.Empty<string>()
            : c.Shortcut!.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Span<float> badgeWidths = keys.Length <= 8 ? stackalloc float[keys.Length] : new float[keys.Length];
        Span<Vector2> keySizes = keys.Length <= 8 ? stackalloc Vector2[keys.Length] : new Vector2[keys.Length];
        float badgesW = 0f;
        for (int i = 0; i < keys.Length; i++)
        {
            keySizes[i] = MeasureText(keys[i], badgeStyle);
            badgeWidths[i] = MathF.Max(
                help.BadgeMinimumWidth * scale,
                keySizes[i].X + 2f * help.BadgePaddingX * scale);
            badgesW += help.ContentGap * scale + badgeWidths[i];
        }
        float border = help.BorderWidth * scale;
        float cardW = 2f * border + help.PaddingX * scale
            + textSize.X + badgesW + help.PaddingX * scale;
        float cardH = help.CardHeight * scale;
        float offset = help.TargetOffset * scale;
        var pos = c.Position ?? FloatingSurface.PlaceSide(
            c.Side,
            c.Min,
            c.Max,
            new Vector2(cardW, cardH),
            offset);
        float k = help.PopScaleOut + (1f - help.PopScaleOut) * inness;
        float rise = (1f - inness) * help.PopRise * scale;
        var center = pos + new Vector2(cardW, cardH) * 0.5f;
        // Translate after scaling so animation, bounds, and occlusion share the same card rect.
        var translate = new Vector2(0f, rise * k);
        var animMin = center + (pos - center) * k + translate;
        var animMax = center + (pos + new Vector2(cardW, cardH) - center) * k + translate;
        float radius = ActiveTheme.Radii.Medium * scale;
        if (!c.Instant)
            Interactive.RegisterOccluder(
                new InteractionOwner(
                    "hover-help",
                    InteractionLayer.HoverSurface,
                    int.MaxValue),
                animMin,
                animMax);

        var fg = ImGui.GetForegroundDrawList();
        // Tooltips animate over the scene without backdrop blur.
        float alpha = inness * c.Alpha;
        int vtxStart = fg.VtxBuffer.Size;
        BoxRenderer.Draw(
            fg,
            pos,
            pos + new Vector2(cardW, cardH),
            SurfaceStyle);
        float x = pos.X + border + help.PaddingX * scale;
        float midY = pos.Y + cardH * 0.5f;

        DrawRun(fg, new Vector2(x, InkSeatY(
                pos.Y, cardH, textSize.Y, contentStyle)),
            c.Text, contentStyle);
        x += textSize.X;

        for (int i = 0; i < keys.Length; i++)
        {
            x += help.ContentGap * scale;
            float bh = help.BadgeHeight * scale;
            var bMin = new Vector2(x, midY - bh * 0.5f);
            var bMax = new Vector2(x + badgeWidths[i], midY + bh * 0.5f);
            BoxRenderer.Draw(fg, bMin, bMax, new BoxStyle
            {
                BackgroundColor = ActiveTheme.Chrome.ControlHover,
                BorderRadius = help.BadgeRadius,
            });
            DrawRun(fg, new Vector2(
                    bMin.X + (badgeWidths[i] - keySizes[i].X) * 0.5f,
                    InkSeatY(
                        bMin.Y, bh, keySizes[i].Y, badgeStyle)),
                keys[i], badgeStyle);
            x += badgeWidths[i];
        }

        int vtxEnd = fg.VtxBuffer.Size;
        VertexTransform.ApplyPop(fg, vtxStart, vtxEnd, center, k, translate, alpha);
    }
}
