using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using static Poser.UI.Widgets.TablerIconWidgets;
using static Poser.UI.Widgets.TextWidgets;
using static Poser.UI.Widgets.Themes;

namespace Poser.UI.Widgets;

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
            ActiveTheme.Motion.Fast,
            0.4f, 0f, 0.22f, 1f);
    private static Transition Exit =>
        Transition.CubicBezier(
            ActiveTheme.Motion.MenuExit,
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
    private static int _submenuParent = -1;
    private static int _submenuClicked = -1;
    private static int _submenuClickedParent = -1;

    private sealed class SubmenuLevel
    {
        public int Parent;
        public int[] Path = [];
        public string[] PathLabels = [];
        public ContextMenuItem[] Items = [];
        public Vector2 Min;
        public Vector2 Size;
        public Vector2 Pivot;
        public double KeepUntil;
        public Phase Phase = Phase.Opening;
        public double PhaseStart;
        public float ExitScale = 1f;
        public float ExitAlpha = 1f;
    }

    private static readonly List<SubmenuLevel> Submenus = new();
    private static readonly List<SubmenuLevel> ClosingSubmenus = new();

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
        Submenus.Clear();
        ClosingSubmenus.Clear();
        _submenuItems = null;
        _submenuParent = -1;
        _submenuClicked = -1;
        _size = new Vector2(
            (width ?? ActiveTheme.Floating.MenuWidth) * s,
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
        Submenus.Clear();
        ClosingSubmenus.Clear();
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
        if (_phase == Phase.Hidden || _id != id)
            return;
        // Dispatch indices must describe the rows on screen. A changed
        // capability/menu shape closes instead of keeping stale rows.
        if (items.Length != _items.Length)
        {
            DismissAll();
            return;
        }
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
            Size = ActiveTheme.Typography.BodySize,
        };
        var shortcutStyle = new TextStyle
        {
            Size = ActiveTheme.Typography.CaptionSize,
        };

        // Everything DrawSurfaceAndRows spends before and after the label:
        // the surface padding on both sides, the row padding on both sides,
        // and the icon seat with its gap.
        float chrome =
            ActiveTheme.Floating.MenuPadding * 2f
            + ActiveTheme.Floating.MenuRowPadding * 2f
            + ActiveTheme.Controls.IconSize
            + ActiveTheme.Floating.MenuIconGap;

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
                MathF.Ceiling(MeasureText(item.Label, labelStyle).X) / s;
            if (item.Shortcut is { Length: > 0 } shortcut)
                content +=
                    MathF.Ceiling(MeasureText(shortcut, shortcutStyle).X) / s
                    + ShortcutGap;
            widest = MathF.Max(widest, content);
        }

        return MathF.Max(
            ActiveTheme.Floating.MenuMinWidth,
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
        float height = ActiveTheme.Floating.MenuPadding * 2f * s;
        for (int i = 0; i < items.Length; i++)
        {
            height += (items[i].IsSeparator
                ? ActiveTheme.Floating.MenuSeparatorBlock
                : ActiveTheme.Controls.ListRowHeight) * s;
            if (i > 0)
                height += ActiveTheme.Floating.MenuRowGap * s;
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
        _submenuClicked = -1;
        _submenuClickedParent = -1;
        // Hand-rolled surface, same handshake: claim on open, sync
        // every frame it draws, release on dismissal.
        if (!FloatingSurface.SyncExclusive(ExclusiveKey(id)))
        {
            DismissAll();
            return -1;
        }

        var pointer = ImGui.GetMousePos();
        bool pointerOverMenu = InRect(pointer, _min, _size)
            || PointerWithinSubmenus(pointer, 0);
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
                    t / ActiveTheme.Motion.Fast,
                    0f,
                    1f));
                scale = 0.92f + 0.08f * k;
                alpha = k;
                // The visual rect is transformed during entrance. Waiting
                // one short transition before enabling input keeps the hit
                // geometry identical to the rendered rows.
                interactive = false;
                if (t >= ActiveTheme.Motion.Fast)
                    _phase = Phase.Open;
                break;
            }
            case Phase.Closing:
            {
                if (t >= ActiveTheme.Motion.MenuExit)
                {
                    DismissAll();
                    return -1;
                }
                float k = Exit.Evaluate(Math.Clamp(
                    t / ActiveTheme.Motion.MenuExit,
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

        float host = ActiveTheme.Floating.HostMargin * s;
        UpdateSubmenus(pointer, s, io.DisplaySize, interactive);
        PruneClosingSubmenus(now);
        var unionMin = _min;
        var unionMax = _min + _size;
        foreach (var level in Submenus)
        {
            unionMin = Vector2.Min(unionMin, level.Min);
            unionMax = Vector2.Max(unionMax, level.Min + level.Size);
        }
        foreach (var level in ClosingSubmenus)
        {
            unionMin = Vector2.Min(unionMin, level.Min);
            unionMax = Vector2.Max(unionMax, level.Min + level.Size);
        }
        var hostBounds = (Min: unionMin - new Vector2(host),
            Size: unionMax - unionMin + new Vector2(host * 2f));
        ImGui.SetNextWindowPos(hostBounds.Min);
        ImGui.SetNextWindowSize(hostBounds.Size);
        ImGui.SetNextWindowFocus();
        ImGui.Begin("##floating-menu", hostFlags);
        var dl = ImGui.GetWindowDrawList();
        int vtxStart = dl.VtxBuffer.Size;
        int clicked = DrawSurfaceAndRows(
            dl, s, interactive && !PointerWithinSubmenus(pointer, 0),
            _items, _min, _size, "##fm-row", alpha);
        foreach (var level in ClosingSubmenus)
        {
            var motion = SubmenuMotion(level, now);
            int retiringStart = dl.VtxBuffer.Size;
            DrawSurfaceAndRows(
                dl, s, false, level.Items, level.Min, level.Size,
                "##fm-retiring-row", alpha * motion.Alpha);
            int retiringEnd = dl.VtxBuffer.Size;
            VertexTransform.ApplyPop(
                dl, retiringStart, retiringEnd, level.Pivot,
                motion.Scale, Vector2.Zero, motion.Alpha);
        }
        Action? nestedAction = null;
        bool nestedClicked = false;
        bool keepOpen = clicked >= 0 && _items[clicked].KeepOpen;
        for (int depth = 0; depth < Submenus.Count; depth++)
        {
            var level = Submenus[depth];
            var motion = SubmenuMotion(level, now);
            int childStart = dl.VtxBuffer.Size;
            int childClicked = DrawSurfaceAndRows(
                dl, s, interactive && level.Phase == Phase.Open
                    && !PointerWithinSubmenus(pointer, depth + 1),
                level.Items, level.Min, level.Size,
                $"##fm-submenu-{depth}-row", alpha * motion.Alpha);
            int childEnd = dl.VtxBuffer.Size;
            VertexTransform.ApplyPop(
                dl, childStart, childEnd, level.Pivot,
                motion.Scale, Vector2.Zero, motion.Alpha);
            if (childClicked < 0) continue;
            keepOpen = level.Items[childClicked].KeepOpen;
            if (depth == 0)
            {
                _submenuClicked = childClicked;
                _submenuClickedParent = level.Parent;
            }
            else
            {
                // Legacy consumers use a root/child index pair. Deeper
                // leaves carry their command, never an ambiguous index.
                nestedClicked = true;
                nestedAction = level.Items[childClicked].OnInvoke;
            }
        }
        int vtxEnd = dl.VtxBuffer.Size;
        // The whole surface — shadow, ring, chrome, rows — pops as one
        // composited unit about the flip-aware transform origin.
        VertexTransform.ApplyPop(dl, vtxStart, vtxEnd, _pivot, scale, Vector2.Zero, alpha);
        ImGui.End();
        Interactive.EndOwner(menuOwner);

        if ((clicked >= 0 || _submenuClicked >= 0 || nestedClicked) && !keepOpen)
            StartClose();
        // Commands may open another surface; never invoke during drawing
        // or close the replacement surface after dispatch.
        nestedAction?.Invoke();
        return clicked;
    }

    private static void UpdateSubmenus(
        Vector2 pointer,
        float scale,
        Vector2 displaySize,
        bool interactive)
    {
        if (!interactive) return;
        var items = _items;
        var min = _min;
        var size = _size;
        double now = ImGui.GetTime();
        int depth = 0;
        // Bound malformed/self-referencing menu descriptions.
        for (; depth < 16; depth++)
        {
            var previous = depth < Submenus.Count ? Submenus[depth] : null;
            int parent = -1;
            Vector2 rowMin = default;
            float y = min.Y + ActiveTheme.Floating.MenuPadding * scale;
            bool overDescendant = PointerWithinSubmenus(pointer, depth);
            for (int i = 0; i < items.Length; i++)
            {
                if (i > 0) y += ActiveTheme.Floating.MenuRowGap * scale;
                var item = items[i];
                var candidateMin = new Vector2(
                    min.X + ActiveTheme.Floating.MenuPadding * scale, y);
                var candidateSize = new Vector2(
                    size.X - ActiveTheme.Floating.MenuPadding * 2f * scale,
                    ActiveTheme.Controls.ListRowHeight * scale);
                if (!item.Disabled && item.SubmenuItems is { Length: > 0 }
                    && ((!overDescendant && InRect(pointer, candidateMin, candidateSize))
                        || (previous?.Parent == i
                            && (overDescendant || now < previous.KeepUntil))))
                {
                    parent = i;
                    rowMin = candidateMin;
                    // An actually hovered branch wins over another's grace.
                    if (!overDescendant && InRect(pointer, candidateMin, candidateSize)) break;
                }
                y += (item.IsSeparator
                    ? ActiveTheme.Floating.MenuSeparatorBlock
                    : ActiveTheme.Controls.ListRowHeight) * scale;
            }
            if (parent < 0) break;
            if (previous is null || previous.Parent != parent)
            {
                if (depth < Submenus.Count)
                    RetireSubmenus(depth, now);
                var path = new int[depth + 1];
                var pathLabels = new string[depth + 1];
                if (depth > 0)
                {
                    Array.Copy(Submenus[depth - 1].Path, path, depth);
                    Array.Copy(Submenus[depth - 1].PathLabels, pathLabels, depth);
                }
                path[depth] = parent;
                pathLabels[depth] = items[parent].Label;
                RemoveClosingBranch(path, pathLabels);
                previous = new SubmenuLevel
                {
                    Parent = parent,
                    Path = path,
                    PathLabels = pathLabels,
                    PhaseStart = now,
                };
                Submenus.Add(previous);
            }
            previous.Items = items[parent].SubmenuItems!;
            previous.Size = new Vector2(MeasureWidth(previous.Items) * scale,
                HeightFor(previous.Items, scale));
            previous.Min = PlaceSubmenu(min, size, rowMin, previous.Size,
                displaySize, scale, ActiveTheme.Floating.MenuPadding);
            previous.Pivot = new Vector2(
                previous.Min.X > min.X ? previous.Min.X : previous.Min.X + previous.Size.X,
                previous.Min.Y);
            if (overDescendant || InRect(pointer, rowMin, new Vector2(
                    size.X - ActiveTheme.Floating.MenuPadding * 2f * scale,
                    ActiveTheme.Controls.ListRowHeight * scale)))
                previous.KeepUntil = now + SubmenuGraceSeconds;
            items = previous.Items;
            min = previous.Min;
            size = previous.Size;
        }
        if (depth < Submenus.Count)
            RetireSubmenus(depth, now);
        _submenuItems = Submenus.Count > 0 ? Submenus[0].Items : null;
        _submenuParent = Submenus.Count > 0 ? Submenus[0].Parent : -1;
    }

    private static void RetireSubmenus(int first, double now)
    {
        for (int i = first; i < Submenus.Count; i++)
        {
            var level = Submenus[i];
            var motion = SubmenuMotion(level, now);
            level.Phase = Phase.Closing;
            level.PhaseStart = now;
            level.ExitScale = motion.Scale;
            level.ExitAlpha = motion.Alpha;
            ClosingSubmenus.Add(level);
        }
        Submenus.RemoveRange(first, Submenus.Count - first);
    }

    private static void RemoveClosingBranch(int[] path, string[] pathLabels)
    {
        for (int i = ClosingSubmenus.Count - 1; i >= 0; i--)
        {
            var closing = ClosingSubmenus[i];
            if (closing.Path.AsSpan().SequenceEqual(path)
                && closing.PathLabels.AsSpan().SequenceEqual(pathLabels))
            {
                ClosingSubmenus.RemoveAt(i);
                return;
            }
        }
    }

    private static (float Scale, float Alpha) SubmenuMotion(
        SubmenuLevel level,
        double now)
    {
        float elapsed = (float)(now - level.PhaseStart);
        if (level.Phase == Phase.Opening)
        {
            float k = Enter.Evaluate(Math.Clamp(
                elapsed / ActiveTheme.Motion.Fast, 0f, 1f));
            if (elapsed >= ActiveTheme.Motion.Fast)
                level.Phase = Phase.Open;
            return (0.92f + 0.08f * k, k);
        }
        if (level.Phase == Phase.Closing)
        {
            float k = Exit.Evaluate(Math.Clamp(
                elapsed / ActiveTheme.Motion.MenuExit, 0f, 1f));
            return (
                level.ExitScale * (1f - 0.05f * k),
                level.ExitAlpha * (1f - k));
        }
        return (1f, 1f);
    }

    private static void PruneClosingSubmenus(double now)
    {
        for (int i = ClosingSubmenus.Count - 1; i >= 0; i--)
        {
            if (now - ClosingSubmenus[i].PhaseStart
                >= ActiveTheme.Motion.MenuExit)
                ClosingSubmenus.RemoveAt(i);
        }
    }

    private static bool PointerWithinSubmenus(Vector2 pointer, int first)
    {
        for (int i = first; i < Submenus.Count; i++)
        {
            var level = Submenus[i];
            var parentMin = i == 0 ? _min : Submenus[i - 1].Min;
            var parentSize = i == 0 ? _size : Submenus[i - 1].Size;
            if (InRect(pointer, level.Min, level.Size)
                || InSubmenuBridge(pointer,
                    new Vector2(parentMin.X, level.Min.Y),
                    new Vector2(parentMin.X + parentSize.X, level.Min.Y + level.Size.Y),
                    parentMin, parentSize, level.Min, level.Size))
                return true;
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
            ActiveTheme.Radii.Surface,
            fade: fade);

        // Rows.
        int clicked = -1;
        float y = min.Y + ActiveTheme.Floating.MenuPadding * s;
        float left = min.X + ActiveTheme.Floating.MenuPadding * s;
        float right = max.X - ActiveTheme.Floating.MenuPadding * s;
        for (int i = 0; i < items.Length; i++)
        {
            if (i > 0)
                y += ActiveTheme.Floating.MenuRowGap * s;
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
                    ActiveTheme.Border);
                y += ActiveTheme.Floating.MenuSeparatorBlock * s;
                continue;
            }

            var rowMin = new Vector2(left, y);
            var rowMax = new Vector2(
                right,
                y + ActiveTheme.Controls.ListRowHeight * s);
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

            // Context menus carry NO hovers (ruled 2026-08-31): a
            // row's label is its whole explanation, and the help card
            // under the menu read as a stray band.

            if (hovered)
                dl.AddRectFilled(rowMin, rowMax,
                    ImGui.ColorConvertFloat4ToU32(ColorEx.ApplyAlpha(
                        item.Danger
                            ? ActiveTheme.Chrome.DangerHover
                            : item.Disruptive
                                ? ActiveTheme.Chrome.DisruptiveHover
                                : ActiveTheme.Chrome.WeakOverlay)),
                    ActiveTheme.Radii.Control * s);

            float rowAlpha = item.Disabled ? ActiveTheme.Chrome.DisabledOpacity : 1f;
            var text = (item.Danger
                ? ActiveTheme.Chrome.Danger
                : item.Disruptive
                    ? ActiveTheme.Chrome.Disruptive
                    : ActiveTheme.Chrome.Text).Fade(rowAlpha);
            // Raw tint: the canonical icon path applies the global
            // ImGui alpha exactly once inside the SVG renderer.
            var iconTint = text.Fade(hovered ? 1f : 0.8f);

            ImGui.SetCursorScreenPos(new Vector2(
                rowMin.X + ActiveTheme.Floating.MenuRowPadding * s,
                rowMin.Y + (ActiveTheme.Controls.ListRowHeight
                    - ActiveTheme.Controls.IconSize) * 0.5f * s));
            Icon(item.Icon, ActiveTheme.Controls.IconSize, iconTint);

            float textX = rowMin.X
                + (ActiveTheme.Floating.MenuRowPadding
                    + ActiveTheme.Controls.IconSize
                    + ActiveTheme.Floating.MenuIconGap) * s;
            float rowHeightPx =
                ActiveTheme.Controls.ListRowHeight * s;
            float labelRight = rowMax.X
                - ActiveTheme.Floating.MenuRowPadding * s;
            if (item.SubmenuItems is { Length: > 0 })
            {
                float arrowSize = ActiveTheme.Controls.IconSize * 0.8f;
                labelRight -= (arrowSize + ActiveTheme.Floating.MenuIconGap) * s;
                ImGui.SetCursorScreenPos(new Vector2(
                    labelRight,
                    rowMin.Y + (ActiveTheme.Controls.ListRowHeight
                        - arrowSize) * 0.5f * s));
                Icon(TablerIcon.ChevronRight, arrowSize, iconTint);
            }
            if (item.Shortcut is { Length: > 0 } shortcut)
            {
                var shortcutStyle = new TextStyle
                {
                    Size = ActiveTheme.Typography.CaptionSize,
                    Color = text.Fade(0.5f),
                };
                var shortcutSize =
                    MeasureText(shortcut, shortcutStyle);
                TextInBand(
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
                Size = ActiveTheme.Typography.BodySize,
                Color = text,
            };
            var labelSize =
                MeasureText(item.Label, labelStyle);
            float labelWidth = MathF.Max(1f, labelRight - textX);
            var labelBand = new Vector2(labelWidth, rowHeightPx);
            // CSS .label: flex 1, ellipsis. Constrain ONLY on
            // overflow: the truncate path clips to the line box, and
            // Segoe's descenders reach a hair below it — an
            // unconditional clip shaved the bottom off 'g'.
            if (labelSize.X > labelWidth)
                TextInBand(
                    new Vector2(textX, rowMin.Y),
                    labelBand,
                    item.Label,
                    labelStyle,
                    TextConstraint.Truncate(labelWidth),
                    besideIcon: true);
            else
                TextInBand(
                    new Vector2(textX, rowMin.Y),
                    labelBand,
                    item.Label,
                    labelStyle,
                    besideIcon: true);

            y += ActiveTheme.Controls.ListRowHeight * s;
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
        // Use the facing edges. A left-opening child must not turn its
        // parent's entire width into a bridge that steals row hover/clicks.
        bool opensLeft = submenuMin.X < parentMenuMin.X;
        float left = opensLeft ? submenuMin.X + submenuSize.X
            : parentMenuMin.X + parentMenuSize.X;
        float right = opensLeft ? parentMenuMin.X : submenuMin.X;
        float top = MathF.Min(parentRowMin.Y, submenuMin.Y);
        float bottom = MathF.Max(
            parentRowMax.Y,
            submenuMin.Y + submenuSize.Y);
        return point.X >= left && point.X <= right
            && point.Y >= top && point.Y < bottom;
    }
}
