using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;

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
    /// The ONE explanatory hover-help renderer — a transcription of
    /// picto's KbdTooltip (shared/ui/KbdTooltip/KbdTooltip.tsx): open
    /// delay 400 ms, close delay 0, six-pixel target offset; glass
    /// background with backdrop blur, one-pixel secondary border with the
    /// glass top edge, radius 4, and a 0 2px 8px black@30% shadow;
    /// one-line cards 24px tall with 6px horizontal padding, 13px text, a
    /// 4px content gap, and 16px kbd shortcut badges.
    ///
    /// The animation is Mantine's <c>pop</c> transition, exactly: OUT is
    /// opacity 0, scale(.9), translateY(10px); IN is opacity 1, scale(1).
    /// Entering interpolates OUT→IN and exiting IN→OUT, each over 150 ms
    /// on the CSS <c>ease</c> curve (cubic-bezier 0.25, 0.1, 0.25, 1)
    /// with transform-origin at the card centre; the translation composes
    /// inside the scaled space (`scale(k) translateY(y)` ⇒ offset y·k),
    /// and the backdrop blur stays at constant strength while the blurred
    /// result fades with the group opacity. Close delay 0 means the
    /// EXIT BEGINS immediately when hover ends — the card retains its
    /// content and geometry and visibly reverses; it does not vanish. An
    /// interrupted entrance exits from its current visual state, and a
    /// re-entered target resumes its entrance the same way. The transform
    /// is applied to the COMPLETE composited card — blur surface,
    /// background, borders, shadow, text, and badges scale, rise, and
    /// fade as one surface via a captured vertex range.
    ///
    /// Controls provide only a stable id, the semantic target rectangle,
    /// the explanation, an optional shortcut, and a preferred side; this
    /// class owns timing, animation, placement, chrome, and top-layer
    /// rendering. The card draws on the FOREGROUND draw list — above
    /// every Poser window, taking no input and affecting no layout,
    /// measurement, scrolling, or hover state. Never more than one card
    /// renders: a directly entered target runs its own 400 ms delay while
    /// the old card exits, and a target that becomes ready supersedes any
    /// remaining exit. When several targets register in one frame (a form
    /// row over its own wells), the LAST registration — the most
    /// specific, drawn latest — wins.
    /// </summary>
    public static class HoverHelp
    {

        /// <summary>CSS default timing function `ease`.</summary>
        private static Transition PopEase =>
            Transition.CubicBezier(Crystarium.ActiveTheme.Motion.HoverPop,
                0.25f, 0.1f, 0.25f, 1f);

        private enum Phase { Hidden, Entering, Open, Exiting }

        private readonly record struct Candidate(
            uint Id, Vector2 Min, Vector2 Max, string Text,
            string? Shortcut, HoverHelpSide Side, bool Instant,
            InteractionOwner Owner);

        private static Candidate? _candidate;
        private static uint? _pendingId;
        private static double _pendingSince;
        private static Phase _phase = Phase.Hidden;
        private static Candidate _card;
        private static double _phaseStart;

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
            // Identity resolves through ImGui's ID stack at the
            // registration site, so equal raw strings in different
            // windows or ID scopes are distinct targets.
            _candidate = new Candidate(
                ImGui.GetID(id), targetMin, targetMax, text, shortcut, side,
                Instant: false, Interactive.CurrentOwner);
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
            _candidate = new Candidate(
                ImGui.GetID(id), targetMin, targetMax, text, null, side,
                Instant: true, Interactive.CurrentOwner);
        }

        /// <summary>
        /// Occlusion-aware hover test for GEOMETRIC help targets — form
        /// rows, label clusters, and disabled controls that have no live
        /// item. True only when the mouse is inside the rect AND the
        /// current window is the one under the mouse, so help cannot
        /// bleed through an overlapping window or popup.
        /// </summary>
        public static bool HelpHovered(Vector2 min, Vector2 max) =>
            !Interactive.PointerOccluded() &&
            ImGui.IsMouseHoveringRect(min, max) &&
            ImGui.IsWindowHovered(ImGuiHoveredFlags.ChildWindows |
                ImGuiHoveredFlags.AllowWhenBlockedByActiveItem);

        /// <summary>
        /// The ONE help gate for a reserved control: live hover from the
        /// hit result, falling back to the geometric test for disabled
        /// controls, which have no live item to hover.
        /// </summary>
        internal static bool Gate(
            in InteractionResult hit,
            bool disabled,
            Vector2 min,
            Vector2 max) =>
            hit.Hovered || (disabled && HelpHovered(min, max));

        /// <summary>
        /// Advances the state machine and draws the single card. Called
        /// exactly once per frame, after every window has drawn, so
        /// registrations from any pane are complete.
        /// </summary>
        public static void Render()
        {
            var candidate = _candidate;
            _candidate = null;
            if (candidate is { } registered
                && Interactive.PointerOccluded(
                    registered.Owner,
                    ImGui.GetMousePos()))
                candidate = null;
            double now = ImGui.GetTime();

            if (candidate is { } c)
            {
                if (_pendingId != c.Id)
                {
                    // Moving directly to another control restarts ITS delay.
                    _pendingId = c.Id;
                    _pendingSince = now;
                }

                bool ready = c.Instant || now - _pendingSince >= Crystarium.ActiveTheme.Motion.HoverOpenDelay;
                if (ready)
                {
                    if (_phase == Phase.Hidden || _card.Id != c.Id)
                    {
                        // A ready card supersedes anything still exiting:
                        // never two rendered cards.
                        _card = c;
                        _phase = Phase.Entering;
                        _phaseStart = now;
                    }
                    else
                    {
                        // Same id: follow the live target rect and text.
                        var wasExiting = _phase == Phase.Exiting;
                        float inness = CurrentInness(now);
                        _card = c;
                        if (wasExiting)
                        {
                            // Re-entered mid-exit: the entrance resumes
                            // from the current visual state, as a CSS
                            // transition would.
                            _phase = Phase.Entering;
                            _phaseStart = now - InverseProgress(inness) * Crystarium.ActiveTheme.Motion.HoverPop;
                        }
                    }
                }
                else if (_phase is Phase.Entering or Phase.Open)
                {
                    // Hover moved onto a still-pending target: the old
                    // card BEGINS its exit now (close delay 0 starts the
                    // exit; it does not remove the card).
                    BeginExit(now);
                }
            }
            else
            {
                _pendingId = null;
                if (_phase is Phase.Entering or Phase.Open)
                    BeginExit(now);
            }

            if (_phase == Phase.Entering && now - _phaseStart >= Crystarium.ActiveTheme.Motion.HoverPop)
                _phase = Phase.Open;
            if (_phase == Phase.Exiting && now - _phaseStart >= Crystarium.ActiveTheme.Motion.HoverPop)
                _phase = Phase.Hidden;
            if (_phase == Phase.Hidden)
                return;

            Draw(_card, CurrentInness(now));
        }

        /// <summary>Starts the IN→OUT exit from the CURRENT visual state,
        /// so an interrupted entrance reverses continuously.</summary>
        private static void BeginExit(double now)
        {
            float inness = CurrentInness(now);
            _phase = Phase.Exiting;
            _phaseStart = now - InverseProgress(1f - inness) * Crystarium.ActiveTheme.Motion.HoverPop;
        }

        /// <summary>How far IN (0 = Mantine OUT, 1 = Mantine IN) the card
        /// currently is on the ease curve.</summary>
        private static float CurrentInness(double now)
        {
            float p = (float)Math.Clamp((now - _phaseStart) / Crystarium.ActiveTheme.Motion.HoverPop, 0.0, 1.0);
            return _phase switch
            {
                Phase.Entering => PopEase.Evaluate(p),
                Phase.Open => 1f,
                Phase.Exiting => 1f - PopEase.Evaluate(p),
                _ => 0f,
            };
        }

        /// <summary>Linear progress whose eased value equals
        /// <paramref name="eased"/> (the curve is monotonic).</summary>
        private static float InverseProgress(float eased)
        {
            float lo = 0f, hi = 1f;
            for (int i = 0; i < 20; i++)
            {
                float mid = (lo + hi) * 0.5f;
                if (PopEase.Evaluate(mid) < eased) lo = mid; else hi = mid;
            }
            return (lo + hi) * 0.5f;
        }

        private static void Draw(in Candidate c, float inness)
        {
            float scale = ImGuiHelpers.GlobalScale;

            var textFont = FontRegistry.Resolve(FontFamily.Default, Crystarium.ActiveTheme.Typography.BodySize);
            var badgeFont = FontRegistry.Resolve(FontFamily.Default, Crystarium.ActiveTheme.Typography.ShortcutSize);
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
                    Crystarium.ActiveTheme.HoverHelp.BadgeMinimumWidth * scale,
                    ImGui.CalcTextSize(keys[i]).X + 2f * Crystarium.ActiveTheme.HoverHelp.BadgePaddingX * scale);
                badgesW += Crystarium.ActiveTheme.HoverHelp.ContentGap * scale + badgeWidths[i];
            }
            if (badgePushed) badgeFont!.Pop();

            float cardW = Crystarium.ActiveTheme.HoverHelp.PaddingX * scale + textSize.X + badgesW + Crystarium.ActiveTheme.HoverHelp.PaddingX * scale;
            float cardH = Crystarium.ActiveTheme.HoverHelp.CardHeight * scale;

            // Anchor to the centre of the semantic target on the
            // preferred side, flip when the viewport edge is closer than
            // the card, then clamp the remainder.
            float offset = Crystarium.ActiveTheme.HoverHelp.TargetOffset * scale;
            var pos = FloatingSurface.PlaceSide(
                c.Side,
                c.Min,
                c.Max,
                new Vector2(cardW, cardH),
                offset);

            // Mantine pop, transform-origin centre: scale .9 → 1 and
            // translateY 10px → 0 while opacity 0 → 1. CSS composes
            // `scale(k) translateY(y)` with the translation INSIDE the
            // scaled space, so the applied offset is y·k.
            float k = Crystarium.ActiveTheme.HoverHelp.PopScaleOut + (1f - Crystarium.ActiveTheme.HoverHelp.PopScaleOut) * inness;
            float rise = (1f - inness) * Crystarium.ActiveTheme.HoverHelp.PopRise * scale;
            var center = pos + new Vector2(cardW, cardH) * 0.5f;
            var translate = new Vector2(0f, rise * k);
            var animMin = center + (pos - center) * k + translate;
            var animMax = center + (pos + new Vector2(cardW, cardH) - center) * k + translate;
            float radius = Crystarium.ActiveTheme.Radii.Medium * scale;
            Interactive.RegisterOccluder(
                new InteractionOwner(
                    "hover-help",
                    InteractionLayer.HoverSurface,
                    int.MaxValue),
                animMin,
                animMax);

            var fg = ImGui.GetForegroundDrawList();
            // The blur runs at CONSTANT strength (picto keeps blur(16px)
            // fixed and fades the element); its command geometry is the
            // animated rect, and its own emitted vertices are alpha-faded
            // with the group opacity so the blurred result composites out
            // with the card instead of flashing.
            int blurVtxStart = fg.VtxBuffer.Size;
            GlassChrome.PrependHoverBlur(fg, animMin, animMax, radius * k);
            int blurVtxEnd = fg.VtxBuffer.Size;
            VertexTransform.ApplyPop(fg, blurVtxStart, blurVtxEnd, center, 1f, Vector2.Zero, inness);

            // Everything below lands in one captured vertex range and is
            // popped as one composited surface: background, borders,
            // shadow, text, and badges together.
            int vtxStart = fg.VtxBuffer.Size;

            var secondary = Crystarium.ActiveTheme.Chrome.WeakOverlay;
            BoxRenderer.Draw(fg, pos, pos + new Vector2(cardW, cardH), new BoxStyle
            {
                BackgroundColor = GlassChrome.BackgroundColor,
                BorderRadius = Crystarium.ActiveTheme.Radii.Medium,
                BorderWidth = 1f,
                BorderTopColor = Crystarium.ActiveTheme.Glass.BorderTop,
                BorderLeftColor = secondary,
                BorderRightColor = secondary,
                BorderBottomColor = secondary,
                BoxShadow = Crystarium.ActiveTheme.Shadows.HoverHelp,
            });

            var theme = Crystarium.ActiveTheme;
            float x = pos.X + Crystarium.ActiveTheme.HoverHelp.PaddingX * scale;
            float midY = pos.Y + cardH * 0.5f;

            if (textPushed) textFont!.Push();
            fg.AddText(new Vector2(x, midY - textSize.Y * 0.5f),
                ImGui.ColorConvertFloat4ToU32(ColorEx.ApplyAlpha(theme.Text)), c.Text);
            if (textPushed) textFont!.Pop();
            x += textSize.X;

            if (keys.Length > 0)
            {
                if (badgePushed) badgeFont!.Push();
                uint badgeBg = ImGui.ColorConvertFloat4ToU32(
                    ColorEx.ApplyAlpha(Crystarium.ActiveTheme.Chrome.ControlHover));
                uint badgeText = ImGui.ColorConvertFloat4ToU32(
                    ColorEx.ApplyAlpha(theme.Text));
                for (int i = 0; i < keys.Length; i++)
                {
                    x += Crystarium.ActiveTheme.HoverHelp.ContentGap * scale;
                    float bh = Crystarium.ActiveTheme.HoverHelp.BadgeHeight * scale;
                    var bMin = new Vector2(x, midY - bh * 0.5f);
                    var bMax = new Vector2(x + badgeWidths[i], midY + bh * 0.5f);
                    fg.AddRectFilled(bMin, bMax, badgeBg,
                        Crystarium.ActiveTheme.Radii.Small * scale);
                    var keySize = ImGui.CalcTextSize(keys[i]);
                    fg.AddText(new Vector2(
                            bMin.X + (badgeWidths[i] - keySize.X) * 0.5f,
                            midY - keySize.Y * 0.5f),
                        badgeText, keys[i]);
                    x += badgeWidths[i];
                }
                if (badgePushed) badgeFont!.Pop();
            }

            int vtxEnd = fg.VtxBuffer.Size;
            VertexTransform.ApplyPop(fg, vtxStart, vtxEnd, center, k, translate, inness);
        }
    }
}
