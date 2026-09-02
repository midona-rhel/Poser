using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Windowing;
using Poser.UI.Views;

namespace Poser.UI;

/// <summary>
/// Detached mode's SCENE window: the sidebar exactly as it lives attached —
/// search, tree, status bar — under a modal-height bar carrying undo/redo,
/// the spawn plus and the reattach. Draws from <see cref="MainWindow"/>'s
/// per-frame view model, so the window set registers it AFTER the main
/// window; the file dialog's glass chassis, verbatim.
/// </summary>
public sealed class SidebarPartWindow : Window
{
    private readonly MainWindow _main;
    private Vector2? _pendingPos;
    private Vector2? _pendingSize;
    // Collapse-to-titlebar, the shell contract every window keeps.
    private bool _collapsed;
    private bool? _pendingCollapsed;
    private Vector2 _lastLogicalSize = new(300f, 520f);
    private float _savedHeight = 520f;

    /// <summary>Reattach clicked: the window set merges the shell.</summary>
    public event Action? OnReattach;

    public SidebarPartWindow(MainWindow main)
        : base($"Sidebar###{PluginConstants.PluginName}_split_sidebar",
            ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoCollapse |
            ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse |
            ImGuiWindowFlags.NoBackground)
    {
        _main = main;
        Size = new Vector2(300f, 520f);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(240f, 320f),
            MaximumSize = new Vector2(420f, float.MaxValue),
        };
        RespectCloseHotkey = false;
    }

    /// <summary>Seats the window at the detach moment: the sidebar stays in
    /// the same place it occupied inside the main window, so the toggle
    /// reads as a split, not a teleport. Screen px; size logical.</summary>
    public void PlaceAt(Vector2 position, Vector2 sizeLogical)
    {
        _pendingPos = position;
        _pendingSize = sizeLogical;
    }

    public override void PreDraw()
    {
        base.PreDraw();
        float barHeight = Crystarium.ActiveTheme.Floating.ModalBarHeight;
        if (_pendingCollapsed is { } next)
        {
            if (next)
                _savedHeight = _lastLogicalSize.Y;
            _collapsed = next;
            _pendingCollapsed = null;
            _pendingSize = new Vector2(
                _lastLogicalSize.X, next ? barHeight : _savedHeight);
        }
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(240f, _collapsed ? barHeight : 320f),
            MaximumSize = new Vector2(
                420f, _collapsed ? barHeight : float.MaxValue),
        };
        if (_pendingPos is { } pos)
        {
            Position = pos;
            PositionCondition = ImGuiCond.Always;
            _pendingPos = null;
        }
        else
        {
            Position = null;
        }
        if (_pendingSize is { } size)
        {
            Size = size;
            SizeCondition = ImGuiCond.Always;
            _pendingSize = null;
        }
        else
        {
            SizeCondition = ImGuiCond.FirstUseEver;
        }
        ImGui.PushStyleColor(ImGuiCol.ChildBg, Vector4.Zero);
        ImGui.PushStyleColor(ImGuiCol.Text, Crystarium.ActiveTheme.Text);
        ImGui.PushStyleColor(ImGuiCol.TextDisabled, Crystarium.ActiveTheme.TextDim);
        ImGui.PushStyleColor(ImGuiCol.Border, Crystarium.ActiveTheme.Border);
        // Resize feedback — the grip and the lit border edge — is the
        // theme's accent, never Dalamud's global highlight.
        ImGui.PushStyleColor(ImGuiCol.ResizeGripHovered, Crystarium.ActiveTheme.Accent);
        ImGui.PushStyleColor(ImGuiCol.ResizeGripActive, Crystarium.ActiveTheme.Accent);
        ImGui.PushStyleColor(ImGuiCol.SeparatorHovered, Crystarium.ActiveTheme.Accent);
        ImGui.PushStyleColor(ImGuiCol.SeparatorActive, Crystarium.ActiveTheme.Accent);
        ImGui.PushStyleColor(ImGuiCol.Button, Crystarium.ActiveTheme.SurfaceRaised);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, Crystarium.ActiveTheme.AccentHover);
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, Crystarium.ActiveTheme.AccentActive);
        ImGui.PushStyleColor(ImGuiCol.FrameBg, Crystarium.ActiveTheme.SurfaceSunken);
        ImGui.PushStyleColor(ImGuiCol.Header, Crystarium.ActiveTheme.Accent);
        ImGui.PushStyleColor(ImGuiCol.HeaderHovered, Crystarium.ActiveTheme.AccentHover);
        ImGui.PushStyleColor(ImGuiCol.HeaderActive, Crystarium.ActiveTheme.AccentActive);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0f);
        ImGui.PushStyleVar(
            ImGuiStyleVar.WindowRounding,
            Crystarium.ActiveTheme.Radii.Window * ImGuiHelpers.GlobalScale);
    }

    public override void PostDraw()
    {
        ImGui.PopStyleVar(3);
        ImGui.PopStyleColor(15);
        base.PostDraw();
    }

    public override void Draw()
    {
        if (!_main.IsOpen
            || (Controls.ManipulationHide.Hidden && !Controls.ManipulationDrag.ShellHeld))
            return;
        using var manipulationFade = Controls.ManipulationHide.FadeScope();
        float s = ImGuiHelpers.GlobalScale;
        var theme = Crystarium.ActiveTheme;
        var min = ImGui.GetWindowPos();
        var max = min + ImGui.GetWindowSize();
        _lastLogicalSize = (max - min) / s;
        var dl = ImGui.GetWindowDrawList();
        var owner = Interactive.BeginOwner(
            "poser-part-sidebar", InteractionLayer.Window, min, max);
        try
        {
            Crystarium.FloatingSurface.DrawChrome(
                dl, min, max, theme.Radii.Window);
            float headerBottom = DrawBar(min, max, s, dl);
            if (!_collapsed)
                AppShellView.DrawSidebarContent(
                    _main.ShellVm, new Vector2(min.X, headerBottom), max);
        }
        finally
        {
            Interactive.EndOwner(owner);
        }
    }

    /// <summary>The scene bar: the window's name, the spawn plus, the
    /// reattach — undo/redo live on the toolbar strip (user 2026-08-11).
    /// </summary>
    private float DrawBar(Vector2 min, Vector2 max, float s, ImDrawListPtr dl)
    {
        var theme = Crystarium.ActiveTheme;
        var vm = _main.ShellVm;
        float height = theme.Floating.ModalBarHeight * s;
        // The label stands on the content column's inset — the search pill's
        // own left edge — so the window's left side reads as one line.
        float inset = theme.Page.Inset * s;
        float side = theme.Controls.ShellIconAction;
        float y = min.Y + (height - side * s) * 0.5f;

        Crystarium.TextInBand(
            new Vector2(min.X + inset, min.Y),
            new Vector2(max.X - min.X - inset * 2f, height),
            "Sidebar",
            new TextStyle
            {
                Size = theme.Typography.BodySize,
                Weight = FontWeight.SemiBold,
                Color = theme.Chrome.Text,
            });

        float closeSide = theme.Floating.CloseActionSize;
        // The shell's own order: collapse stands far right, the merge to
        // its LEFT.
        float chevronX = max.X - theme.Floating.CloseInset * s - closeSide * s;
        ImGui.SetCursorScreenPos(new Vector2(
            chevronX,
            min.Y + (height - closeSide * s) * 0.5f));
        Crystarium.IconButton(
            _collapsed ? "chevron-down" : "chevron-up",
            ToggleCollapse,
            ControlStyle.Square(closeSide),
            help: _collapsed
                ? "Expand the window"
                : "Collapse to the title bar",
            id: "##part-collapse-sidebar");
        float closeX = chevronX - theme.Page.ActionGap * s - closeSide * s;
        ImGui.SetCursorScreenPos(new Vector2(
            closeX,
            min.Y + (height - closeSide * s) * 0.5f));
        Crystarium.IconButton(
            "x",
            () => OnReattach?.Invoke(),
            ControlStyle.Square(closeSide),
            help: "Attach the sidebar",
            id: "##part-reattach-sidebar");
        // The library button is the sidebar titlebar's — this window IS
        // the sidebar's titlebar while the shell is split. TWO sidebars,
        // ONE contract: the same TEXT button the merged cell carries.
        if (vm.OnLibrary is { } onLibrary)
        {
            var labelStyle = new TextStyle
            { Size = theme.Typography.LabelSize };
            float labelWidth = Crystarium.MeasureText(
                "Library", labelStyle).X;
            float buttonWidth = labelWidth / s + theme.Spacing.Six * 2f;
            ImGui.SetCursorScreenPos(new Vector2(
                closeX - theme.Spacing.Two * s - buttonWidth * s,
                min.Y + (height - closeSide * s) * 0.5f));
            Crystarium.Button(
                "Library",
                onLibrary,
                style: ControlStyle.Square(closeSide) with
                { Width = UiWidth.Fixed(buttonWidth) },
                help: "Open the library",
                id: "##part-library-sidebar");
        }

        float rule = MathF.Max(1f, s);
        dl.AddRectFilled(
            new Vector2(min.X, MathF.Round(min.Y + height - rule)),
            new Vector2(max.X, MathF.Round(min.Y + height)),
            ImGui.ColorConvertFloat4ToU32(
                ColorEx.ApplyAlpha(theme.FormSeparator)));

        // Double-clicking the bar's open band collapses — the chevron's
        // gesture twin, every shell window's rule.
        if (ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left)
            && !ImGui.IsAnyItemHovered())
        {
            var barMouse = ImGui.GetMousePos();
            if (barMouse.X >= min.X && barMouse.X < max.X
                && barMouse.Y >= min.Y && barMouse.Y < min.Y + height)
                ToggleCollapse();
        }
        return min.Y + height;
    }

    /// <summary>Deferred to PreDraw: the state and the size must land in
    /// the SAME frame, or the body draws one frame inside a bar-height
    /// window — the one-frame settle the standard forbids.</summary>
    private void ToggleCollapse() => _pendingCollapsed = !_collapsed;
}

/// <summary>Detached mode's TOOLBAR strip: the brand and its GPose pill,
/// the command menu, the four segment groups, self-sized, reattach on the
/// far end. The file dialog's glass chassis, verbatim.</summary>
public sealed class ToolbarPartWindow : Window
{
    private readonly MainWindow _main;
    private Vector2? _pendingPos;

    public event Action? OnReattach;

    public ToolbarPartWindow(MainWindow main)
        : base($"Toolbar###{PluginConstants.PluginName}_split_toolbar",
            ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoCollapse |
            ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse |
            ImGuiWindowFlags.NoBackground | ImGuiWindowFlags.NoResize)
    {
        _main = main;
        RespectCloseHotkey = false;
    }

    /// <summary>Seats the strip at the detach moment (screen px).</summary>
    public void PlaceAt(Vector2 position) => _pendingPos = position;

    public override void PreDraw()
    {
        base.PreDraw();
        if (_pendingPos is { } pos)
        {
            Position = pos;
            PositionCondition = ImGuiCond.Always;
            _pendingPos = null;
        }
        else
        {
            Position = null;
        }
        float s = ImGuiHelpers.GlobalScale;
        var theme = Crystarium.ActiveTheme;
        float inset = theme.Floating.HeaderInset;
        float side = theme.Floating.CloseActionSize;
        // Self-sized: content and insets. The toolbar is permanently its
        // own window, so it carries no reattach square.
        _ = side;
        Size = new Vector2(
            AppShellView.MeasureToolbar(_main.ShellVm) / s
                + inset * 2f,
            AppShellView.CollapsedBarHeight);
        SizeCondition = ImGuiCond.Always;
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0f);
        ImGui.PushStyleVar(
            ImGuiStyleVar.WindowRounding,
            theme.Radii.Window * ImGuiHelpers.GlobalScale);
    }

    public override void PostDraw()
    {
        ImGui.PopStyleVar(3);
        base.PostDraw();
    }

    public override void Draw()
    {
        if (!_main.IsOpen
            || (Controls.ManipulationHide.Hidden && !Controls.ManipulationDrag.ShellHeld))
            return;
        using var manipulationFade = Controls.ManipulationHide.FadeScope();
        float s = ImGuiHelpers.GlobalScale;
        var theme = Crystarium.ActiveTheme;
        var min = ImGui.GetWindowPos();
        var size = ImGui.GetWindowSize();
        var max = min + size;
        var dl = ImGui.GetWindowDrawList();
        var owner = Interactive.BeginOwner(
            "poser-part-toolbar", InteractionLayer.Window, min, max);
        try
        {
            Crystarium.FloatingSurface.DrawChrome(
                dl, min, max, theme.Radii.Window);
            float inset = theme.Floating.HeaderInset * s;
            AppShellView.DrawToolbarContent(
                _main.ShellVm, new Vector2(min.X + inset, min.Y), size.Y);

        }
        finally
        {
            Interactive.EndOwner(owner);
        }
    }
}

/// <summary>The split INSPECTOR window: the rail exactly as it lives in
/// the shell — same content seam, same width — under its own bar. It
/// exists while the inspector is split from the properties window; the
/// bar's merge folds it back in.</summary>
public sealed class InspectorPartWindow : Window
{
    private readonly MainWindow _main;
    private Vector2? _pendingPos;
    private Vector2? _pendingSize;
    private bool _collapsed;
    private bool? _pendingCollapsed;
    private Vector2 _lastLogicalSize = new(282f, 560f);
    private float _savedHeight = 560f;

    /// <summary>Merge clicked: the rail returns to the shell.</summary>
    public event Action? OnMerge;

    public InspectorPartWindow(MainWindow main)
        : base($"Inspector###{PluginConstants.PluginName}_split_inspector",
            ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoCollapse |
            ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse |
            ImGuiWindowFlags.NoBackground)
    {
        _main = main;
        float width = AppShellView.RailWidth + 2f;
        Size = new Vector2(width, 560f);
        SizeCondition = ImGuiCond.FirstUseEver;
        // The rail's width is a design constant: the window resizes in
        // HEIGHT only, exactly as the attached rail does.
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(width, 320f),
            MaximumSize = new Vector2(width, float.MaxValue),
        };
        RespectCloseHotkey = false;
    }

    /// <summary>Seats the window where the rail stood at the split moment,
    /// so the toggle reads as a split, not a teleport.</summary>
    public void PlaceAt(Vector2 position, Vector2 sizeLogical)
    {
        _pendingPos = position;
        _pendingSize = sizeLogical;
    }

    public override void PreDraw()
    {
        base.PreDraw();
        float width = AppShellView.RailWidth + 2f;
        float barHeight = Crystarium.ActiveTheme.Floating.ModalBarHeight;
        if (_pendingCollapsed is { } next)
        {
            if (next)
                _savedHeight = _lastLogicalSize.Y;
            _collapsed = next;
            _pendingCollapsed = null;
            _pendingSize = new Vector2(width, next ? barHeight : _savedHeight);
        }
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(width, _collapsed ? barHeight : 320f),
            MaximumSize = new Vector2(
                width, _collapsed ? barHeight : float.MaxValue),
        };
        if (_pendingPos is { } pos)
        {
            Position = pos;
            PositionCondition = ImGuiCond.Always;
            _pendingPos = null;
        }
        else
        {
            Position = null;
        }
        if (_pendingSize is { } size)
        {
            Size = size;
            SizeCondition = ImGuiCond.Always;
            _pendingSize = null;
        }
        else
        {
            SizeCondition = ImGuiCond.FirstUseEver;
        }
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0f);
        ImGui.PushStyleVar(
            ImGuiStyleVar.WindowRounding,
            Crystarium.ActiveTheme.Radii.Window * ImGuiHelpers.GlobalScale);
    }

    public override void PostDraw()
    {
        ImGui.PopStyleVar(3);
        base.PostDraw();
    }

    public override void Draw()
    {
        if (!_main.IsOpen
            || (Controls.ManipulationHide.Hidden && !Controls.ManipulationDrag.ShellHeld))
            return;
        using var manipulationFade = Controls.ManipulationHide.FadeScope();
        float s = ImGuiHelpers.GlobalScale;
        var theme = Crystarium.ActiveTheme;
        var min = ImGui.GetWindowPos();
        var max = min + ImGui.GetWindowSize();
        _lastLogicalSize = (max - min) / s;
        var dl = ImGui.GetWindowDrawList();
        var owner = Interactive.BeginOwner(
            "poser-part-inspector", InteractionLayer.Window, min, max);
        try
        {
            Crystarium.FloatingSurface.DrawChrome(
                dl, min, max, theme.Radii.Window);
            float headerBottom = DrawBar(min, max, s, dl);
            if (!_collapsed)
                AppShellView.DrawRailContent(
                    _main.ShellVm, new Vector2(min.X, headerBottom), max);
        }
        finally
        {
            Interactive.EndOwner(owner);
        }
    }

    private float DrawBar(Vector2 min, Vector2 max, float s, ImDrawListPtr dl)
    {
        var theme = Crystarium.ActiveTheme;
        float height = theme.Floating.ModalBarHeight * s;
        float inset = theme.Page.Inset * s;

        Crystarium.TextInBand(
            new Vector2(min.X + inset, min.Y),
            new Vector2(max.X - min.X - inset * 2f, height),
            "Inspector",
            new TextStyle
            {
                Size = theme.Typography.BodySize,
                Weight = FontWeight.SemiBold,
                Color = theme.Chrome.Text,
            });

        float closeSide = theme.Floating.CloseActionSize;
        // The shell's own order: collapse stands far right, the merge to
        // its LEFT.
        float chevronX = max.X - theme.Floating.CloseInset * s - closeSide * s;
        ImGui.SetCursorScreenPos(new Vector2(
            chevronX,
            min.Y + (height - closeSide * s) * 0.5f));
        Crystarium.IconButton(
            _collapsed ? "chevron-down" : "chevron-up",
            ToggleCollapse,
            ControlStyle.Square(closeSide),
            help: _collapsed
                ? "Expand the window"
                : "Collapse to the title bar",
            id: "##part-collapse-inspector");
        float closeX = chevronX - theme.Page.ActionGap * s - closeSide * s;
        ImGui.SetCursorScreenPos(new Vector2(
            closeX,
            min.Y + (height - closeSide * s) * 0.5f));
        Crystarium.IconButton(
            "x",
            () => OnMerge?.Invoke(),
            ControlStyle.Square(closeSide),
            help: "Attach the inspector",
            id: "##part-merge-inspector");

        float rule = MathF.Max(1f, s);
        dl.AddRectFilled(
            new Vector2(min.X, MathF.Round(min.Y + height - rule)),
            new Vector2(max.X, MathF.Round(min.Y + height)),
            ImGui.ColorConvertFloat4ToU32(
                ColorEx.ApplyAlpha(theme.FormSeparator)));

        // Double-clicking the bar's open band collapses — the chevron's
        // gesture twin, every shell window's rule.
        if (ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left)
            && !ImGui.IsAnyItemHovered())
        {
            var barMouse = ImGui.GetMousePos();
            if (barMouse.X >= min.X && barMouse.X < max.X
                && barMouse.Y >= min.Y && barMouse.Y < min.Y + height)
                ToggleCollapse();
        }
        return min.Y + height;
    }

    /// <summary>Deferred to PreDraw: the state and the size must land in
    /// the SAME frame, or the body draws one frame inside a bar-height
    /// window — the one-frame settle the standard forbids.</summary>
    private void ToggleCollapse() => _pendingCollapsed = !_collapsed;
}
