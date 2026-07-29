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
        new("text-disabled", 320, 74),
        new("text-truncated", 320, 44),
        new("text-truncated-cjk", 320, 44),
        new("text-truncated-combining", 320, 44),
        new("text-truncated-emoji", 320, 44),
        new("text-truncated-fit", 320, 44),
        new("text-truncated-narrow", 320, 44),
        new("text-truncated-flow", 320, 44),
        new("text-wrapped", 320, 96),
        new("text-wrapped-newline", 320, 130),
        new("text-wrapped-overwide", 320, 100),
        new("text-wrapped-flow", 320, 130),
        new("text-ws-collapse", 320, 80),
        new("text-ws-prewrap", 320, 110),
        new("text-ws-tab", 320, 74),
        new("text-ws-crlf", 320, 130),
        new("text-align-end", 320, 88),
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
            {
                // src/shared/ui/ContextMenu/ContextMenu.module.css
                // .item.disabled > .label — the real Picto disabled
                // selector: opacity .4 on a 26px flex menu row with 6px
                // horizontal padding, 13px text-primary, flex-centered.
                var disabledStyle = new TextStyle { Disabled = true };
                float rowHeight = 26f * ImGuiHelpers.GlobalScale;
                float lineHeight = Ui.MeasureText("Unavailable action", disabledStyle).Y;
                Ui.TextAt(
                    origin + new Vector2(
                        6f * ImGuiHelpers.GlobalScale,
                        (rowHeight - lineHeight) * 0.5f),
                    "Unavailable action", disabledStyle);
                break;
            }
            case "text-truncated":
                // ContextMenu.module.css .label — single line,
                // ellipsis-truncated inside 140px.
                Ui.Text("The quick brown fox jumps over", default,
                    TextConstraint.Truncate(140f * ImGuiHelpers.GlobalScale));
                break;
            case "text-truncated-cjk":
                // ContextMenu.module.css .label idiom — grapheme-safe
                // backoff over ideographs (no Latin word boundaries).
                Ui.Text("素早い茶色の狐が飛び跳ねる", default,
                    TextConstraint.Truncate(140f * ImGuiHelpers.GlobalScale));
                break;
            case "text-truncated-combining":
                // ContextMenu.module.css .label idiom — combining acute
                // accents (U+0301) must never separate from their base.
                Ui.Text("réservé déjà touché", default,
                    TextConstraint.Truncate(100f * ImGuiHelpers.GlobalScale));
                break;
            case "text-truncated-emoji":
                // ContextMenu.module.css .label idiom — the surrogate
                // pair (U+1F600) must never be split by the backoff.
                Ui.Text("Emoji \U0001F600 marker overflow test", default,
                    TextConstraint.Truncate(110f * ImGuiHelpers.GlobalScale));
                break;
            case "text-truncated-fit":
                // ContextMenu.module.css .label idiom — text that fits
                // must pass through untouched, no ellipsis.
                Ui.Text("The quick brown fox", default,
                    TextConstraint.Truncate(140f * ImGuiHelpers.GlobalScale));
                break;
            case "text-truncated-narrow":
                // ContextMenu.module.css .label idiom — narrower than
                // the ellipsis itself: Blink drops the ellipsis and
                // clips the raw run, and so does the renderer's clip.
                Ui.Text("Unreachable", default,
                    TextConstraint.Truncate(6f * ImGuiHelpers.GlobalScale));
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
                    },
                    TextConstraint.Wrap(220f * ImGuiHelpers.GlobalScale, 1.4f));
                break;
            case "text-wrapped-newline":
                // src/shared/ui/InspectorField/InspectorField.module.css
                // .popover .popoverText — 13px text-primary at
                // line-height 1.5, white-space: pre-wrap preserving the
                // explicit newline, inside 200px.
                Ui.Text(
                    "First line\nSecond block that wraps onward, and onward.",
                    default,
                    TextConstraint.Wrap(
                        200f * ImGuiHelpers.GlobalScale, 1.5f,
                        TextWhitespace.PreWrap));
                break;
            case "text-wrapped-overwide":
                // GlassModal.module.css .helpText inside 120px — the
                // over-wide token overflows its line (CSS overflow-wrap
                // normal), it is never hard-broken.
                Ui.Text(
                    "A veryverylongunbrokentoken overflows.",
                    new TextStyle
                    {
                        Size = Ui.ActiveTheme.Typography.CaptionSize,
                        Color = Ui.ActiveTheme.TextMuted,
                    },
                    TextConstraint.Wrap(120f * ImGuiHelpers.GlobalScale, 1.4f));
                break;
            case "text-truncated-flow":
                // ContextMenu.module.css .label in a fixed 140px flex slot
                // with a VISIBLE following sibling (flex gap 0): the
                // truncated run occupies its constraint width in layout,
                // so the sibling starts at the box edge.
                ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, Vector2.Zero);
                Ui.Text("The quick brown fox jumps over", default,
                    TextConstraint.Truncate(140f * ImGuiHelpers.GlobalScale));
                ImGui.SameLine();
                Ui.Text("Next");
                ImGui.PopStyleVar();
                break;
            case "text-wrapped-flow":
                // GlassModal.module.css .helpText wrapped at 160px with a
                // body-text block sibling BELOW (block flow, margin 0):
                // the wrap block's layout height positions the sibling.
                // Both blocks share the container's left edge, so the
                // sibling re-anchors to the stage origin the same way
                // both divs inherit the stage padding.
                ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, Vector2.Zero);
                Ui.Text(
                    "Poser keeps the incoming state and restores it exactly.",
                    new TextStyle
                    {
                        Size = Ui.ActiveTheme.Typography.CaptionSize,
                        Color = Ui.ActiveTheme.TextMuted,
                    },
                    TextConstraint.Wrap(160f * ImGuiHelpers.GlobalScale, 1.4f));
                ImGui.SetCursorScreenPos(new Vector2(
                    origin.X, ImGui.GetCursorScreenPos().Y));
                Ui.Text("After");
                ImGui.PopStyleVar();
                break;
            case "text-ws-collapse":
                // GlassModal.module.css .helpText (white-space: normal) —
                // repeated spaces AND the newline all collapse to single
                // spaces before wrapping inside 220px.
                Ui.Text(
                    "Collapse   runs of   spacing\nacross   breaks in the   source text.",
                    new TextStyle
                    {
                        Size = Ui.ActiveTheme.Typography.CaptionSize,
                        Color = Ui.ActiveTheme.TextMuted,
                    },
                    TextConstraint.Wrap(220f * ImGuiHelpers.GlobalScale, 1.4f));
                break;
            case "text-ws-prewrap":
                // InspectorField.module.css .popover .popoverText
                // (white-space: pre-wrap) — the doubled and tripled
                // spaces stay visible and the newline breaks.
                Ui.Text(
                    "Keep  two   spaces\nand the break",
                    default,
                    TextConstraint.Wrap(
                        200f * ImGuiHelpers.GlobalScale, 1.5f,
                        TextWhitespace.PreWrap));
                break;
            case "text-ws-tab":
                // InspectorField.module.css .popover .popoverText — tabs
                // preserved by pre-wrap advance to 8-space-width stops
                // (CSS default tab-size: 8).
                Ui.Text(
                    "a\tbc\tdef\ttab stops",
                    default,
                    TextConstraint.Wrap(
                        260f * ImGuiHelpers.GlobalScale, 1.5f,
                        TextWhitespace.PreWrap));
                break;
            case "text-ws-crlf":
                // text-wrapped-newline's exact content with CRLF line
                // separators: presentation normalization must make this
                // capture pixel-identical to the LF twin, as the HTML
                // parser normalizes CRLF before layout on the reference.
                Ui.Text(
                    "First line\r\nSecond block that wraps onward, and onward.",
                    default,
                    TextConstraint.Wrap(
                        200f * ImGuiHelpers.GlobalScale, 1.5f,
                        TextWhitespace.PreWrap));
                break;
            case "text-align-end":
            {
                // src/shared/ui/PropertyRow/PropertyRow.module.css —
                // .row (20px, nowrap) with .label (11px secondary,
                // min-width 64) and .value (flex:1, 11px primary at
                // opacity .8, text-align: right); second row uses
                // .valueMono. End alignment through the canonical
                // constrained renderer pins each value to the row's end
                // edge. Picto has no end-aligned ELLIPSIS grammar, so
                // the truncating-End case has no reference here.
                float s2 = ImGuiHelpers.GlobalScale;
                float rowWidth = 200f * s2;
                float labelWidth = 64f * s2;
                float rowHeight = 20f * s2;
                var labelStyle = new TextStyle
                {
                    Size = Ui.ActiveTheme.Typography.CaptionSize,
                    Color = Ui.ActiveTheme.TextDim,
                };
                var rows = new (string Label, string Value, TextStyle Style)[]
                {
                    ("Opacity", "1.000", new TextStyle
                    {
                        Size = Ui.ActiveTheme.Typography.CaptionSize,
                        Color = Ui.ActiveTheme.Text with { W = 0.8f },
                    }),
                    ("Scale", "0.750", new TextStyle
                    {
                        Size = Ui.ActiveTheme.Typography.CaptionSize,
                        Family = FontFamily.Mono,
                        Color = Ui.ActiveTheme.Text with { W = 0.8f },
                    }),
                };
                for (int i = 0; i < rows.Length; i++)
                {
                    float rowTop = origin.Y + i * rowHeight;
                    float lineHeight = Ui.MeasureText(
                        rows[i].Label, labelStyle).Y;
                    float textY = rowTop + (rowHeight - lineHeight) * 0.5f;
                    Ui.TextAt(
                        new Vector2(origin.X, textY),
                        rows[i].Label, labelStyle);
                    Ui.TextAt(
                        new Vector2(origin.X + labelWidth, textY),
                        rows[i].Value, rows[i].Style,
                        TextConstraint.Truncate(
                            rowWidth - labelWidth, TextAlign.End));
                }
                break;
            }
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
                {
                    // FloatingMenu is a retained static surface and Open
                    // TOGGLES an already-open menu closed — a prior batch
                    // entry leaving it open would capture a closing menu.
                    // Dismiss first so every entry opens fresh, exactly
                    // like an isolated capture.
                    Ui.FloatingMenu.DismissAll();
                    Ui.FloatingMenu.Open(
                        "##context-menu",
                        origin,
                        MenuItems);
                }
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
