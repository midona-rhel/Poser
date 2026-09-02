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
    public ContextMenuItem[]? SubmenuItems;

    /// <summary>Explanatory hover help for the row — the same card every
    /// control's <c>help</c> shows. The one row shape that NEEDS it is a
    /// disabled row explaining why it is unavailable, which has no live
    /// item to hover, so the menu registers it geometrically.</summary>
    public string? Help;

    /// <summary>A toggle row: clicking it fires but leaves the menu open,
    /// so several can be set in one visit. Pair it with a refresh of the
    /// items so the row shows its new state.</summary>
    public bool KeepOpen;

    public ContextMenuItem(
        string label,
        TablerIcon icon = TablerIcon.Circle,
        string? shortcut = null,
        bool danger = false,
        bool disabled = false,
        string? help = null,
        ContextMenuItem[]? submenuItems = null,
        bool keepOpen = false)
    {
        Label = label;
        Icon = icon;
        Shortcut = shortcut;
        Danger = danger;
        Disabled = disabled;
        Help = help;
        SubmenuItems = submenuItems;
        KeepOpen = keepOpen;
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

        private static Transition Enter =>
            Transition.CubicBezier(
                Crystarium.ActiveTheme.Motion.Fast,
                0.4f, 0f, 0.22f, 1f);
        private static Transition Exit =>
            Transition.CubicBezier(
                Crystarium.ActiveTheme.Motion.MenuExit,
                0.42f, 0f, 1f, 1f); // CSS ease-in


        // Deliberate deviation from Picto: its menu rows sit their text
        // visibly below true center even in the browser, because flex centers
        // the LINE BOX. Poser centers the INK, and a menu row is
        // icon-adjacent by construction — label and shortcut go through
        // TextInBand's besideIcon mode, which owns that seat.

        private static Phase _phase;
        private static string _id = string.Empty;
        private static ContextMenuItem[] _items = Array.Empty<ContextMenuItem>();
        private static Vector2 _min;
        private static Vector2 _size;
        private static Vector2 _pivot;
        private static ContextMenuItem[]? _submenuItems;

        /// <summary>The submenu's hover GRACE: the geometric bridge misses
        /// the row gaps, the menu padding, and a diagonal pass over other
        /// rows — pixels where the pointer hovers nothing — so an open
        /// submenu survives this long after the pointer left every
        /// keep-region. A row that opens its own submenu still takes over
        /// immediately.</summary>
        private const double SubmenuGraceSeconds = 0.30;
        private static double _submenuKeepUntil;
        private static Vector2 _submenuMin;
        private static Vector2 _submenuSize;
        private static int _submenuParent = -1;
        private static int _submenuClicked = -1;
        private static int _submenuClickedParent = -1;

        private static double _phaseStart;
        private static int _lastOwnerFrame = -1;
        private static int _openedFrame = -1;

        /// <summary>Opens the menu for <paramref name="id"/> at the given
        /// screen position (typically the mouse), replacing any open menu.
        /// Items freeze at open.</summary>
        /// <param name="width">Surface width in LOGICAL units, overriding the
        /// canonical <c>Floating.MenuWidth</c> surface. Null — the default —
        /// keeps the 260px context-menu width every action menu is drawn at;
        /// pass <see cref="MeasureWidth"/> for a menu that fits its own rows.
        /// </param>
        public static void Open(
            string id,
            Vector2 position,
            ContextMenuItem[] items,
            float? width = null)
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
            _submenuItems = null;
            _submenuParent = -1;
            _submenuClicked = -1;
            _size = new Vector2(
                (width ?? Crystarium.ActiveTheme.Floating.MenuWidth) * s,
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
            _submenuItems = null;
            _submenuParent = -1;
            _submenuClicked = -1;
        }

        public static void EndFrame()
        {
            if (_phase != Phase.Hidden
                && _lastOwnerFrame != ImGui.GetFrameCount())
                DismissAll();
        }

        public static bool IsOpen(string id) => _phase != Phase.Hidden && _id == id;

        /// <summary>Replaces the open menu's rows in place — a menu whose
        /// rows show live state (a toggle's check) is rebuilt by its owner
        /// every frame and handed back here. A closed menu, or another
        /// menu, ignores it.</summary>
        public static void Refresh(string id, ContextMenuItem[] items)
        {
            if (_phase == Phase.Hidden || _id != id || items.Length != _items?.Length)
                return;
            _items = items;
            if (_submenuParent >= 0 && _submenuParent < items.Length)
                _submenuItems = items[_submenuParent].SubmenuItems;
        }

        /// <summary>Returns and clears a submenu click.</summary>
        public static int ConsumeSubmenuClick()
        {
            _submenuClickedParent = -1;
            return ConsumeSubmenuClick(ref _submenuClicked, _submenuItems);
        }

        /// <summary>Returns and clears a submenu click, naming the PARENT
        /// row whose submenu it came from — a menu that carries several
        /// submenus routes the click by it.</summary>
        public static int ConsumeSubmenuClick(out int parent)
        {
            parent = _submenuClickedParent;
            _submenuClickedParent = -1;
            return ConsumeSubmenuClick(ref _submenuClicked, _submenuItems);
        }

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

        /// <summary>
        /// The narrowest surface that still shows every row of
        /// <paramref name="items"/> whole: the widest label measured through
        /// the menu's own label font, plus the icon slot, the shortcut column
        /// where one exists, and the row and surface insets. Floored at
        /// <c>Floating.MenuMinWidth</c> so a two-command menu is a menu and not
        /// a sliver. Returns LOGICAL units, ready to hand to
        /// <see cref="Open"/>'s <c>width</c>; call it inside a frame, since it
        /// measures through the live font stack.
        /// </summary>
        public static float MeasureWidth(ContextMenuItem[] items)
        {
            float s = ImGuiHelpers.GlobalScale;
            var labelStyle = new TextStyle
            {
                Size = Crystarium.ActiveTheme.Typography.BodySize,
            };
            var shortcutStyle = new TextStyle
            {
                Size = Crystarium.ActiveTheme.Typography.CaptionSize,
            };

            // Everything DrawSurfaceAndRows spends before and after the label:
            // the surface padding on both sides, the row padding on both sides,
            // and the icon seat with its gap.
            float chrome =
                Crystarium.ActiveTheme.Floating.MenuPadding * 2f
                + Crystarium.ActiveTheme.Floating.MenuRowPadding * 2f
                + Crystarium.ActiveTheme.Controls.IconSize
                + Crystarium.ActiveTheme.Floating.MenuIconGap;

            float widest = 0f;
            for (int i = 0; i < items.Length; i++)
            {
                var item = items[i];
                if (item.IsSeparator)
                    continue;
                // Measured at the live scale and rounded UP before being taken
                // back to logical units: text does not rasterize linearly in
                // scale, and a half-pixel short would ellipsize the very label
                // this width exists to show.
                float content =
                    MathF.Ceiling(Crystarium.MeasureText(item.Label, labelStyle).X) / s;
                if (item.Shortcut is { Length: > 0 } shortcut)
                    content +=
                        MathF.Ceiling(Crystarium.MeasureText(shortcut, shortcutStyle).X) / s
                        + ShortcutGap;
                widest = MathF.Max(widest, content);
            }

            return MathF.Max(
                Crystarium.ActiveTheme.Floating.MenuMinWidth,
                chrome + widest);
        }

        /// <summary>CSS <c>.shortcut</c> padding-left: the minimum
        /// label-to-shortcut gap, shared by the drawn row and
        /// <see cref="MeasureWidth"/>.</summary>
        private const float ShortcutGap = 28f;

        internal static Vector2 PlaceSubmenu(
            Vector2 parentMin,
            Vector2 parentSize,
            Vector2 triggerRowMin,
            Vector2 childSize,
            Vector2 displaySize,
            float scale,
            float menuPadding)
        {
            // Uses screen pixels, so the gap stays one pixel.
            const float gap = 1f;
            float rightX = parentMin.X + parentSize.X + gap;
            if (rightX + childSize.X > displaySize.X)
                rightX = parentMin.X - gap - childSize.X;
            // A tall submenu slides up so its last row stays on screen.
            float top = triggerRowMin.Y - menuPadding * scale;
            top = MathF.Min(top, displaySize.Y - childSize.Y - menuPadding * scale);
            top = MathF.Max(top, menuPadding * scale);
            return new Vector2(rightX, top);
        }

        /// <summary>Includes the submenu in the menu window bounds.</summary>
        internal static (Vector2 Min, Vector2 Size) HostBounds(
            Vector2 parentMin,
            Vector2 parentSize,
            bool hasSubmenu,
            Vector2 submenuMin,
            Vector2 submenuSize,
            float hostMargin)
        {
            var unionMin = parentMin;
            var unionMax = parentMin + parentSize;
            if (hasSubmenu)
            {
                unionMin = Vector2.Min(unionMin, submenuMin);
                unionMax = Vector2.Max(unionMax, submenuMin + submenuSize);
            }

            var margin = new Vector2(hostMargin, hostMargin);
            return (unionMin - margin, unionMax - unionMin + margin * 2f);
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
            bool pointerOverMenu = IsMenuOrSubmenuPointerWithin(
                pointer, _min, _size, _submenuItems, _submenuMin, _submenuSize);
            bool outsidePressed =
                ImGui.IsMouseClicked(ImGuiMouseButton.Left)
                || ImGui.IsMouseClicked(ImGuiMouseButton.Right);
            if (ImGui.GetFrameCount() != _openedFrame
                && ShouldDismiss(
                    outsidePressed,
                    pointerOverMenu,
                    ImGui.IsKeyPressed(ImGuiKey.Escape)))
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
            // Includes a newly hovered submenu in the first frame.
            bool hasPredictedSubmenu = TryGetSubmenuBounds(
                pointer, s, io.DisplaySize,
                out var predictedSubmenuMin, out var predictedSubmenuSize);
            bool hasSubmenu = _submenuItems is not null || hasPredictedSubmenu;
            Vector2 hostSubmenuMin = hasPredictedSubmenu
                ? predictedSubmenuMin
                : _submenuMin;
            Vector2 hostSubmenuSize = hasPredictedSubmenu
                ? predictedSubmenuSize
                : _submenuSize;
            var hostBounds = HostBounds(
                _min, _size, hasSubmenu,
                hostSubmenuMin, hostSubmenuSize, host);
            // The submenu the rows drew last frame is covered too: the
            // prediction can undersize it (the Pose submenu lost its last
            // row to the host's edge, 2026-09-02), and a host that clips
            // its own rows is worse than one a little too large.
            if (_submenuItems is not null)
            {
                var shown = HostBounds(
                    _min, _size, true, _submenuMin, _submenuSize, host);
                var unionMin = Vector2.Min(hostBounds.Min, shown.Min);
                var unionMax = Vector2.Max(
                    hostBounds.Min + hostBounds.Size, shown.Min + shown.Size);
                hostBounds = (unionMin, unionMax - unionMin);
            }
            ImGui.SetNextWindowPos(hostBounds.Min);
            ImGui.SetNextWindowSize(hostBounds.Size);
            ImGui.SetNextWindowFocus();
            ImGui.Begin("##floating-menu", hostFlags);
            var dl = ImGui.GetWindowDrawList();
            int vtxStart = dl.VtxBuffer.Size;
            int clicked = DrawSurfaceAndRows(
                dl, s, interactive, _items, _min, _size, "##fm-row", alpha);
            if (_submenuItems is { } submenu)
            {
                int childClicked = DrawSurfaceAndRows(
                    dl, s, interactive, submenu, _submenuMin, _submenuSize,
                    "##fm-submenu-row", alpha);
                _submenuClicked = AcceptSubmenuClick(childClicked, submenu);
                if (_submenuClicked >= 0)
                    _submenuClickedParent = _submenuParent;
            }
            int vtxEnd = dl.VtxBuffer.Size;
            // The whole surface — shadow, ring, chrome, rows — pops as one
            // composited unit about the flip-aware transform origin.
            VertexTransform.ApplyPop(dl, vtxStart, vtxEnd, _pivot, scale, Vector2.Zero, alpha);
            ImGui.End();
            Interactive.EndOwner(menuOwner);

            bool keepOpen =
                _submenuClicked >= 0 && _submenuItems is { } open
                && _submenuClicked < open.Length && open[_submenuClicked].KeepOpen;
            if ((clicked >= 0 || _submenuClicked >= 0) && !keepOpen)
                StartClose();
            return clicked;
        }

        private static bool TryGetSubmenuBounds(
            Vector2 pointer,
            float scale,
            Vector2 displaySize,
            out Vector2 submenuMin,
            out Vector2 submenuSize)
        {
            submenuMin = default;
            submenuSize = default;
            float y = _min.Y + Crystarium.ActiveTheme.Floating.MenuPadding * scale;
            float left = _min.X + Crystarium.ActiveTheme.Floating.MenuPadding * scale;
            float right = _min.X + _size.X
                - Crystarium.ActiveTheme.Floating.MenuPadding * scale;
            for (int i = 0; i < _items.Length; i++)
            {
                if (i > 0)
                    y += Crystarium.ActiveTheme.Floating.MenuRowGap * scale;
                var item = _items[i];
                if (item.IsSeparator)
                {
                    y += Crystarium.ActiveTheme.Floating.MenuSeparatorBlock * scale;
                    continue;
                }

                var rowMin = new Vector2(left, y);
                var rowMax = new Vector2(
                    right,
                    y + Crystarium.ActiveTheme.Controls.ListRowHeight * scale);
                if (!item.Disabled
                    && item.SubmenuItems is { Length: > 0 } child
                    && InRect(pointer, rowMin, rowMax))
                {
                    submenuSize = new Vector2(
                        MeasureWidth(child) * scale,
                        HeightFor(child, scale));
                    submenuMin = PlaceSubmenu(
                        _min, _size, rowMin, submenuSize,
                        displaySize, scale,
                        Crystarium.ActiveTheme.Floating.MenuPadding);
                    return true;
                }

                y += Crystarium.ActiveTheme.Controls.ListRowHeight * scale;
            }

            return false;
        }

        private static string ExclusiveKey(string id) =>
            $"floating-menu:{id}";

        private static int DrawSurfaceAndRows(
            ImDrawListPtr dl,
            float s,
            bool interactive,
            ContextMenuItem[] items,
            Vector2 min,
            Vector2 size,
            string rowIdPrefix,
            float fade)
        {
            var max = min + size;
            // The lifecycle alpha rides the vertex pop AFTER this draw, so
            // the blur — a prepass, not vertices — takes it here instead.
            FloatingSurface.DrawChrome(
                dl,
                min,
                max,
                Crystarium.ActiveTheme.Radii.Surface,
                fade: fade);

            // Rows.
            int clicked = -1;
            float y = min.Y + Crystarium.ActiveTheme.Floating.MenuPadding * s;
            float left = min.X + Crystarium.ActiveTheme.Floating.MenuPadding * s;
            float right = max.X - Crystarium.ActiveTheme.Floating.MenuPadding * s;
            var previousSubmenu = _submenuItems;
            int previousParent = _submenuParent;
            bool openedSubmenu = false;
            if (ReferenceEquals(items, _items))
                _submenuItems = null;
            for (int i = 0; i < items.Length; i++)
            {
                if (i > 0)
                    y += Crystarium.ActiveTheme.Floating.MenuRowGap * s;
                var item = items[i];
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
                        $"{rowIdPrefix}{i}",
                        rowMax - rowMin,
                        disabled: false);
                    if (hit.Clicked && item.SubmenuItems is not { Length: > 0 })
                        clicked = i;
                    hovered = hit.Hovered;
                }

                bool keepAlive = false;
                if (ReferenceEquals(items, _items)
                    && item.SubmenuItems is { Length: > 0 }
                    && previousSubmenu is not null && previousParent == i)
                {
                    bool held = KeepSubmenuOpen(
                        ImGui.GetMousePos(), rowMin, rowMax,
                        _submenuMin, _submenuSize, min, size);
                    double now = ImGui.GetTime();
                    if (held || hovered)
                        _submenuKeepUntil = now + SubmenuGraceSeconds;
                    // openedSubmenu gate: a row the pointer actually stands
                    // on beat this row already; grace never steals back.
                    keepAlive = held || (!openedSubmenu
                        && now < _submenuKeepUntil);
                }
                if (ReferenceEquals(items, _items)
                    && item.SubmenuItems is { Length: > 0 } child
                    && (hovered || keepAlive))
                {
                    _submenuParent = i;
                    _submenuItems = child;
                    openedSubmenu = true;
                    float childWidth = MeasureWidth(child) * s;
                    float childHeight = HeightFor(child, s);
                    _submenuMin = PlaceSubmenu(
                        min,
                        size,
                        rowMin,
                        new Vector2(childWidth, childHeight),
                        ImGui.GetIO().DisplaySize,
                        s,
                        Crystarium.ActiveTheme.Floating.MenuPadding);
                    _submenuSize = new Vector2(childWidth, childHeight);
                }

                // Context menus carry NO hovers (ruled 2026-08-31): a
                // row's label is its whole explanation, and the help card
                // under the menu read as a stray band.

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
                float labelRight = rowMax.X
                    - Crystarium.ActiveTheme.Floating.MenuRowPadding * s;
                if (item.SubmenuItems is { Length: > 0 })
                {
                    float arrowSize = Crystarium.ActiveTheme.Controls.IconSize * 0.8f;
                    labelRight -= (arrowSize + Crystarium.ActiveTheme.Floating.MenuIconGap) * s;
                    ImGui.SetCursorScreenPos(new Vector2(
                        labelRight,
                        rowMin.Y + (Crystarium.ActiveTheme.Controls.ListRowHeight
                            - arrowSize) * 0.5f * s));
                    Icon(TablerIcon.ChevronRight, arrowSize, iconTint);
                }
                if (item.Shortcut is { Length: > 0 } shortcut)
                {
                    var shortcutStyle = new TextStyle
                    {
                        Size = Crystarium.ActiveTheme.Typography.CaptionSize,
                        Color = text.Fade(0.5f),
                    };
                    var shortcutSize =
                        Crystarium.MeasureText(shortcut, shortcutStyle);
                    Crystarium.TextInBand(
                        new Vector2(rowMin.X, rowMin.Y),
                        new Vector2(labelRight - rowMin.X, rowHeightPx),
                        shortcut,
                        shortcutStyle,
                        TextAlign.End,
                        besideIcon: true);
                    labelRight -= shortcutSize.X + ShortcutGap * s;
                }
                var labelStyle = new TextStyle
                {
                    Size = Crystarium.ActiveTheme.Typography.BodySize,
                    Color = text,
                };
                var labelSize =
                    Crystarium.MeasureText(item.Label, labelStyle);
                float labelWidth = MathF.Max(1f, labelRight - textX);
                var labelBand = new Vector2(labelWidth, rowHeightPx);
                // CSS .label: flex 1, ellipsis. Constrain ONLY on
                // overflow: the truncate path clips to the line box, and
                // Segoe's descenders reach a hair below it — an
                // unconditional clip shaved the bottom off 'g'.
                if (labelSize.X > labelWidth)
                    Crystarium.TextInBand(
                        new Vector2(textX, rowMin.Y),
                        labelBand,
                        item.Label,
                        labelStyle,
                        TextConstraint.Truncate(labelWidth),
                        besideIcon: true);
                else
                    Crystarium.TextInBand(
                        new Vector2(textX, rowMin.Y),
                        labelBand,
                        item.Label,
                        labelStyle,
                        besideIcon: true);

                y += Crystarium.ActiveTheme.Controls.ListRowHeight * s;
            }

            if (ReferenceEquals(items, _items) && !openedSubmenu)
            {
                _submenuItems = null;
                _submenuParent = -1;
            }

            return clicked;
        }

        internal static bool IsMenuOrSubmenuPointerWithin(
            Vector2 point,
            Vector2 menuMin,
            Vector2 menuSize,
            ContextMenuItem[]? submenu,
            Vector2 submenuMin,
            Vector2 submenuSize) =>
            InRect(point, menuMin, menuSize)
            || (submenu is not null
                && (InRect(point, submenuMin, submenuSize)
                    || InSubmenuBridge(
                        point,
                        new Vector2(menuMin.X, submenuMin.Y),
                        new Vector2(menuMin.X + menuSize.X, submenuMin.Y + submenuSize.Y),
                        menuMin,
                        menuSize,
                        submenuMin,
                        submenuSize)));

        internal static bool KeepSubmenuOpen(
            Vector2 pointer,
            Vector2 parentRowMin,
            Vector2 parentRowMax,
            Vector2 submenuMin,
            Vector2 submenuSize,
            Vector2 parentMenuMin,
            Vector2 parentMenuSize) =>
            InRect(pointer, submenuMin, submenuSize)
            || InSubmenuBridge(
                pointer,
                parentRowMin,
                parentRowMax,
                parentMenuMin,
                parentMenuSize,
                submenuMin,
                submenuSize);

        internal static int AcceptSubmenuClick(
            int clicked,
            ContextMenuItem[] items) =>
            clicked >= 0 && clicked < items.Length && !items[clicked].Disabled
                ? clicked
                : -1;

        internal static int ConsumeSubmenuClick(
            ref int clicked,
            ContextMenuItem[]? items)
        {
            int result = items is null ? -1 : AcceptSubmenuClick(clicked, items);
            clicked = -1;
            return result;
        }

        internal static bool ShouldDismiss(
            bool outsidePressed,
            bool pointerWithinMenu,
            bool escapePressed) =>
            (outsidePressed && !pointerWithinMenu) || escapePressed;

        private static bool InRect(Vector2 point, Vector2 min, Vector2 size) =>
            point.X >= min.X && point.X < min.X + size.X
            && point.Y >= min.Y && point.Y < min.Y + size.Y;

        private static bool InSubmenuBridge(
            Vector2 point,
            Vector2 parentRowMin,
            Vector2 parentRowMax,
            Vector2 parentMenuMin,
            Vector2 parentMenuSize,
            Vector2 submenuMin,
            Vector2 submenuSize)
        {
            float parentRight = parentMenuMin.X + parentMenuSize.X;
            float childLeft = submenuMin.X;
            float left = MathF.Min(parentRight, childLeft);
            float right = MathF.Max(parentRight, childLeft);
            float top = MathF.Min(parentRowMin.Y, submenuMin.Y);
            float bottom = MathF.Max(
                parentRowMax.Y,
                submenuMin.Y + submenuSize.Y);
            return point.X >= left && point.X <= right
                && point.Y >= top && point.Y < bottom;
        }
    }
}
