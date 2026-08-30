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
            float stripBottom = DrawTypeStrip(min, max, barBottom, s, dl);

            var pane = _main.LibraryPane;
            var type = (PoseLibraryPane.LibraryType)pane.SelectedType;

            bool preview = type is PoseLibraryPane.LibraryType.Poses
                or PoseLibraryPane.LibraryType.AutoSaves;
            float inset = theme.Page.Inset * s;
            float columnSpan = (PreviewColumnWidth * s) + inset * 2f;
            float navigatorRight = max.X - columnSpan;
            float rule = MathF.Max(1f, s);

            // There is NO footer: the navigator takes the whole height,
            // and the right column is permanent — the preview where a
            // pose can preview, the file's metadata everywhere else. The
            // import options hide behind the importer's own menu, opened
            // from the settings seat by the preview.
            pane.Draw(
                new Vector2(min.X, stripBottom),
                new Vector2(
                    navigatorRight - min.X,
                    max.Y - stripBottom));
            dl.AddRectFilled(
                new Vector2(MathF.Round(navigatorRight), stripBottom),
                new Vector2(MathF.Round(navigatorRight) + rule, max.Y),
                ImGui.ColorConvertFloat4ToU32(
                    ColorEx.ApplyAlpha(theme.FormSeparator)));

            var columnOrigin = new Vector2(
                navigatorRight + rule + inset, stripBottom + inset);
            var columnSize = new Vector2(
                PreviewColumnWidth * s,
                max.Y - stripBottom - inset * 2f);
            if (preview)
            {
                _main.PoseFiles.DrawPreviewColumn(columnOrigin, columnSize);
                // The importer's own options menu, verbatim — copied, not
                // rebuilt. Only the types with import options get the seat.
                float side = theme.Controls.ShellIconAction;
                var seat = new Vector2(
                    columnOrigin.X + columnSize.X - side * s,
                    columnOrigin.Y);
                ImGui.SetCursorScreenPos(seat);
                Crystarium.IconButton(
                    "settings",
                    () => _main.PoseFiles.RequestImportMenu(
                        withPresets: false,
                        seat + new Vector2(0f, side * s)),
                    ControlStyle.Square(side),
                    help: "Import options",
                    id: "##library-options");
            }
            else if (type == PoseLibraryPane.LibraryType.Objects)
            {
                // The objects rail already leads with the file's name and
                // its properties — it IS the metadata panel here.
                pane.DrawObjectsRail(columnOrigin, columnSize);
            }
            else
            {
                pane.DrawInfoRail(columnOrigin, columnSize);
            }
        }
        finally
        {
            Interactive.EndOwner(owner);
        }
    }

    /// <summary>The bar: the title and the close — NOTHING else lives in
    /// a titlebar. The type strip gets its own band below.</summary>
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

    /// <summary>The type strip's own band, between the titlebar and the
    /// navigator — a titlebar carries a title, not navigation.</summary>
    private float DrawTypeStrip(
        Vector2 min, Vector2 max, float top, float s, ImDrawListPtr dl)
    {
        var theme = Crystarium.ActiveTheme;
        float height = theme.Floating.ModalBarHeight * s;
        float inset = theme.Page.Inset * s;

        var pane = _main.LibraryPane;
        int active = Array.IndexOf(
            StripOrder, (PoseLibraryPane.LibraryType)pane.SelectedType);
        var stripSize = Crystarium.MeasureSegmentedControl(StripLabels);
        ImGui.SetCursorScreenPos(new Vector2(
            min.X + inset,
            top + (height - stripSize.Y) * 0.5f));
        Crystarium.SegmentedControl(
            "##library-type",
            StripLabels,
            active < 0 ? 0 : active,
            index =>
            {
                if (index >= 0 && index < StripOrder.Length)
                    pane.SelectType((int)StripOrder[index]);
            });

        float rule = MathF.Max(1f, s);
        dl.AddRectFilled(
            new Vector2(min.X, MathF.Round(top + height - rule)),
            new Vector2(max.X, MathF.Round(top + height)),
            ImGui.ColorConvertFloat4ToU32(
                ColorEx.ApplyAlpha(theme.FormSeparator)));
        return top + height;
    }
}
