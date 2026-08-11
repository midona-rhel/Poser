using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Windowing;
using Poser.UI.Views;

namespace Poser.UI;

/// <summary>The three parts the shell can split off.</summary>
public enum ShellPart
{
    Sidebar,
    Toolbar,
    Inspector,
}

/// <summary>
/// One detached part of the split shell: an undecorated glass window with a
/// slim header band — the part's name and the reattach action — above a
/// content box the subclass fills. Parts draw from <see cref="MainWindow"/>'s
/// per-frame view model, so the window set registers them AFTER it; a part
/// whose main window is closed draws nothing rather than a stale frame.
/// </summary>
public abstract class ShellPartWindow : Window
{
    /// <summary>The header band: drag surface, label, reattach.</summary>
    protected const float HeaderHeight = 30f;

    protected readonly MainWindow Main;
    private readonly string _label;
    private readonly string _ownerId;
    private readonly string _reattachId;

    /// <summary>Reattach clicked: the binder flips the split flag off and the
    /// window-set sync closes this window.</summary>
    public event Action? OnReattach;

    protected ShellPartWindow(MainWindow main, string name, string label)
        : base(name,
            ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoCollapse |
            ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse |
            ImGuiWindowFlags.NoBackground)
    {
        Main = main;
        _label = label;
        _ownerId = $"poser-part-{label}";
        _reattachId = $"##part-reattach-{label}";
        RespectCloseHotkey = false;
    }

    // The same widget palette MainWindow pushes: the hosted panes render
    // identically whichever window seats them.
    public override void PreDraw()
    {
        base.PreDraw();
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
            10f * ImGuiHelpers.GlobalScale);
    }

    public override void PostDraw()
    {
        ImGui.PopStyleVar(3);
        ImGui.PopStyleColor(11);
        base.PostDraw();
    }

    public override void Draw()
    {
        if (!Main.IsOpen)
            return;
        float s = ImGuiHelpers.GlobalScale;
        var min = ImGui.GetWindowPos();
        var max = min + ImGui.GetWindowSize();
        var dl = ImGui.GetWindowDrawList();
        var owner = Interactive.BeginOwner(
            _ownerId, InteractionLayer.Window, min, max);
        try
        {
            float radius = Crystarium.ActiveTheme.Radii.Window;
            Crystarium.FloatingSurface.PrependShellBlur(
                dl, min, max, radius * s);
            Crystarium.FloatingSurface.DrawChrome(
                dl, min, max, radius, shadow: false, blur: false);
            float headerBottom = DrawHeader(min, max, s, dl);
            DrawContent(new Vector2(min.X, headerBottom), max, s);
            Crystarium.FloatingSurface.DrawBorder(min, max, radius);
        }
        finally
        {
            Interactive.EndOwner(owner);
        }
    }

    private float DrawHeader(
        Vector2 min, Vector2 max, float s, ImDrawListPtr dl)
    {
        var theme = Crystarium.ActiveTheme;
        float height = HeaderHeight * s;
        float inset = theme.Page.Inset * s;
        Crystarium.TextInBand(
            new Vector2(min.X + inset, min.Y),
            new Vector2(max.X - min.X - inset * 2f, height),
            _label,
            new TextStyle
            {
                Size = theme.Typography.CaptionSize,
                Weight = FontWeight.SemiBold,
                Color = theme.TextMuted,
            });
        float side = theme.Controls.ShellIconAction;
        ImGui.SetCursorScreenPos(new Vector2(
            max.X - inset - side * s,
            min.Y + (height - side * s) * 0.5f));
        Crystarium.IconButton(
            "x",
            () => OnReattach?.Invoke(),
            ControlStyle.Square(side),
            help: "Reattach to the main window",
            id: _reattachId);
        float rule = MathF.Max(1f, s);
        dl.AddRectFilled(
            new Vector2(min.X, min.Y + height),
            new Vector2(max.X, min.Y + height + rule),
            ImGui.ColorConvertFloat4ToU32(
                ColorEx.ApplyAlpha(Crystarium.ActiveTheme.FormSeparator)));
        return min.Y + height + rule;
    }

    protected abstract void DrawContent(Vector2 min, Vector2 max, float s);
}

/// <summary>The scene tree as its own floating window: the SAME retained
/// sidebar (cache, search, status bar) the shell seats when attached.
/// </summary>
public sealed class SidebarPartWindow : ShellPartWindow
{
    public SidebarPartWindow(MainWindow main)
        : base(main, $"Scene###{PluginConstants.PluginName}_split_sidebar",
            "Scene")
    {
        Size = new Vector2(300f, 520f);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(240f, 320f),
            MaximumSize = new Vector2(420f, float.MaxValue),
        };
    }

    protected override void DrawContent(Vector2 min, Vector2 max, float s) =>
        AppShellView.DrawSidebarContent(Main.ShellVm, min, max);
}

/// <summary>The inspector rail as its own floating window, hosting whatever
/// the rail would host attached — the selection inspector, or the library's
/// import options while the library is open.</summary>
public sealed class InspectorPartWindow : ShellPartWindow
{
    public InspectorPartWindow(MainWindow main)
        : base(main, $"Inspector###{PluginConstants.PluginName}_split_inspector",
            "Inspector")
    {
        Size = new Vector2(AppShellView.RailWidth, 560f);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(AppShellView.RailWidth, 320f),
            MaximumSize = new Vector2(AppShellView.RailWidth, float.MaxValue),
        };
    }

    protected override void DrawContent(Vector2 min, Vector2 max, float s) =>
        AppShellView.DrawRailContent(Main.ShellVm, min, max);
}

/// <summary>The gizmo toolbar as its own floating strip: undo/redo, spawn
/// and the four segment groups, self-sized, with the reattach on its far
/// end instead of a header band.</summary>
public sealed class ToolbarPartWindow : Window
{
    private readonly MainWindow _main;

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

    public override void PreDraw()
    {
        base.PreDraw();
        float s = ImGuiHelpers.GlobalScale;
        var theme = Crystarium.ActiveTheme;
        float inset = theme.Page.Inset;
        float side = theme.Controls.ShellIconAction;
        // Self-sized: content, one action gap, the reattach square, insets.
        Size = new Vector2(
            AppShellView.MeasureToolbar(_main.ShellVm) / s
                + inset * 2f
                + theme.Page.ActionGap
                + side,
            AppShellView.TitlebarHeight);
        SizeCondition = ImGuiCond.Always;
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0f);
        ImGui.PushStyleVar(
            ImGuiStyleVar.WindowRounding, 10f * ImGuiHelpers.GlobalScale);
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
            float radius = theme.Radii.Window;
            Crystarium.FloatingSurface.PrependShellBlur(
                dl, min, max, radius * s);
            Crystarium.FloatingSurface.DrawChrome(
                dl, min, max, radius, shadow: false, blur: false);
            float inset = theme.Page.Inset * s;
            AppShellView.DrawToolbarContent(
                _main.ShellVm, new Vector2(min.X + inset, min.Y), size.Y);
            float side = theme.Controls.ShellIconAction;
            ImGui.SetCursorScreenPos(new Vector2(
                max.X - inset - side * s,
                min.Y + (size.Y - side * s) * 0.5f));
            Crystarium.IconButton(
                "x",
                () => OnReattach?.Invoke(),
                ControlStyle.Square(side),
                help: "Reattach to the main window",
                id: "##part-reattach-toolbar");
            Crystarium.FloatingSurface.DrawBorder(min, max, radius);
        }
        finally
        {
            Interactive.EndOwner(owner);
        }
    }
}
