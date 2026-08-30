using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Windowing;
using Poser.UI.Views;

namespace Poser.UI;

/// <summary>
/// The library: its OWN window — never a mode of the main window, never
/// replacing the properties window. The bar carries the title, the type
/// strip and the close; the body is the file navigator; the types that can
/// preview get a plain preview column (the old rail less a fifth, and NOT
/// styled as an inspector — the library has none); every other type hands
/// that width back to the navigator. The footer is the importer-style
/// options band: ONE height whatever the type, so switching tabs never
/// reflows the frame — only the band's content changes.
/// </summary>
public sealed class LibraryWindow : Window
{
    private readonly MainWindow _main;

    /// <summary>The strip's display order as pane types: the tabs that can
    /// preview lead, the file-info tabs stand at the far right. Positional
    /// against <see cref="StripLabels"/>.</summary>
    private static readonly PoseLibraryPane.LibraryType[] StripOrder =
    [
        PoseLibraryPane.LibraryType.Poses,
        PoseLibraryPane.LibraryType.AutoSaves,
        PoseLibraryPane.LibraryType.Objects,
        PoseLibraryPane.LibraryType.Mcdf,
        PoseLibraryPane.LibraryType.Scenes,
    ];

    private static readonly string[] StripLabels =
        ["Poses", "Auto-saves", "Objects", "MCDF", "Scenes"];

    /// <summary>The preview column, logical: the old 280 rail less a
    /// fifth.</summary>
    private const float PreviewColumnWidth = 224f;

    /// <summary>The scenes footer's file-info column, logical; the load
    /// options take the rest of the band.</summary>
    private const float SceneInfoColumnWidth = 320f;

    public LibraryWindow(MainWindow main)
        : base($"Library###{PluginConstants.PluginName}_library",
            ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoCollapse |
            ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse |
            ImGuiWindowFlags.NoBackground)
    {
        _main = main;
        Size = new Vector2(1060f, 680f);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(860f, 520f),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };
        RespectCloseHotkey = false;
    }

    public override void OnClose()
    {
        base.OnClose();
        _main.LibraryPane.OnHidden();
    }

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
        if (!_main.IsOpen)
            return;
        float s = ImGuiHelpers.GlobalScale;
        var theme = Crystarium.ActiveTheme;
        var min = ImGui.GetWindowPos();
        var max = min + ImGui.GetWindowSize();
        var dl = ImGui.GetWindowDrawList();
        var owner = Interactive.BeginOwner(
            "poser-library", InteractionLayer.Window, min, max);
        try
        {
            Crystarium.FloatingSurface.DrawChrome(
                dl, min, max, theme.Radii.Window);
            float barBottom = DrawBar(min, max, s, dl);

            var pane = _main.LibraryPane;
            var type = (PoseLibraryPane.LibraryType)pane.SelectedType;

            // ONE footer height for every type — the band's content
            // changes, its frame does not, so the strip never reflows
            // the window.
            float footerHeight = _main.PoseFiles.OptionsBandHeight() * s;
            float rule = MathF.Max(1f, s);
            float footerTop = max.Y - footerHeight - rule;
            dl.AddRectFilled(
                new Vector2(min.X, MathF.Round(footerTop)),
                new Vector2(max.X, MathF.Round(footerTop + rule)),
                ImGui.ColorConvertFloat4ToU32(
                    ColorEx.ApplyAlpha(theme.FormSeparator)));

            bool preview = type is PoseLibraryPane.LibraryType.Poses
                or PoseLibraryPane.LibraryType.AutoSaves;
            float inset = theme.Page.Inset * s;
            float previewSpan = preview
                ? (PreviewColumnWidth * s) + inset * 2f
                : 0f;

            pane.Draw(
                new Vector2(min.X, barBottom),
                new Vector2(
                    max.X - min.X - previewSpan,
                    footerTop - barBottom));
            if (preview)
                _main.PoseFiles.DrawPreviewColumn(
                    new Vector2(
                        max.X - previewSpan + inset, barBottom + inset),
                    new Vector2(
                        PreviewColumnWidth * s,
                        footerTop - barBottom - inset * 2f));

            var footerOrigin = new Vector2(min.X, footerTop + rule);
            var footerSize = new Vector2(max.X - min.X, footerHeight);
            switch (type)
            {
                case PoseLibraryPane.LibraryType.Poses:
                case PoseLibraryPane.LibraryType.AutoSaves:
                    _main.PoseFiles.DrawOptionsBand(
                        footerOrigin, footerSize, pane.SelectedPath);
                    break;
                case PoseLibraryPane.LibraryType.Scenes:
                    // The file leads, the load options take the rest of
                    // the band — the same options a tile's load runs.
                    pane.DrawInfoRail(
                        footerOrigin,
                        new Vector2(SceneInfoColumnWidth * s, footerHeight));
                    _main.Scene.DrawLibraryRail(
                        footerOrigin + new Vector2(SceneInfoColumnWidth * s, 0f),
                        footerSize - new Vector2(SceneInfoColumnWidth * s, 0f));
                    break;
                case PoseLibraryPane.LibraryType.Mcdf:
                    pane.DrawInfoRail(footerOrigin, footerSize);
                    break;
                case PoseLibraryPane.LibraryType.Objects:
                    pane.DrawObjectsRail(footerOrigin, footerSize);
                    break;
            }
        }
        finally
        {
            Interactive.EndOwner(owner);
        }
    }

    /// <summary>The bar: the title, the type strip, the close.</summary>
    private float DrawBar(Vector2 min, Vector2 max, float s, ImDrawListPtr dl)
    {
        var theme = Crystarium.ActiveTheme;
        float height = theme.Floating.ModalBarHeight * s;
        float inset = theme.Page.Inset * s;

        var titleStyle = new TextStyle
        {
            Size = theme.Typography.BodySize,
            Weight = FontWeight.SemiBold,
            Color = theme.Chrome.Text,
        };
        float titleWidth = Crystarium.MeasureText("Library", titleStyle).X;
        Crystarium.TextInBand(
            new Vector2(min.X + inset, min.Y),
            new Vector2(titleWidth, height),
            "Library",
            titleStyle);

        var pane = _main.LibraryPane;
        int active = Array.IndexOf(
            StripOrder, (PoseLibraryPane.LibraryType)pane.SelectedType);
        var stripSize = Crystarium.MeasureSegmentedControl(StripLabels);
        ImGui.SetCursorScreenPos(new Vector2(
            min.X + inset + titleWidth + theme.Spacing.Six * s,
            min.Y + (height - stripSize.Y) * 0.5f));
        Crystarium.SegmentedControl(
            "##library-type",
            StripLabels,
            active < 0 ? 0 : active,
            index =>
            {
                if (index >= 0 && index < StripOrder.Length)
                    pane.SelectType((int)StripOrder[index]);
            });

        float closeSide = theme.Floating.CloseActionSize;
        ImGui.SetCursorScreenPos(new Vector2(
            max.X - theme.Floating.CloseInset * s - closeSide * s,
            min.Y + (height - closeSide * s) * 0.5f));
        Crystarium.IconButton(
            "x",
            () => IsOpen = false,
            ControlStyle.Square(closeSide),
            help: "Close the library",
            id: "##library-close");

        float rule = MathF.Max(1f, s);
        dl.AddRectFilled(
            new Vector2(min.X, MathF.Round(min.Y + height - rule)),
            new Vector2(max.X, MathF.Round(min.Y + height)),
            ImGui.ColorConvertFloat4ToU32(
                ColorEx.ApplyAlpha(theme.FormSeparator)));
        return min.Y + height;
    }
}
