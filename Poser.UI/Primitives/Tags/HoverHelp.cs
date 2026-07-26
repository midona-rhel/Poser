using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Poser.UI.Effects;

namespace Poser.UI;

/// <summary>Preferred side of the semantic target for a hover card.</summary>
public enum HoverHelpSide
{
    Top,
    Bottom,
    Left,
    Right,
}

public static partial class Crystarium
{
    /// <summary>
    /// The ONE explanatory hover-help renderer — a pixel transcription of
    /// picto's KbdTooltip (shared/ui/KbdTooltip/KbdTooltip.tsx): open
    /// delay 400 ms, close delay 0, six-pixel target offset, 150 ms
    /// ease-out pop; glass background with backdrop blur, one-pixel
    /// secondary border with the glass top edge, radius 4, and a
    /// 0 2px 8px black@30% shadow; one-line cards 24px tall with 6px
    /// horizontal padding, 13px text, a 4px content gap, and 16px kbd
    /// shortcut badges.
    ///
    /// Controls provide only a stable id, the semantic target rectangle,
    /// the explanation, an optional shortcut, and a preferred side; this
    /// class owns timing, animation, placement, chrome, and top-layer
    /// rendering. The card draws on the FOREGROUND draw list — above
    /// every Poser window, taking no input and affecting no layout,
    /// measurement, scrolling, or hover state. Only one card is ever
    /// visible: when several targets register in one frame (a form row
    /// over its own wells), the LAST registration — the most specific,
    /// drawn latest — wins. Moving directly between controls restarts
    /// the new id's delay; leaving closes immediately. The pop scales
    /// the card chrome 90→100% around its centre while the content
    /// fades — the closest draw-list equivalent of Mantine's pop.
    /// </summary>
    public static class HoverHelp
    {
        private const double OpenDelaySeconds = 0.4;
        private const double PopSeconds = 0.15;
        private const float TargetOffset = 6f;
        private const float CardHeight = 24f;
        private const float PaddingX = 6f;
        private const float ContentGap = 4f;
        private const float BadgeHeight = 16f;
        private const float BadgeMinWidth = 16f;
        private const float BadgePaddingX = 4f;

        private readonly record struct Candidate(
            string Id, Vector2 Min, Vector2 Max, string Text,
            string? Shortcut, HoverHelpSide Side, bool Instant);

        private static Candidate? _candidate;
        private static string? _pendingId;
        private static double _pendingSince;
        private static string? _openId;
        private static double _openedAt;

        /// <summary>
        /// Registers explanatory help for a hovered target. Call every
        /// frame the semantic target is hovered — including disabled
        /// controls explaining why they are unavailable. The 400 ms
        /// delay, single-card rule, and placement are handled here.
        /// </summary>
        public static void Explain(string id, Vector2 targetMin, Vector2 targetMax,
            string text, string? shortcut = null, HoverHelpSide side = HoverHelpSide.Bottom)
        {
            if (text.Length == 0)
                return;
            _candidate = new Candidate(id, targetMin, targetMax, text, shortcut, side, Instant: false);
        }

        /// <summary>
        /// Registers a truncation-only preview: the same chrome without
        /// the explanatory 400 ms delay.
        /// </summary>
        public static void Preview(string id, Vector2 targetMin, Vector2 targetMax,
            string text, HoverHelpSide side = HoverHelpSide.Bottom)
        {
            if (text.Length == 0)
                return;
            _candidate = new Candidate(id, targetMin, targetMax, text, null, side, Instant: true);
        }

        /// <summary>
        /// Draws the single visible card. Called exactly once per frame,
        /// after every window has drawn, so registrations from any pane
        /// are complete.
        /// </summary>
        public static void Render()
        {
            var candidate = _candidate;
            _candidate = null;
            double now = ImGui.GetTime();

            if (candidate is not { } c)
            {
                // Leaving closes immediately; nothing lingers.
                _pendingId = null;
                _openId = null;
                return;
            }

            if (_pendingId != c.Id)
            {
                // Moving directly to another control restarts ITS delay.
                _pendingId = c.Id;
                _pendingSince = now;
                _openId = null;
            }

            if (!c.Instant && now - _pendingSince < OpenDelaySeconds)
                return;

            if (_openId != c.Id)
            {
                // A stable id starts the pop exactly once; hovering in
                // place must not restart the animation every frame.
                _openId = c.Id;
                _openedAt = now;
            }

            float scale = ImGuiHelpers.GlobalScale;
            float t = (float)Math.Clamp((now - _openedAt) / PopSeconds, 0.0, 1.0);
            float ease = 1f - MathF.Pow(1f - t, 3f);

            var textFont = FontRegistry.Resolve(FontFamily.Default, 13f);
            var badgeFont = FontRegistry.Resolve(FontFamily.Default, 10f);
            bool textPushed = textFont is { Available: true };

            // Measure the one-line content: text, then optional 16px kbd
            // badges after 4px gaps.
            if (textPushed) textFont!.Push();
            var textSize = ImGui.CalcTextSize(c.Text);
            if (textPushed) textFont!.Pop();

            string[] keys = string.IsNullOrEmpty(c.Shortcut)
                ? Array.Empty<string>()
                : c.Shortcut!.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            Span<float> badgeWidths = keys.Length <= 8 ? stackalloc float[keys.Length] : new float[keys.Length];
            bool badgePushed = badgeFont is { Available: true };
            if (badgePushed) badgeFont!.Push();
            float badgesW = 0f;
            for (int i = 0; i < keys.Length; i++)
            {
                badgeWidths[i] = MathF.Max(
                    BadgeMinWidth * scale,
                    ImGui.CalcTextSize(keys[i]).X + 2f * BadgePaddingX * scale);
                badgesW += ContentGap * scale + badgeWidths[i];
            }
            if (badgePushed) badgeFont!.Pop();

            float cardW = PaddingX * scale + textSize.X + badgesW + PaddingX * scale;
            float cardH = CardHeight * scale;

            // Anchor to the centre of the semantic target on the
            // preferred side, flip when the viewport edge is closer than
            // the card, then clamp the remainder.
            var display = ImGui.GetIO().DisplaySize;
            var targetCenter = (c.Min + c.Max) * 0.5f;
            float offset = TargetOffset * scale;
            Vector2 pos = c.Side switch
            {
                HoverHelpSide.Top => new Vector2(targetCenter.X - cardW * 0.5f, c.Min.Y - offset - cardH),
                HoverHelpSide.Left => new Vector2(c.Min.X - offset - cardW, targetCenter.Y - cardH * 0.5f),
                HoverHelpSide.Right => new Vector2(c.Max.X + offset, targetCenter.Y - cardH * 0.5f),
                _ => new Vector2(targetCenter.X - cardW * 0.5f, c.Max.Y + offset),
            };
            switch (c.Side)
            {
                case HoverHelpSide.Bottom when pos.Y + cardH > display.Y:
                    pos.Y = c.Min.Y - offset - cardH;
                    break;
                case HoverHelpSide.Top when pos.Y < 0f:
                    pos.Y = c.Max.Y + offset;
                    break;
                case HoverHelpSide.Right when pos.X + cardW > display.X:
                    pos.X = c.Min.X - offset - cardW;
                    break;
                case HoverHelpSide.Left when pos.X < 0f:
                    pos.X = c.Max.X + offset;
                    break;
            }
            pos.X = Math.Clamp(pos.X, 0f, MathF.Max(0f, display.X - cardW));
            pos.Y = Math.Clamp(pos.Y, 0f, MathF.Max(0f, display.Y - cardH));

            // Pop: the chrome scales 90 → 100% about the card centre;
            // every color fades with the eased time.
            float pop = 0.9f + 0.1f * ease;
            var center = pos + new Vector2(cardW, cardH) * 0.5f;
            var half = new Vector2(cardW, cardH) * 0.5f * pop;
            var min = center - half;
            var max = center + half;
            float radius = 4f * scale;

            var fg = ImGui.GetForegroundDrawList();
            GlassChrome.PrependBlur(fg, min, max, radius);

            Vector4 Faded(Vector4 color) => color with { W = color.W * ease };
            var secondary = new Vector4(1f, 1f, 1f, 0.08f);
            BoxRenderer.Draw(fg, min, max, new BoxStyle
            {
                BackgroundColor = Faded(GlassChrome.BackgroundColor),
                BorderRadius = 4f,
                BorderWidth = 1f,
                BorderTopColor = Faded(Theme.Glass.BorderTop),
                BorderLeftColor = Faded(secondary),
                BorderRightColor = Faded(secondary),
                BorderBottomColor = Faded(secondary),
                BoxShadow = new BoxShadow(0f, 2f, 8f, Faded(new Vector4(0f, 0f, 0f, 0.30f))),
            });

            var theme = Norvrandt.Sheet.CurrentTheme;
            float x = min.X + PaddingX * scale;
            float midY = (min.Y + max.Y) * 0.5f;

            if (textPushed) textFont!.Push();
            fg.AddText(new Vector2(x, midY - textSize.Y * 0.5f),
                ImGui.ColorConvertFloat4ToU32(ColorEx.ApplyAlpha(Faded(theme.Text))), c.Text);
            if (textPushed) textFont!.Pop();
            x += textSize.X;

            if (keys.Length > 0)
            {
                if (badgePushed) badgeFont!.Push();
                uint badgeBg = ImGui.ColorConvertFloat4ToU32(
                    ColorEx.ApplyAlpha(Faded(new Vector4(1f, 1f, 1f, 0.10f))));
                uint badgeText = ImGui.ColorConvertFloat4ToU32(
                    ColorEx.ApplyAlpha(Faded(theme.Text)));
                for (int i = 0; i < keys.Length; i++)
                {
                    x += ContentGap * scale;
                    float bh = BadgeHeight * scale;
                    var bMin = new Vector2(x, midY - bh * 0.5f);
                    var bMax = new Vector2(x + badgeWidths[i], midY + bh * 0.5f);
                    fg.AddRectFilled(bMin, bMax, badgeBg, 3f * scale);
                    var keySize = ImGui.CalcTextSize(keys[i]);
                    fg.AddText(new Vector2(
                            bMin.X + (badgeWidths[i] - keySize.X) * 0.5f,
                            midY - keySize.Y * 0.5f),
                        badgeText, keys[i]);
                    x += badgeWidths[i];
                }
                if (badgePushed) badgeFont!.Pop();
            }
        }
    }
}
