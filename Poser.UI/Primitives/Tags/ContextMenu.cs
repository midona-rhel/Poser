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

public static partial class Crystarium
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

        // Picto ContextMenu.module.css values.
        private const float MenuWidth = 260f;
        private const float Pad = 4f;
        private const float Gap = 2f;
        private const float RowHeight = 26f;
        private const float RowPad = 6f;
        private const float RowRadius = 6f;
        private const float MenuRadius = 8f;
        private const float IconSize = 16f;
        private const float IconGap = 6f;
        private const float SeparatorBlock = 5f; // 1px line + 2px margins
        private const float ViewportMargin = 12f;
        private const float HostMargin = 24f;    // room for shadow + ring

        private static readonly Transition Enter =
            Transition.CubicBezier(0.10f, 0.4f, 0f, 0.22f, 1f);
        private static readonly Transition Exit =
            Transition.CubicBezier(0.08f, 0.42f, 0f, 1f, 1f); // CSS ease-in

        private static readonly Vector4 Danger = new(1f, 71f / 255f, 87f / 255f, 1f);

        private static Phase _phase;
        private static string _id = string.Empty;
        private static ContextMenuItem[] _items = Array.Empty<ContextMenuItem>();
        private static Vector2 _min;
        private static Vector2 _size;
        private static Vector2 _pivot;
        private static double _phaseStart;
        private static bool _focusPending;

        /// <summary>Opens the menu for <paramref name="id"/> at the given
        /// screen position (typically the mouse), replacing any open menu.
        /// Items freeze at open.</summary>
        public static void Open(string id, Vector2 position, ContextMenuItem[] items)
        {
            float s = ImGuiHelpers.GlobalScale;
            _id = id;
            _items = items;
            _size = new Vector2(MenuWidth * s, HeightFor(items, s));

            // Picto's shift/clamp: pushed inside a 12px viewport margin,
            // never mirrored; only the transform origin flips.
            var display = ImGui.GetIO().DisplaySize;
            float margin = ViewportMargin * s;
            var pos = position;
            bool fromRight = false, fromBottom = false;
            if (pos.X + _size.X > display.X - margin)
            {
                pos.X = display.X - _size.X - margin;
                fromRight = true;
            }
            if (pos.Y + _size.Y > display.Y - margin)
            {
                pos.Y = display.Y - _size.Y - margin;
                fromBottom = true;
            }
            pos.X = MathF.Max(pos.X, margin);
            pos.Y = MathF.Max(pos.Y, margin);
            _min = pos;
            _pivot = new Vector2(
                fromRight ? pos.X + _size.X : pos.X,
                fromBottom ? pos.Y + _size.Y : pos.Y);

            _phase = Phase.Opening;
            _phaseStart = ImGui.GetTime();
            _focusPending = true;
        }

        public static bool IsOpen(string id) => _phase != Phase.Hidden && _id == id;

        /// <summary>Instantly hides the menu (stale target).</summary>
        public static void Dismiss(string id)
        {
            if (_id == id)
                _phase = Phase.Hidden;
        }

        private static void StartClose()
        {
            if (_phase is Phase.Opening or Phase.Open)
            {
                _phase = Phase.Closing;
                _phaseStart = ImGui.GetTime();
            }
        }

        private static float HeightFor(ContextMenuItem[] items, float s)
        {
            float height = Pad * 2f * s;
            for (int i = 0; i < items.Length; i++)
            {
                height += (items[i].IsSeparator ? SeparatorBlock : RowHeight) * s;
                if (i > 0)
                    height += Gap * s;
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
                    float k = Enter.Evaluate(Math.Clamp(t / 0.10f, 0f, 1f));
                    scale = 0.92f + 0.08f * k;
                    alpha = k;
                    interactive = true;
                    if (t >= 0.10f)
                        _phase = Phase.Open;
                    break;
                }
                case Phase.Closing:
                {
                    if (t >= 0.08f)
                    {
                        _phase = Phase.Hidden;
                        return -1;
                    }
                    float k = Exit.Evaluate(Math.Clamp(t / 0.08f, 0f, 1f));
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
            const ImGuiWindowFlags hostFlags =
                ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize
                | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoScrollbar
                | ImGuiWindowFlags.NoScrollWithMouse | ImGuiWindowFlags.NoSavedSettings
                | ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoBackground;

            // Transparent full-viewport backdrop: swallows the outside
            // press that closes the menu, exactly Picto's backdrop div.
            if (interactive)
            {
                ImGui.SetNextWindowPos(Vector2.Zero);
                ImGui.SetNextWindowSize(io.DisplaySize);
                ImGui.Begin("##floating-menu-backdrop",
                    hostFlags | ImGuiWindowFlags.NoFocusOnAppearing);
                ImGui.SetCursorScreenPos(Vector2.Zero);
                ImGui.InvisibleButton("##floating-menu-backdrop-hit", io.DisplaySize);
                if (ImGui.IsItemActivated()
                    || ImGui.IsItemClicked(ImGuiMouseButton.Right))
                    StartClose();
                ImGui.End();
                if (ImGui.IsKeyPressed(ImGuiKey.Escape))
                    StartClose();
            }

            float host = HostMargin * s;
            ImGui.SetNextWindowPos(_min - new Vector2(host, host));
            ImGui.SetNextWindowSize(_size + new Vector2(host, host) * 2f);
            if (_focusPending)
            {
                ImGui.SetNextWindowFocus();
                _focusPending = false;
            }
            ImGui.Begin("##floating-menu", hostFlags);
            var dl = ImGui.GetWindowDrawList();
            int vtxStart = dl.VtxBuffer.Size;
            int clicked = DrawSurfaceAndRows(dl, s, interactive);
            int vtxEnd = dl.VtxBuffer.Size;
            // The whole surface — shadow, ring, chrome, rows — pops as one
            // composited unit about the flip-aware transform origin.
            VertexTransform.ApplyPop(dl, vtxStart, vtxEnd, _pivot, scale, Vector2.Zero, alpha);
            ImGui.End();

            if (clicked >= 0)
                StartClose();
            return clicked;
        }

        private static int DrawSurfaceAndRows(ImDrawListPtr dl, float s, bool interactive)
        {
            var min = _min;
            var max = _min + _size;
            float radius = MenuRadius * s;

            // 0 3px 12px rgba(0,0,0,.3): a layered feather approximation.
            var shadowOffset = new Vector2(0f, 3f * s);
            const int layers = 10;
            for (int i = layers; i >= 1; i--)
            {
                float spread = i * (12f / layers) * s;
                float falloff = 1f - (i - 1f) / layers;
                uint color = ImGui.ColorConvertFloat4ToU32(
                    new Vector4(0f, 0f, 0f, 0.055f * falloff * falloff));
                dl.AddRect(
                    min + shadowOffset - new Vector2(spread, spread),
                    max + shadowOffset + new Vector2(spread, spread),
                    color, radius + spread, ImDrawFlags.None, spread * 0.9f + 1f);
            }
            // 0 0 0 1px rgba(0,0,0,.5): the outer ring, OUTSIDE the surface
            // — the host margin exists so none of this clips.
            dl.AddRect(
                min - new Vector2(0.5f * s), max + new Vector2(0.5f * s),
                ImGui.ColorConvertFloat4ToU32(new Vector4(0f, 0f, 0f, 0.5f)),
                radius, ImDrawFlags.None, 1f * s);

            GlassChrome.DrawMenuSurface(dl, min, max, MenuRadius);

            // Rows.
            int clicked = -1;
            float y = min.Y + Pad * s;
            float left = min.X + Pad * s;
            float right = max.X - Pad * s;
            for (int i = 0; i < _items.Length; i++)
            {
                if (i > 0)
                    y += Gap * s;
                var item = _items[i];
                if (item.IsSeparator)
                {
                    float lineY = y + 2f * s;
                    dl.AddRectFilled(
                        new Vector2(left, lineY),
                        new Vector2(right, lineY + MathF.Max(1f, 1f * s)),
                        ImGui.ColorConvertFloat4ToU32(new Vector4(1f, 1f, 1f, 0.08f)));
                    y += SeparatorBlock * s;
                    continue;
                }

                var rowMin = new Vector2(left, y);
                var rowMax = new Vector2(right, y + RowHeight * s);
                bool hovered = false;
                if (interactive && !item.Disabled)
                {
                    ImGui.SetCursorScreenPos(rowMin);
                    if (ImGui.InvisibleButton($"##fm-row{i}", rowMax - rowMin))
                        clicked = i;
                    hovered = ImGui.IsItemHovered();
                }

                if (hovered)
                    dl.AddRectFilled(rowMin, rowMax,
                        ImGui.ColorConvertFloat4ToU32(item.Danger
                            ? new Vector4(Danger.X, Danger.Y, Danger.Z, 0.12f)
                            : new Vector4(1f, 1f, 1f, 0.08f)),
                        RowRadius * s);

                float rowAlpha = item.Disabled ? 0.4f : 1f;
                var text = item.Danger ? Danger : new Vector4(1f, 1f, 1f, 1f);
                text.W *= rowAlpha;
                var iconTint = ColorEx.ApplyAlpha(
                    text with { W = text.W * (hovered ? 1f : 0.8f) });

                ImGui.SetCursorScreenPos(new Vector2(
                    rowMin.X + RowPad * s, rowMin.Y + (RowHeight - IconSize) * 0.5f * s));
                Icon(item.Icon, IconSize, iconTint);

                float textX = rowMin.X + (RowPad + IconSize + IconGap) * s;
                var labelFont = FontRegistry.Resolve(FontFamily.Default, 13f);
                bool labelPushed = labelFont is { Available: true };
                if (labelPushed) labelFont!.Push();
                var labelSize = ImGui.CalcTextSize(item.Label);
                dl.AddText(
                    new Vector2(textX, rowMin.Y + (RowHeight * s - labelSize.Y) * 0.5f),
                    ImGui.ColorConvertFloat4ToU32(ColorEx.ApplyAlpha(text)), item.Label);
                if (labelPushed) labelFont!.Pop();

                if (item.Shortcut is { Length: > 0 } shortcut)
                {
                    var shortcutFont = FontRegistry.Resolve(FontFamily.Default, 11f);
                    bool shortcutPushed = shortcutFont is { Available: true };
                    if (shortcutPushed) shortcutFont!.Push();
                    var shortcutSize = ImGui.CalcTextSize(shortcut);
                    dl.AddText(
                        new Vector2(
                            rowMax.X - RowPad * s - shortcutSize.X,
                            rowMin.Y + (RowHeight * s - shortcutSize.Y) * 0.5f),
                        ImGui.ColorConvertFloat4ToU32(
                            ColorEx.ApplyAlpha(text with { W = text.W * 0.5f })),
                        shortcut);
                    if (shortcutPushed) shortcutFont!.Pop();
                }

                y += RowHeight * s;
            }

            return clicked;
        }
    }
}
