using System;
using System.Numerics;
using System.Text;
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

public static partial class LegacyCrystarium
{
    /// <summary>
    /// The ONE explanatory hover-help renderer — a transcription of
    /// picto's KbdTooltip (shared/ui/KbdTooltip/KbdTooltip.tsx): open
    /// delay 400 ms, close delay 0, six-pixel target offset; glass
    /// background with backdrop blur, one-pixel secondary border with the
    /// glass top edge, radius 4, and a 0 2px 8px black@30% shadow;
    /// one-line cards 24px tall with 6px horizontal padding, 13px text, a
    /// 4px content gap, and 16px kbd shortcut badges on their own 3px
    /// radius. The card declares no width, so it is content-sized and both
    /// paddings AND both border edges widen it.
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

        /// <summary>
        /// A registration's two INDEPENDENT timing opt-outs.
        /// <paramref name="Instant"/> drops the 400 ms open DELAY (what
        /// <see cref="Preview"/> asks for: a truncation readout must not
        /// make the reader wait). <paramref name="Animated"/> keeps or
        /// drops the 150 ms pop TRANSITION. They are separate axes because
        /// they answer separate questions — "how long before it appears"
        /// and "does its appearance move" — and a caller may want either
        /// one alone.
        /// </summary>
        private readonly record struct Candidate(
            uint Id, Vector2 Min, Vector2 Max, string Text,
            string? Shortcut, HoverHelpSide Side, bool Instant,
            bool Animated, InteractionOwner Owner);

        private static Candidate? _candidate;
        private static uint? _pendingId;
        private static double _pendingSince;
        private static Phase _phase = Phase.Hidden;
        private static Candidate _card;
        private static double _phaseStart;

        /// <summary>
        /// The CURRENT card's pop length — zero when the card opted out of
        /// animation. A zero-length transition completes in the frame it
        /// starts, so the entrance lands settled and the exit removes the
        /// card at once, while everything else about the card is untouched:
        /// the 400 ms delay, the single-card rule, placement, and chrome
        /// all read the same members they always did. This is the ONLY
        /// place the option acts. <see cref="Draw"/> is deliberately NOT
        /// branched — a card at inness 1 already runs scale 1, rise 0, and
        /// full opacity through the same vertex path, so an unanimated card
        /// is pixel-for-pixel a settled animated one.
        /// </summary>
        private static float PopDuration =>
            _card.Animated ? Crystarium.ActiveTheme.Motion.HoverPop : 0f;

        /// <summary>
        /// Registers explanatory help for a hovered target. Call every
        /// frame the semantic target is hovered — including disabled
        /// controls explaining why they are unavailable. The 400 ms
        /// delay, single-card rule, and placement are handled here.
        /// <paramref name="animated"/> false renders the card at its
        /// settled state — full scale, no rise, full opacity — the frame
        /// it opens and removes it the frame it closes; the 400 ms delay
        /// is unaffected, since motion and latency are separate axes.
        /// </summary>
        public static void Explain(string id, Vector2 targetMin, Vector2 targetMax,
            string text, string? shortcut = null, HoverHelpSide side = HoverHelpSide.Bottom,
            bool animated = true)
        {
            if (text.Length == 0)
                return;
            // Identity resolves through ImGui's ID stack at the
            // registration site, so equal raw strings in different
            // windows or ID scopes are distinct targets.
            _candidate = new Candidate(
                ImGui.GetID(id), targetMin, targetMax, text, shortcut, side,
                Instant: false, animated, Interactive.CurrentOwner);
        }

        /// <summary>
        /// Registers a truncation-only preview: the same chrome without
        /// the explanatory 400 ms delay. <paramref name="animated"/>
        /// carries the same meaning as on <see cref="Explain"/> — passing
        /// false alongside the preview's own instant open is how a caller
        /// asks for a readout with no timing behaviour at all.
        /// </summary>
        public static void Preview(string id, Vector2 targetMin, Vector2 targetMax,
            string text, HoverHelpSide side = HoverHelpSide.Bottom,
            bool animated = true)
        {
            if (text.Length == 0)
                return;
            _candidate = new Candidate(
                ImGui.GetID(id), targetMin, targetMax, text, null, side,
                Instant: true, animated, Interactive.CurrentOwner);
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
                            _phaseStart = now - InverseProgress(inness) * PopDuration;
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

            if (_phase == Phase.Entering && now - _phaseStart >= PopDuration)
                _phase = Phase.Open;
            if (_phase == Phase.Exiting && now - _phaseStart >= PopDuration)
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
            _phaseStart = now - InverseProgress(1f - inness) * PopDuration;
        }

        /// <summary>How far IN (0 = Mantine OUT, 1 = Mantine IN) the card
        /// currently is on the ease curve.</summary>
        private static float CurrentInness(double now)
        {
            float duration = PopDuration;
            // A zero-length transition is fully elapsed the instant it
            // starts, which is also the only sane reading of 0/0.
            float p = duration <= 0f
                ? 1f
                : (float)Math.Clamp((now - _phaseStart) / duration, 0.0, 1.0);
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

        /// <summary>
        /// <c>.content</c>: 13px regular in --color-text-primary.
        /// </summary>
        private static TextStyle ContentStyle => new()
        {
            Size = Crystarium.ActiveTheme.Typography.BodySize,
            Weight = FontWeight.Regular,
            Family = FontFamily.Default,
            Color = Crystarium.ActiveTheme.Text,
        };

        /// <summary>
        /// <c>.kbd</c>: 10px regular in --color-text-primary.
        /// </summary>
        private static TextStyle BadgeStyle => new()
        {
            Size = Crystarium.ActiveTheme.Typography.ShortcutSize,
            Weight = FontWeight.Regular,
            Family = FontFamily.Default,
            Color = Crystarium.ActiveTheme.Text,
        };

        /// <summary>
        /// Draws one styled run onto the card's FOREGROUND draw list.
        ///
        /// <para>This is the one place the card cannot use
        /// <see cref="LegacyCrystarium.TextAt(Vector2, string, in TextStyle)"/>:
        /// the canonical renderer emits into
        /// <c>ImGui.GetWindowDrawList()</c>, and the card is composited on
        /// <c>ImGui.GetForegroundDrawList()</c>. Routing the label through
        /// it would put the glyphs on a different list from the chrome —
        /// under every window instead of above them, and OUTSIDE the
        /// vertex range <see cref="VertexTransform.ApplyPop"/> captures,
        /// so the text would neither scale, rise, nor fade with the card.
        /// Style resolution, alpha, and presentation form still come from
        /// the canonical <see cref="TextStyle"/> path; only the draw list
        /// differs, and it can only be unified by giving Text.cs a draw-
        /// list overload.</para>
        /// </summary>
        private static void DrawRun(
            ImDrawListPtr drawList, Vector2 position, string text, in TextStyle style)
        {
            float size = style.Size ?? Crystarium.ActiveTheme.Typography.BodySize;
            var font = FontRegistry.Resolve(
                style.Family, style.Weight ?? FontWeight.Regular, size);
            bool pushed = font is { Available: true };
            if (pushed) font!.Push();
            try
            {
                drawList.AddText(
                    position,
                    ImGui.ColorConvertFloat4ToU32(ColorEx.ApplyAlpha(
                        style.Color ?? Crystarium.ActiveTheme.Text)),
                    // The canonical measurer normalizes to NFC before it
                    // measures, so the run drawn here must be the same
                    // sequence or the card would size for a different one.
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
            var help = Crystarium.ActiveTheme.HoverHelp;

            // Measure the one-line content through the CANONICAL text
            // measurer, so the card sizes on exactly the face, weight, and
            // presentation form the run will be drawn with: the label,
            // then optional 16px kbd badges after 4px gaps.
            var contentStyle = ContentStyle;
            var badgeStyle = BadgeStyle;
            var textSize = LegacyCrystarium.MeasureText(c.Text, contentStyle);

            string[] keys = string.IsNullOrEmpty(c.Shortcut)
                ? Array.Empty<string>()
                : c.Shortcut!.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            Span<float> badgeWidths = keys.Length <= 8 ? stackalloc float[keys.Length] : new float[keys.Length];
            Span<Vector2> keySizes = keys.Length <= 8 ? stackalloc Vector2[keys.Length] : new Vector2[keys.Length];
            float badgesW = 0f;
            for (int i = 0; i < keys.Length; i++)
            {
                keySizes[i] = LegacyCrystarium.MeasureText(keys[i], badgeStyle);
                // box-sizing: border-box with no border, so the padded
                // text width competes with min-width directly.
                badgeWidths[i] = MathF.Max(
                    help.BadgeMinimumWidth * scale,
                    keySizes[i].X + 2f * help.BadgePaddingX * scale);
                badgesW += help.ContentGap * scale + badgeWidths[i];
            }

            // The card declares a height but no width, so it is
            // content-sized: CSS adds BOTH horizontal paddings and BOTH
            // border edges outside the content box. BoxRenderer paints the
            // border inside the rect exactly as CSS does, so the border
            // must be part of the rect the content is inset from.
            float border = help.BorderWidth * scale;
            float cardW = 2f * border + help.PaddingX * scale
                + textSize.X + badgesW + help.PaddingX * scale;
            float cardH = help.CardHeight * scale;

            // Anchor to the centre of the semantic target on the
            // preferred side, flip when the viewport edge is closer than
            // the card, then clamp the remainder.
            float offset = help.TargetOffset * scale;
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
            float k = help.PopScaleOut + (1f - help.PopScaleOut) * inness;
            float rise = (1f - inness) * help.PopRise * scale;
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

            // border: 1px solid --color-border-secondary, with the top
            // edge replaced by --glass-border-top.
            var secondary = Crystarium.ActiveTheme.Border;
            BoxRenderer.Draw(fg, pos, pos + new Vector2(cardW, cardH), new BoxStyle
            {
                BackgroundColor = GlassChrome.BackgroundColor,
                BorderRadius = Crystarium.ActiveTheme.Radii.Medium,
                BorderWidth = help.BorderWidth,
                BorderTopColor = Crystarium.ActiveTheme.Glass.BorderTop,
                BorderLeftColor = secondary,
                BorderRightColor = secondary,
                BorderBottomColor = secondary,
                BoxShadow = Crystarium.ActiveTheme.Shadows.HoverHelp,
            });

            // The flex row runs inside the padding box, vertically centred
            // by `align-items: center` on a card whose height is fixed.
            float x = pos.X + border + help.PaddingX * scale;
            float midY = pos.Y + cardH * 0.5f;

            DrawRun(fg, new Vector2(x, midY - textSize.Y * 0.5f),
                c.Text, contentStyle);
            x += textSize.X;

            for (int i = 0; i < keys.Length; i++)
            {
                x += help.ContentGap * scale;
                float bh = help.BadgeHeight * scale;
                var bMin = new Vector2(x, midY - bh * 0.5f);
                var bMax = new Vector2(x + badgeWidths[i], midY + bh * 0.5f);
                // .kbd has a background and a 3px radius and NO border, so
                // it is the same shared box paint the card uses, with the
                // border members left unset.
                BoxRenderer.Draw(fg, bMin, bMax, new BoxStyle
                {
                    BackgroundColor = Crystarium.ActiveTheme.Chrome.ControlHover,
                    BorderRadius = help.BadgeRadius,
                });
                DrawRun(fg, new Vector2(
                        bMin.X + (badgeWidths[i] - keySizes[i].X) * 0.5f,
                        midY - keySizes[i].Y * 0.5f),
                    keys[i], badgeStyle);
                x += badgeWidths[i];
            }

            int vtxEnd = fg.VtxBuffer.Size;
            VertexTransform.ApplyPop(fg, vtxStart, vtxEnd, center, k, translate, inness);
        }
    }
}
