using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;

namespace Poser.UI;

/// <summary>One floating-menu entry.</summary>
public record struct ContextMenuItem
{
    public string Label;
    public TablerIcon Icon;
    public string? Shortcut;
    public bool Danger;
    public bool Disabled;
    public bool IsSeparator;

    public ContextMenuItem(
        string label,
        TablerIcon icon = TablerIcon.Circle,
        string? shortcut = null,
        bool danger = false,
        bool disabled = false)
    {
        Label = label;
        Icon = icon;
        Shortcut = shortcut;
        Danger = danger;
        Disabled = disabled;
        IsSeparator = false;
    }

    public static ContextMenuItem Separator => new() { IsSeparator = true, Label = string.Empty };
}

public static partial class LegacyCrystarium
{
    /// <summary>
    /// The ONE floating menu (Picto ContextMenu transcription): a 260px
    /// surface sized to its rows, hosted in its own overlay window with
    /// enough transparent margin that the shadow and outer ring are never
    /// clipped. Chrome: surface-1 at 92% over blur(13px) brightness(.7),
    /// directional glass borders, black 50% outer ring, 0 3px 12px
    /// black-30 shadow. Lifecycle: 100ms entrance from opacity 0 /
    /// scale .92 on Picto's default easing, 80ms ease-in exit toward
    /// .95, shift-clamped to the viewport with a 12px margin and the
    /// transform origin flipped to the shifted corner. A transparent
    /// backdrop swallows the outside press that closes it. Short action
    /// menus only — deliberately non-searchable.
    /// </summary>
    public static class FloatingMenu
    {
        private enum Phase { Hidden, Opening, Open, Closing }

        private static Transition Enter =>
            Transition.CubicBezier(
                Crystarium.ActiveTheme.Motion.Fast,
                0.4f, 0f, 0.22f, 1f);
        private static Transition Exit =>
            Transition.CubicBezier(
                Crystarium.ActiveTheme.Motion.MenuExit,
                0.42f, 0f, 1f, 1f); // CSS ease-in


        /// <summary>
        /// USER DEVIATION (recorded like selection-dominance): Picto's menu
        /// rows sit their text visibly below true center — even in the
        /// browser — because flex centers the LINE BOX while Segoe's
        /// ascent-heavy ink hangs low inside it. Poser centers the INK:
        /// label and shortcut rise by this logical offset so the text ink
        /// centroid meets the icon ink centroid at the row center
        /// (verified by measurement in the conformance capture).
        /// </summary>
        private const float RowInkRise = -2f;

        private static Phase _phase;
        private static string _id = string.Empty;
        private static ContextMenuItem[] _items = Array.Empty<ContextMenuItem>();
        private static Vector2 _min;
        private static Vector2 _size;
        private static Vector2 _pivot;
        private static double _phaseStart;
        private static int _lastOwnerFrame = -1;
        private static int _openedFrame = -1;

        /// <summary>Opens the menu for <paramref name="id"/> at the given
        /// screen position (typically the mouse), replacing any open menu.
        /// Items freeze at open.</summary>
        public static void Open(string id, Vector2 position, ContextMenuItem[] items)
        {
            if (_phase != Phase.Hidden && _id == id)
            {
                StartClose();
                return;
            }

            Interactive.ClaimExclusive(ExclusiveKey(id));
            float s = ImGuiHelpers.GlobalScale;
            _id = id;
            _items = items;
            _size = new Vector2(
                Crystarium.ActiveTheme.Floating.MenuWidth * s,
                HeightFor(items, s));
            _min = FloatingSurface.PlaceAtPoint(
                position,
                _size,
                s,
                out _pivot);

            _phase = Phase.Opening;
            _phaseStart = ImGui.GetTime();
            _openedFrame = ImGui.GetFrameCount();
        }

        public static void DismissAll()
        {
            if (_phase != Phase.Hidden)
                Interactive.ReleaseExclusive(ExclusiveKey(_id));
            _phase = Phase.Hidden;
        }

        public static void EndFrame()
        {
            if (_phase != Phase.Hidden
                && _lastOwnerFrame != ImGui.GetFrameCount())
                DismissAll();
        }

        public static bool IsOpen(string id) => _phase != Phase.Hidden && _id == id;

        /// <summary>Instantly hides the menu (stale target).</summary>
        public static void Dismiss(string id)
        {
            if (_id == id)
                DismissAll();
        }

        private static void StartClose()
        {
            if ((_phase is Phase.Opening or Phase.Open)
                && ImGui.GetFrameCount() != _openedFrame)
            {
                _phase = Phase.Closing;
                _phaseStart = ImGui.GetTime();
            }
        }

        private static float HeightFor(ContextMenuItem[] items, float s)
        {
            float height = Crystarium.ActiveTheme.Floating.MenuPadding * 2f * s;
            for (int i = 0; i < items.Length; i++)
            {
                height += (items[i].IsSeparator
                    ? Crystarium.ActiveTheme.Floating.MenuSeparatorBlock
                    : Crystarium.ActiveTheme.Controls.ListRowHeight) * s;
                if (i > 0)
                    height += Crystarium.ActiveTheme.Floating.MenuRowGap * s;
            }
            return height;
        }

        /// <summary>
        /// Pumps the menu for its owning id; call every frame while the
        /// menu may be open. Returns the clicked item index exactly once,
        /// else -1.
        /// </summary>
        public static int Draw(string id)
        {
            if (_phase == Phase.Hidden || _id != id)
                return -1;
            // Hand-rolled surface, same handshake: claim on open, sync
            // every frame it draws, release on dismissal.
            if (!FloatingSurface.SyncExclusive(ExclusiveKey(id)))
            {
                DismissAll();
                return -1;
            }

            var pointer = ImGui.GetMousePos();
            bool pointerOverMenu =
                pointer.X >= _min.X
                && pointer.X < _min.X + _size.X
                && pointer.Y >= _min.Y
                && pointer.Y < _min.Y + _size.Y;
            bool outsidePressed =
                ImGui.IsMouseClicked(ImGuiMouseButton.Left)
                || ImGui.IsMouseClicked(ImGuiMouseButton.Right);
            if (ImGui.GetFrameCount() != _openedFrame
                && ((outsidePressed && !pointerOverMenu)
                    || ImGui.IsKeyPressed(ImGuiKey.Escape)))
            {
                DismissAll();
                return -1;
            }

            _lastOwnerFrame = ImGui.GetFrameCount();
            float s = ImGuiHelpers.GlobalScale;
            double now = ImGui.GetTime();
            float t = (float)(now - _phaseStart);

            // Lifecycle: 100ms in, 80ms out.
            float scale, alpha;
            bool interactive = false;
            switch (_phase)
            {
                case Phase.Opening:
                {
                    float k = Enter.Evaluate(Math.Clamp(
                        t / Crystarium.ActiveTheme.Motion.Fast,
                        0f,
                        1f));
                    scale = 0.92f + 0.08f * k;
                    alpha = k;
                    // The visual rect is transformed during entrance. Waiting
                    // one short transition before enabling input keeps the hit
                    // geometry identical to the rendered rows.
                    interactive = false;
                    if (t >= Crystarium.ActiveTheme.Motion.Fast)
                        _phase = Phase.Open;
                    break;
                }
                case Phase.Closing:
                {
                    if (t >= Crystarium.ActiveTheme.Motion.MenuExit)
                    {
                        DismissAll();
                        return -1;
                    }
                    float k = Exit.Evaluate(Math.Clamp(
                        t / Crystarium.ActiveTheme.Motion.MenuExit,
                        0f,
                        1f));
                    scale = 1f - 0.05f * k;
                    alpha = 1f - k;
                    break;
                }
                default:
                    scale = 1f;
                    alpha = 1f;
                    interactive = true;
                    break;
            }

            var io = ImGui.GetIO();
            var menuOwner = Interactive.BeginOwner(
                ExclusiveKey(_id),
                InteractionLayer.Popup,
                Vector2.Zero,
                io.DisplaySize);
            const ImGuiWindowFlags hostFlags =
                ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize
                | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoScrollbar
                | ImGuiWindowFlags.NoScrollWithMouse | ImGuiWindowFlags.NoSavedSettings
                | ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoBackground;

            // Transparent full-viewport backdrop: swallows the outside
            // press that closes the menu, exactly Picto's backdrop div.
            if (_phase is Phase.Opening or Phase.Open)
            {
                ImGui.SetNextWindowPos(Vector2.Zero);
                ImGui.SetNextWindowSize(io.DisplaySize);
                ImGui.SetNextWindowFocus();
                ImGui.Begin("##floating-menu-backdrop",
                    hostFlags | ImGuiWindowFlags.NoFocusOnAppearing);
                ImGui.SetCursorScreenPos(Vector2.Zero);
                Interactive.Reserve(
                    "##floating-menu-backdrop-hit",
                    io.DisplaySize,
                    disabled: false);
                ImGui.End();
            }

            float host = Crystarium.ActiveTheme.Floating.HostMargin * s;
            ImGui.SetNextWindowPos(_min - new Vector2(host, host));
            ImGui.SetNextWindowSize(_size + new Vector2(host, host) * 2f);
            ImGui.SetNextWindowFocus();
            ImGui.Begin("##floating-menu", hostFlags);
            var dl = ImGui.GetWindowDrawList();
            int vtxStart = dl.VtxBuffer.Size;
            int clicked = DrawSurfaceAndRows(dl, s, interactive);
            int vtxEnd = dl.VtxBuffer.Size;
            // The whole surface — shadow, ring, chrome, rows — pops as one
            // composited unit about the flip-aware transform origin.
            VertexTransform.ApplyPop(dl, vtxStart, vtxEnd, _pivot, scale, Vector2.Zero, alpha);
            ImGui.End();
            Interactive.EndOwner(menuOwner);

            if (clicked >= 0)
                StartClose();
            return clicked;
        }

        private static string ExclusiveKey(string id) =>
            $"floating-menu:{id}";

        private static int DrawSurfaceAndRows(ImDrawListPtr dl, float s, bool interactive)
        {
            var min = _min;
            var max = _min + _size;
            FloatingSurface.DrawChrome(
                dl,
                min,
                max,
                Crystarium.ActiveTheme.Radii.Surface);

            // Rows.
            int clicked = -1;
            float y = min.Y + Crystarium.ActiveTheme.Floating.MenuPadding * s;
            float left = min.X + Crystarium.ActiveTheme.Floating.MenuPadding * s;
            float right = max.X - Crystarium.ActiveTheme.Floating.MenuPadding * s;
            for (int i = 0; i < _items.Length; i++)
            {
                if (i > 0)
                    y += Crystarium.ActiveTheme.Floating.MenuRowGap * s;
                var item = _items[i];
                if (item.IsSeparator)
                {
                    // CSS .separator: 1px --color-border-secondary with
                    // 2px block margins. The Border token, not the
                    // hover-overlay it previously borrowed (equal in dark,
                    // different in lightgray).
                    float lineY = y + 2f * s;
                    ControlPaint.Separator(
                        dl,
                        new Vector2(left, lineY),
                        right,
                        s,
                        Crystarium.ActiveTheme.Border);
                    y += Crystarium.ActiveTheme.Floating.MenuSeparatorBlock * s;
                    continue;
                }

                var rowMin = new Vector2(left, y);
                var rowMax = new Vector2(
                    right,
                    y + Crystarium.ActiveTheme.Controls.ListRowHeight * s);
                bool hovered = false;
                if (interactive && !item.Disabled)
                {
                    ImGui.SetCursorScreenPos(rowMin);
                    var hit = Interactive.Reserve(
                        $"##fm-row{i}",
                        rowMax - rowMin,
                        disabled: false);
                    if (hit.Clicked)
                        clicked = i;
                    hovered = hit.Hovered;
                }

                if (hovered)
                    dl.AddRectFilled(rowMin, rowMax,
                        ImGui.ColorConvertFloat4ToU32(ColorEx.ApplyAlpha(
                            item.Danger
                                ? Crystarium.ActiveTheme.Chrome.DangerHover
                                : Crystarium.ActiveTheme.Chrome.WeakOverlay)),
                        Crystarium.ActiveTheme.Radii.Control * s);

                float rowAlpha = item.Disabled ? Crystarium.ActiveTheme.Chrome.DisabledOpacity : 1f;
                var text = (item.Danger ? Crystarium.ActiveTheme.Chrome.Danger : Crystarium.ActiveTheme.Chrome.Text).Fade(rowAlpha);
                // Raw tint: the canonical icon path applies the global
                // ImGui alpha exactly once inside the SVG renderer.
                var iconTint = text.Fade(hovered ? 1f : 0.8f);

                ImGui.SetCursorScreenPos(new Vector2(
                    rowMin.X + Crystarium.ActiveTheme.Floating.MenuRowPadding * s,
                    rowMin.Y + (Crystarium.ActiveTheme.Controls.ListRowHeight
                        - Crystarium.ActiveTheme.Controls.IconSize) * 0.5f * s));
                Icon(item.Icon, Crystarium.ActiveTheme.Controls.IconSize, iconTint);

                float textX = rowMin.X
                    + (Crystarium.ActiveTheme.Floating.MenuRowPadding
                        + Crystarium.ActiveTheme.Controls.IconSize
                        + Crystarium.ActiveTheme.Floating.MenuIconGap) * s;
                float rowHeightPx =
                    Crystarium.ActiveTheme.Controls.ListRowHeight * s;
                float rise = RowInkRise * s;
                float labelRight = rowMax.X
                    - Crystarium.ActiveTheme.Floating.MenuRowPadding * s;
                if (item.Shortcut is { Length: > 0 } shortcut)
                {
                    var shortcutStyle = new TextStyle
                    {
                        Size = Crystarium.ActiveTheme.Typography.CaptionSize,
                        Color = text.Fade(0.5f),
                    };
                    var shortcutSize =
                        LegacyCrystarium.MeasureText(shortcut, shortcutStyle);
                    LegacyCrystarium.TextAt(
                        new Vector2(
                            labelRight - shortcutSize.X,
                            rowMin.Y
                                + (rowHeightPx - shortcutSize.Y) * 0.5f
                                + rise),
                        shortcut,
                        shortcutStyle);
                    // CSS .shortcut padding-left: 28px — the minimum
                    // label-to-shortcut gap.
                    labelRight -= shortcutSize.X + 28f * s;
                }
                var labelStyle = new TextStyle
                {
                    Size = Crystarium.ActiveTheme.Typography.BodySize,
                    Color = text,
                };
                var labelSize =
                    LegacyCrystarium.MeasureText(item.Label, labelStyle);
                var labelPos = new Vector2(
                    textX,
                    rowMin.Y
                        + (rowHeightPx - labelSize.Y) * 0.5f
                        + rise);
                float labelWidth = MathF.Max(1f, labelRight - textX);
                // CSS .label: flex 1, ellipsis. Constrain ONLY on
                // overflow: the truncate path clips to the line box, and
                // Segoe's descenders reach a hair below it — an
                // unconditional clip shaved the bottom off 'g'.
                if (labelSize.X > labelWidth)
                    LegacyCrystarium.TextAt(
                        labelPos,
                        item.Label,
                        labelStyle,
                        TextConstraint.Truncate(labelWidth));
                else
                    LegacyCrystarium.TextAt(labelPos, item.Label, labelStyle);

                y += Crystarium.ActiveTheme.Controls.ListRowHeight * s;
            }

            return clicked;
        }
    }
}
