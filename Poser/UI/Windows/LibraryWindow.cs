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

    /// <summary>The Objects tab's kind toggles, in the SPAWN PORTAL's
    /// tab order — actors, lights, cameras, props, objects, overlays —
    /// with the kinds the portal has no tab for (environments, groups)
    /// last (ruled 2026-09-01). Each wears its kind's own mark.</summary>
    private static readonly
        (global::Poser.Library.PoseLibraryEntryKind Kind, TablerIcon Icon,
            string Name)[]
        KindToggles =
    [
        (global::Poser.Library.PoseLibraryEntryKind.Actor, TablerIcon.User,
            "Actors"),
        (global::Poser.Library.PoseLibraryEntryKind.Light, TablerIcon.Bulb,
            "Lights"),
        (global::Poser.Library.PoseLibraryEntryKind.Camera, TablerIcon.Camera,
            "Cameras"),
        (global::Poser.Library.PoseLibraryEntryKind.Prop, TablerIcon.Moneybag,
            "Props"),
        (global::Poser.Library.PoseLibraryEntryKind.WorldObject, TablerIcon.Plant,
            "Objects"),
        (global::Poser.Library.PoseLibraryEntryKind.Overlay, TablerIcon.Message,
            "Overlays"),
        (global::Poser.Library.PoseLibraryEntryKind.Environment, TablerIcon.Sun,
            "Environments"),
        (global::Poser.Library.PoseLibraryEntryKind.Group, TablerIcon.Folder,
            "Groups"),
    ];

    /// <summary>The preview column, logical: the old 280 rail less a
    /// fifth.</summary>
    private const float PreviewColumnWidth = 224f;



    private bool _collapsed;
    private bool _restorePending;
    private float _savedHeight = 680f;
    private float _lastWidth = 1060f;

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
        // Collapse pins the window to its bar, exactly the shell's own
        // collapse; restore hands back the remembered height.
        float bar = Crystarium.ActiveTheme.Floating.ModalBarHeight;
        SizeConstraints = _collapsed
            ? new WindowSizeConstraints
            {
                MinimumSize = new Vector2(860f, bar),
                MaximumSize = new Vector2(float.MaxValue, bar),
            }
            : new WindowSizeConstraints
            {
                MinimumSize = new Vector2(860f, 520f),
                MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
            };
        if (_collapsed)
        {
            Size = new Vector2(_lastWidth, bar);
            SizeCondition = ImGuiCond.Always;
        }
        else if (_restorePending)
        {
            Size = new Vector2(_lastWidth, _savedHeight);
            SizeCondition = ImGuiCond.Always;
            _restorePending = false;
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
        if (!_main.IsOpen || Controls.ManipulationHide.Hidden)
            return;
        using var manipulationFade = Controls.ManipulationHide.FadeScope();
        float s = ImGuiHelpers.GlobalScale;
        var theme = Crystarium.ActiveTheme;
        var min = ImGui.GetWindowPos();
        var max = min + ImGui.GetWindowSize();
        var dl = ImGui.GetWindowDrawList();
        var owner = Interactive.BeginOwner(
            "poser-library", InteractionLayer.Window, min, max);
        try
        {
            // The library stands on the WORKSPACE ground — the darker
            // coat the main content well wears. The chrome's own glass
            // fill stands DOWN for it: two stacked translucent coats read
            // as one opaque slab.
            Crystarium.FloatingSurface.DrawChrome(
                dl, min, max, theme.Radii.Window, fill: false);
            var well = theme.Surface with
            { W = Crystarium.FloatingSurface.FillColor.W };
            dl.AddRectFilled(
                min + new Vector2(1f, 1f) * s,
                max - new Vector2(1f, 1f) * s,
                ImGui.ColorConvertFloat4ToU32(well),
                theme.Radii.Window * s);
            _lastWidth = (max.X - min.X) / s;
            float barBottom = DrawBar(min, max, s, dl);
            if (_collapsed)
                return;
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
                // The container owns its seat: the options button at the
                // column's left with the Preview title beside it, and the
                // menu opens leftward over the navigator. Only the types
                // with import options get the seat.
                _main.PoseFiles.DrawPreviewColumn(
                    columnOrigin, columnSize, optionsSeat: true);
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

        // The shell's own order: collapse stands far right, close to
        // its LEFT.
        float closeSide = theme.Floating.CloseActionSize;
        float actionX = max.X - theme.Floating.CloseInset * s - closeSide * s;
        float actionY = min.Y + (height - closeSide * s) * 0.5f;
        ImGui.SetCursorScreenPos(new Vector2(actionX, actionY));
        Crystarium.IconButton(
            _collapsed ? "chevron-down" : "chevron-up",
            ToggleCollapse,
            ControlStyle.Square(closeSide),
            help: _collapsed
                ? "Expand the window"
                : "Collapse to the title bar",
            id: "##library-collapse");
        ImGui.SetCursorScreenPos(new Vector2(
            actionX - theme.Page.ActionGap * s - closeSide * s, actionY));
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

        // Double-clicking the bar's open band collapses — the chevron's
        // gesture twin, the shell's own rule. The two buttons above keep
        // their clicks.
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

    private void ToggleCollapse()
    {
        if (!_collapsed)
            _savedHeight = ImGui.GetWindowSize().Y
                / Dalamud.Interface.Utility.ImGuiHelpers.GlobalScale;
        else
            _restorePending = true;
        _collapsed = !_collapsed;
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

        // The KIND toggles, right-aligned in the same band — Objects tab
        // only, where many kinds share one grid. Each is an icon button
        // that latches; several can be on at once, and the tiles show the
        // union (ruled 2026-08-31).
        if ((PoseLibraryPane.LibraryType)pane.SelectedType
            == PoseLibraryPane.LibraryType.Objects)
        {
            float buttonSide =
                Crystarium.ActiveTheme.Controls.ShellIconAction * s;
            float gap = theme.Spacing.Three * s;
            // The union leads; the kinds follow. No reset — all-on IS the
            // neutral state, and the union restores it (ruled 2026-09-01).
            int seats = KindToggles.Length + 1;
            float cluster = seats * buttonSide + (seats - 1) * gap;
            var seat = new Vector2(
                max.X - inset - cluster,
                top + (height - buttonSide) * 0.5f);
            bool allActive = true;
            foreach (var (kind, _, _) in KindToggles)
                allActive &= pane.KindFilterContains(kind);

            // The union is a true toggle: all on, or — pressed again while
            // everything is on — all off (ruled 2026-09-01).
            ImGui.SetCursorScreenPos(seat);
            Crystarium.TemporaryIconToggle(
                TablerIcon.LayersUnion,
                allActive,
                allActive ? pane.SetKindFilterNone : pane.SetKindFilterAll,
                help: "Show every kind",
                id: "##library-kind-all");
            seat.X += buttonSide + gap;

            // An admitted kind is latched (the pill's white highlight); a
            // filtered-out kind is dim but still CLICKABLE — the same press
            // re-admits it (ruled 2026-09-01, reversing same-day "inert").
            foreach (var (kind, icon, name) in KindToggles)
            {
                bool latched = pane.KindFilterContains(kind);
                ImGui.SetCursorScreenPos(seat);
                Crystarium.TemporaryIconToggle(
                    icon,
                    latched,
                    () => pane.ToggleKindFilter(kind),
                    help: name,
                    id: "##library-kind-" + name,
                    dimmed: !latched);
                seat.X += buttonSide + gap;
            }
        }

        float rule = MathF.Max(1f, s);
        dl.AddRectFilled(
            new Vector2(min.X, MathF.Round(top + height - rule)),
            new Vector2(max.X, MathF.Round(top + height)),
            ImGui.ColorConvertFloat4ToU32(
                ColorEx.ApplyAlpha(theme.FormSeparator)));
        return top + height;
    }
}
