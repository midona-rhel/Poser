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

    /// <summary>Reattach clicked: the window set merges the shell.</summary>
    public event Action? OnReattach;

    public SidebarPartWindow(MainWindow main)
        : base($"Scene###{PluginConstants.PluginName}_split_sidebar",
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
        ImGui.PopStyleColor(11);
        base.PostDraw();
    }

    public override void Draw()
    {
        if (!_main.IsOpen)
            return;
        float s = ImGuiHelpers.GlobalScale;
        var theme = Crystarium.ActiveTheme;
        var min = ImGui.GetWindowPos();
        var max = min + ImGui.GetWindowSize();
        var dl = ImGui.GetWindowDrawList();
        var owner = Interactive.BeginOwner(
            "poser-part-sidebar", InteractionLayer.Window, min, max);
        try
        {
            Crystarium.FloatingSurface.DrawChrome(
                dl, min, max, theme.Radii.Window);
            float headerBottom = DrawBar(min, max, s, dl);
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
            "Scene",
            new TextStyle
            {
                Size = theme.Typography.BodySize,
                Weight = FontWeight.SemiBold,
                Color = theme.Chrome.Text,
            });

        float closeSide = theme.Floating.CloseActionSize;
        ImGui.SetCursorScreenPos(new Vector2(
            max.X - theme.Floating.CloseInset * s - closeSide * s,
            min.Y + (height - closeSide * s) * 0.5f));
        Crystarium.IconButton(
            "x",
            () => OnReattach?.Invoke(),
            ControlStyle.Square(closeSide),
            help: "Merge the shell back into one window",
            id: "##part-reattach-sidebar");

        float rule = MathF.Max(1f, s);
        dl.AddRectFilled(
            new Vector2(min.X, MathF.Round(min.Y + height - rule)),
            new Vector2(max.X, MathF.Round(min.Y + height)),
            ImGui.ColorConvertFloat4ToU32(
                ColorEx.ApplyAlpha(theme.FormSeparator)));
        return min.Y + height;
    }
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
        if (!_main.IsOpen)
            return;
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
