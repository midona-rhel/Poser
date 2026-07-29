using System.Numerics;
using Dalamud.Bindings.ImGui;
using Poser.UI;
using Dalamud.Interface.Utility;
using FontFamily = Poser.UI.FontFamily;
using Ui = Poser.UI.Crystarium;

namespace Crystarium.Capture;

internal readonly record struct ComponentSpec(
    string Name,
    int Width,
    int Height);

internal static class ComponentCatalog
{
    private static readonly ComponentSpec[] Specs =
    [
        new("text-label", 320, 44),
        new("text-caption", 320, 44),
        new("text-mono", 320, 44),
        new("text-disabled", 320, 44),
        new("text-truncated", 320, 44),
        new("text-wrapped", 320, 96),
        new("action-button", 320, 80),
        new("primary-button", 320, 80),
        new("icon-button", 120, 80),
        new("icon-button-active", 120, 80),
        new("switch-off", 120, 80),
        new("switch-on", 120, 80),
        new("text-input", 320, 80),
        new("search-input", 320, 84),
        new("dropdown-closed", 320, 80),
        new("dropdown-open", 320, 280),
        new("color-palette", 220, 80),
        new("sidebar-row", 320, 80),
        new("sidebar-row-selected", 320, 80),
        new("property-row", 320, 68),
        new("section", 320, 92),
        new("tooltip", 240, 80),
        new("context-menu", 320, 190),
        new("modal", 560, 360),
    ];

    private static readonly string[] DropdownItems =
    [
        "Date Added",
        "Date Created",
        "Date Modified",
        "Name",
        "Rating",
        "File Size",
        "Duration",
    ];

    private static readonly ContextMenuItem[] MenuItems =
    [
        new("Set game target", TablerIcon.Crosshair),
        new("Hide", TablerIcon.EyeOff),
        new("Rename…", TablerIcon.Edit),
        ContextMenuItem.Separator,
        new("Despawn", TablerIcon.X, danger: true),
    ];

    public static IReadOnlyList<ComponentSpec> All => Specs;

    public static ComponentSpec Get(string name) =>
        Specs.FirstOrDefault(
            item => string.Equals(
                item.Name, name, StringComparison.OrdinalIgnoreCase))
        is { Name.Length: > 0 } match
            ? match
            : throw new ArgumentException(
                $"Unknown component '{name}'. " +
                $"Expected one of: {string.Join(", ", Specs.Select(x => x.Name))}.");

    public static Vector2 PointerFor(string name, float scale) =>
        name == "context-menu"
            ? new Vector2(40, 40) * scale
            : new Vector2(-1000, -1000);

    public static void Draw(string name, int frame, Vector2 canvas)
    {
        ImGui.SetNextWindowPos(Vector2.Zero);
        ImGui.SetNextWindowSize(canvas);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        ImGui.Begin(
            "##crystarium-conformance",
            ImGuiWindowFlags.NoDecoration
            | ImGuiWindowFlags.NoMove
            | ImGuiWindowFlags.NoSavedSettings
            | ImGuiWindowFlags.NoScrollWithMouse
            | ImGuiWindowFlags.NoBackground
            | ImGuiWindowFlags.NoBringToFrontOnFocus);
        ImGui.PopStyleVar();

        float scale =
            Dalamud.Interface.Utility.ImGuiHelpers.GlobalScale;
        var origin = new Vector2(24, 24) * scale;
        ImGui.SetCursorScreenPos(origin);

        switch (name)
        {
            case "text-label":
                // Picto typography: src/app/globals.css body — 13px
                // --font-family at --color-text-primary.
                Ui.Text("Actor display name");
                break;
            case "text-caption":
                // src/shared/ui/PropertyRow/PropertyRow.module.css .label —
                // 11px --color-text-secondary.
                Ui.Text("Opacity", new TextStyle
                {
                    Size = Ui.ActiveTheme.Typography.CaptionSize,
                    Color = Ui.ActiveTheme.TextDim,
                });
                break;
            case "text-mono":
                // PropertyRow.module.css .value.valueMono — 11px mono
                // tabular numerals at opacity .8 over text-primary.
                Ui.Text("1.000", new TextStyle
                {
                    Size = Ui.ActiveTheme.Typography.CaptionSize,
                    Family = FontFamily.Mono,
                    Color = Ui.ActiveTheme.Text with { W = 0.8f },
                });
                break;
            case "text-disabled":
                // src/shared/ui/ContextMenu/ContextMenu.module.css
                // .disabled — the ordinary label at opacity .4, Picto's
                // disabled-text idiom.
                Ui.Text("Unavailable action", new TextStyle { Disabled = true });
                break;
            case "text-truncated":
                // ContextMenu.module.css .label — single line,
                // ellipsis-truncated inside 140px.
                Ui.Text("The quick brown fox jumps over", default,
                    140f * ImGuiHelpers.GlobalScale, TextFit.Truncate);
                break;
            case "text-wrapped":
                // src/shared/ui/GlassModal/GlassModal.module.css
                // .helpText — 11px --color-text-tertiary wrapped at
                // line-height 1.4 inside 220px.
                Ui.Text(
                    "Poser keeps the incoming state and restores it exactly when the override is removed.",
                    new TextStyle
                    {
                        Size = Ui.ActiveTheme.Typography.CaptionSize,
                        Color = Ui.ActiveTheme.TextMuted,
                        LineHeight = 1.4f,
                    },
                    220f * ImGuiHelpers.GlobalScale, TextFit.Wrap);
                break;
            case "action-button":
                Ui.Button(
                    "Apply changes",
                    style: new ControlStyle
                    {
                        Width = UiWidth.Content,
                    },
                    id: "##action");
                break;
            case "primary-button":
                Ui.Button(
                    "Apply changes",
                    style: new ControlStyle
                    {
                        Width = UiWidth.Content,
                        Primary = true,
                    },
                    id: "##primary");
                break;
            case "icon-button":
                Ui.IconButton(
                    TablerIcon.Settings,
                    id: "##icon");
                break;
            case "icon-button-active":
                Ui.IconButton(
                    TablerIcon.Settings,
                    style: new ControlStyle { Selected = true },
                    id: "##icon-active");
                break;
            case "switch-off":
                Ui.Switch(
                    "##switch-off", false, _ => { });
                break;
            case "switch-on":
                Ui.Switch(
                    "##switch-on", true, _ => { });
                break;
            case "text-input":
                Ui.TextInput(
                    "##text-input",
                    "Filter scene…",
                    _ => { },
                    new ControlStyle
                    {
                        Width = UiWidth.Fixed(272),
                    });
                break;
            case "search-input":
                Ui.FilterPill(
                    "##search",
                    "Search",
                    _ => { },
                    "Search",
                    new ControlStyle
                    {
                        Width = UiWidth.Fixed(272),
                    });
                break;
            case "dropdown-closed":
                Ui.Dropdown(
                    "##dropdown-closed",
                    DropdownItems,
                    0,
                    _ => { },
                    new ControlStyle
                    {
                        Width = UiWidth.Content,
                    });
                break;
            case "dropdown-open":
                if (frame == 0)
                    Ui.FloatingSurface.OpenPopup(
                        "##dropdown-open_popup");
                Ui.Dropdown(
                    "##dropdown-open",
                    DropdownItems,
                    0,
                    _ => { },
                    new ControlStyle
                    {
                        Width = UiWidth.Content,
                    });
                break;
            case "color-palette":
                DrawPalette();
                break;
            case "sidebar-row":
                DrawSidebar(selected: false);
                break;
            case "sidebar-row-selected":
                DrawSidebar(selected: true);
                break;
            case "property-row":
                // No retained Crystarium counterpart exists. An empty
                // candidate is intentional: the report marks the Picto
                // foreground as missing instead of comparing invented chrome.
                break;
            case "section":
                Ui.Section(
                    "##section",
                    "GENERAL",
                    origin,
                    272 * scale,
                    open: false,
                    _ => { },
                    _ => { });
                break;
            case "tooltip":
                Ui.HoverHelp.Explain(
                    "##tooltip",
                    new Vector2(24, 2) * scale,
                    new Vector2(180, 18) * scale,
                    "Undo",
                    "Ctrl+Z",
                    HoverHelpSide.Bottom);
                break;
            case "context-menu":
                if (frame == 0)
                    Ui.FloatingMenu.Open(
                        "##context-menu",
                        origin,
                        MenuItems);
                Ui.FloatingMenu.Draw("##context-menu");
                break;
            case "modal":
                Ui.Modal(
                    "##modal",
                    true,
                    _ => { },
                    "Import pose",
                    () => Ui.Text(
                        "Apply heroic-stand.pose to Midona Rhel?"),
                    () =>
                    {
                        Ui.Button("Cancel", id: "##modal-cancel");
                        ImGui.SameLine(
                            0,
                            Ui.ActiveTheme.Page.ActionGap * scale);
                        Ui.Button(
                            "Import",
                            style: new ControlStyle { Primary = true },
                            id: "##modal-import");
                    },
                    ModalSize.Small,
                    height: 220);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(name));
        }

        ImGui.End();
    }

    private static void DrawPalette()
    {
        Vector4[] colors =
        [
            new(0x32 / 255f, 0x97 / 255f, 1, 1),
            new(0x9b / 255f, 0x7c / 255f, 1, 1),
            new(0x51 / 255f, 0xcf / 255f, 0x66 / 255f, 1),
            new(1, 0x6b / 255f, 0x6b / 255f, 1),
        ];
        for (int i = 0; i < colors.Length; i++)
        {
            if (i > 0)
                ImGui.SameLine(0, 8);
            Ui.Swatch(
                $"##swatch-{i}", colors[i], active: false);
        }
    }

    private static void DrawSidebar(bool selected)
    {
        var props = new SidebarRowProps
        {
            Icon = TablerIcon.User,
            Selected = selected,
            NoExpanderSlot = true,
        };
        Ui.SidebarRow(
            "##sidebar-row",
            "Midona Rhel",
            in props,
            new ControlStyle
            {
                Width = UiWidth.Fixed(272),
            });
    }
}
