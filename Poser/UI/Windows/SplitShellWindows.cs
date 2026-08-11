using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Windowing;
using Poser.UI.Views;

namespace Poser.UI;

/// <summary>The parts the shell can split off. The sidebar never splits —
/// it is the main window's anchor (user 2026-08-11).</summary>
public enum ShellPart
{
    Toolbar,
    Inspector,
}

/// <summary>
/// One detached part of the split shell: the file dialog's glass chassis —
/// shadow, blur, border, the same <see cref="Crystarium.FloatingSurface"/>
/// treatment every floating surface wears — with a modal-bar-height header
/// (the part's name and the reattach action) above a content box the
/// subclass fills. Parts draw from <see cref="MainWindow"/>'s per-frame view
/// model, so the window set registers them AFTER it; a part whose main
/// window is closed draws nothing rather than a stale frame.
/// </summary>
public abstract class ShellPartWindow : Window
{
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
        if (!Main.IsOpen)
            return;
        float s = ImGuiHelpers.GlobalScale;
        var theme = Crystarium.ActiveTheme;
        var min = ImGui.GetWindowPos();
        var max = min + ImGui.GetWindowSize();
        var dl = ImGui.GetWindowDrawList();
        var owner = Interactive.BeginOwner(
            _ownerId, InteractionLayer.Window, min, max);
        try
        {
            // The file dialog's chassis, verbatim: DrawChrome with its
            // defaults IS the glass every floating surface wears.
            Crystarium.FloatingSurface.DrawChrome(
                dl, min, max, theme.Radii.Window);
            float headerBottom = DrawHeader(min, max, s, dl);
            DrawContent(new Vector2(min.X, headerBottom), max, s);
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
        float height = theme.Floating.ModalBarHeight * s;
        float inset = theme.Floating.HeaderInset * s;
        Crystarium.TextInBand(
            new Vector2(min.X + inset, min.Y),
            new Vector2(max.X - min.X - inset * 2f, height),
            _label,
            new TextStyle
            {
                Size = theme.Typography.BodySize,
                Weight = FontWeight.SemiBold,
                Color = theme.Chrome.Text,
            });
        float side = theme.Floating.CloseActionSize;
        ImGui.SetCursorScreenPos(new Vector2(
            max.X - theme.Floating.CloseInset * s - side * s,
            min.Y + (height - side * s) * 0.5f));
        Crystarium.IconButton(
            "x",
            () => OnReattach?.Invoke(),
            ControlStyle.Square(side),
            help: "Reattach to the main window",
            id: _reattachId);
        float rule = MathF.Max(1f, s);
        dl.AddRectFilled(
            new Vector2(min.X, MathF.Round(min.Y + height - rule)),
            new Vector2(max.X, MathF.Round(min.Y + height)),
            ImGui.ColorConvertFloat4ToU32(
                ColorEx.ApplyAlpha(theme.FormSeparator)));
        return min.Y + height;
    }

    protected abstract void DrawContent(Vector2 min, Vector2 max, float s);
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

/// <summary>The toolbar as its own floating strip: the brand and its GPose
/// pill, then the four segment groups, self-sized, with the reattach on its
/// far end. Undo/redo/spawn/actions stay with the scene sidebar's title
/// cell.</summary>
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
        float inset = theme.Floating.HeaderInset;
        float side = theme.Floating.CloseActionSize;
        // Self-sized: content, one action gap, the reattach square, insets.
        Size = new Vector2(
            AppShellView.MeasureToolbar(_main.ShellVm) / s
                + inset * 2f
                + theme.Page.ActionGap
                + side,
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
            float side = theme.Floating.CloseActionSize;
            ImGui.SetCursorScreenPos(new Vector2(
                max.X - theme.Floating.CloseInset * s - side * s,
                min.Y + (size.Y - side * s) * 0.5f));
            Crystarium.IconButton(
                "x",
                () => OnReattach?.Invoke(),
                ControlStyle.Square(side),
                help: "Reattach to the main window",
                id: "##part-reattach-toolbar");
        }
        finally
        {
            Interactive.EndOwner(owner);
        }
    }
}
