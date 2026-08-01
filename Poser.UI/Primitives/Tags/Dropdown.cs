using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;

namespace Poser.UI;

public static partial class LegacyCrystarium
{
    /// <summary>
    /// <c>.drop max-height: calc(7 * 26px + 6 * 2px + 12px)</c> — the
    /// dropdown scrolls past seven rows. A CSS literal, not a token: the
    /// shared Picker.MaximumRows belongs to the search pickers.
    /// </summary>
    private const int DropVisibleRows = 7;

    /// <summary>CmSelect.tsx places the portal at
    /// <c>top: cssRect.bottom + 4</c>.</summary>
    private const float DropAnchorGap = 4f;

    /// <summary><c>.btnChevron { width: 20px }</c> — the fixed slot the
    /// 14px IconSelector centers in.</summary>
    private const float ChevronSlot = 20f;

    /// <summary><c>.btnChevron { opacity: .5 }</c>.</summary>
    private const float ChevronOpacity = 0.5f;

    /// <summary>Tabler <c>IconSelector</c>, the glyph CmSelect.tsx
    /// renders in <c>.btnChevron</c>.</summary>
    private const string ChevronIcon = "selector";

    /// <summary>
    /// The OPAQUE fill <c>.drop</c> wears — the color the trigger already
    /// SHOWS at rest, not the token it is painted with.
    ///
    /// <para><c>.btn</c> and <c>.drop</c> share
    /// <c>--color-subtle-overlay</c>, which is white at 10%: a TRANSLUCENT
    /// token whose result belongs to whatever sits behind it. Behind the
    /// trigger is the window surface; behind the popup is the popup's own
    /// <c>--shadow-panel</c>, whose solid core <see cref="BoxRenderer"/>
    /// lays down before the fill. The one token therefore reads visibly
    /// darker on the menu than on the closed control the menu is supposed
    /// to continue. Flattening it over <see cref="Theme.Surface"/> yields
    /// that resting appearance as an opaque color, and the opacity is the
    /// point: shadow, blur, or whatever the menu overhangs can no longer
    /// tint it.</para>
    ///
    /// <para>The BORDER token is deliberately NOT flattened.
    /// <see cref="BoxRenderer"/> strokes the border over the fill, so
    /// <c>--color-border-secondary</c> over this opaque fill composites to
    /// the trigger's border pixel by construction — flattening it first
    /// would compute the identical number a second time and hide where the
    /// match comes from.</para>
    /// </summary>
    private static Vector4 PopupFill(in Theme theme) =>
        ColorEx.FlattenOver(theme.Chrome.ControlHover, theme.Surface);

    public static bool Dropdown(
        string id, string[] items, int selected, Action<int> onChange,
        ControlStyle style = default, bool disabled = false, string? help = null) =>
        DropdownCore(id, items, selected, onChange, style, disabled, help, null, false);

    public static bool ActionDropdown(
        string id, string[] items, int selected, string previewText, Action<int> onChange,
        ControlStyle style = default, bool disabled = false, string? help = null) =>
        DropdownCore(id, items, selected, onChange, style, disabled, help, previewText, true);

    /// <summary>
    /// Picto's <c>shared/ui/CmSelect</c>. The trigger is <c>.btn</c>
    /// (26px, <c>padding: 0 6px 0 12px</c>, <c>gap: 6px</c>, radius 6,
    /// <c>--color-subtle-overlay</c> over a 1px
    /// <c>--color-border-secondary</c>, 12px <c>--color-text-primary</c>,
    /// then the 20px <c>.btnChevron</c> slot); the portal is
    /// <c>.drop</c> (the same 1px border and radius, 4px padding, 2px
    /// row gap, <c>--shadow-panel</c>) over
    /// <c>.opt</c> rows (26px, <c>padding: 0 8px</c>, radius 4,
    /// <c>--color-hover-overlay</c> for both <c>:hover</c> and
    /// <c>.optActive</c>). The module declares no <c>:hover</c> and no
    /// <c>transition</c> on <c>.btn</c>, so the trigger has no hover
    /// paint and no motion channel.
    /// One product deviation: <c>.drop</c> takes the trigger's APPEARANCE
    /// instead of <c>--glass-bg</c>, so the open dropdown reads as one
    /// continuous control. Appearance, not token: see
    /// <see cref="PopupFill"/>.
    /// </summary>
    private static bool DropdownCore(
        string id, string[] items, int selected, Action<int> onChange,
        ControlStyle style, bool disabled, string? help,
        string? previewText, bool reselectFires)
    {
        if (items.Length == 0) return false;
        string popupId = $"{id}_popup";
        bool changed = false;
        float scale = ImGuiHelpers.GlobalScale;
        var theme = ActiveTheme;
        float radius = theme.Radii.Control;          // border-radius: 6px
        float borderPx = 1f * scale;                 // border: 1px solid
        float padLeft = theme.Spacing.Six * scale;   // padding-left: 12px
        float padRight = theme.Spacing.Three * scale;// padding-right: 6px
        float gap = theme.Spacing.Three * scale;     // gap: 6px
        float chevronSlot = ChevronSlot * scale;

        // .btn font: 12px --font-family at --color-text-primary.
        var labelStyle = new TextStyle { Size = theme.Typography.LabelSize };

        // CmSelect's base contract is intrinsic sizing: the invisible
        // .sizer spans force the label area to the WIDEST option, so
        // Content/Unspecified must never inherit the surrounding region.
        // Fixed and Fill may still override the resolved width.
        float widestLabel = 0f;
        foreach (string item in items)
            widestLabel = MathF.Max(widestLabel, MeasureText(item, labelStyle).X);
        if (!string.IsNullOrEmpty(previewText))
            widestLabel = MathF.Max(
                widestLabel, MeasureText(previewText!, labelStyle).X);
        // CSS border-box: both borders + both paddings + label + gap + slot.
        float intrinsicWidth =
            borderPx * 2f + padLeft + widestLabel + gap + chevronSlot + padRight;

        // The intrinsic span is only known after measuring the options, so
        // the shared preamble runs here rather than at the top.
        var metrics = ControlSizing.Resolve(
            style, intrinsicWidth / scale, theme.Controls.WorkspaceHeight);
        float height = metrics.Height;
        float totalWidth = metrics.Width;
        float minWidth =
            borderPx * 2f + padLeft + gap + chevronSlot + padRight + 20f * scale;
        if (totalWidth < minWidth) totalWidth = minWidth;

        var pos = ImGui.GetCursorScreenPos();
        var drawList = ImGui.GetWindowDrawList();

        ImGui.SetCursorScreenPos(pos);
        var trigger = Interactive.Reserve(
            $"{id}_value", new Vector2(totalWidth, height), disabled);
        bool valueHovered = trigger.Hovered;
        if (trigger.Clicked)
            OpenPopover(popupId);
        var valueMin = trigger.ScreenMin;
        var valueMax = trigger.ScreenMax;

        var triggerFill = theme.Chrome.ControlHover;  // --color-subtle-overlay
        var triggerBorder = theme.Border;             // --color-border-secondary
        var labelColor = theme.Text;                  // --color-text-primary
        float chevronOpacity = ChevronOpacity;
        if (disabled)
        {
            // CmSelect declares NO :disabled rule; this borrows the Picto
            // action-button family's `.btn:disabled { opacity: .35 }`
            // GROUP opacity — the SAME recipe Button uses, so it comes
            // from the one implementation rather than a second copy. The
            // trigger has two pieces of content inside that group and
            // both take their transform from the same return value: the
            // label compensates, the chevron scales its `.btnChevron`
            // opacity.
            var content = ControlPaint.DisabledGroup(
                drawList, valueMin, valueMax,
                radius * scale, borderPx, triggerFill, triggerBorder,
                theme.Chrome.ControlDisabledOpacity);
            labelColor = content.Label(labelColor);
            chevronOpacity = content.Glyph(ChevronOpacity);
        }
        else
        {
            BoxRenderer.Draw(drawList, valueMin, valueMax, new BoxStyle
            {
                BackgroundColor = triggerFill,
                BorderWidth = 1f,
                BorderRadius = radius,
                BorderTopColor = triggerBorder,
                BorderRightColor = triggerBorder,
                BorderBottomColor = triggerBorder,
                BorderLeftColor = triggerBorder,
            });
        }

        // CSS content box: the 1px border is inside the border box, so
        // padding measures from the border's INNER edge.
        float contentLeft = valueMin.X + borderPx + padLeft;
        float contentRight = valueMax.X - borderPx - padRight;
        float chevronLeft = contentRight - chevronSlot;
        float labelWidth = chevronLeft - gap - contentLeft;

        string currentText = previewText ??
            ((selected >= 0 && selected < items.Length) ? items[selected] : "");
        var triggerLabelStyle = labelStyle with { Color = labelColor };
        bool labelClipped = false;
        if (labelWidth > 0f && currentText.Length > 0)
        {
            var measured = MeasureText(currentText, triggerLabelStyle);
            labelClipped = measured.X > labelWidth;
            // `align-items: center` on the 24px content box. The
            // canonical text path snaps the origin itself, so the old
            // Optical.DropdownText nudge is gone — it pushed the run a
            // pixel BELOW the reference baseline.
            TextAt(
                new Vector2(
                    contentLeft,
                    valueMin.Y + (height - measured.Y) * 0.5f),
                currentText,
                triggerLabelStyle,
                TextConstraint.Truncate(labelWidth));
        }

        // Truncation-only preview: same chrome, no explanatory delay.
        if (labelClipped && valueHovered)
            HoverHelp.Preview($"{id}-full", valueMin, valueMax, currentText);

        // .btnChevron: <IconSelector size={14} /> centered in the 20px slot.
        float iconSpan = theme.Controls.SmallIconSize * scale;
        var iconMin = new Vector2(
            chevronLeft + (chevronSlot - iconSpan) * 0.5f,
            valueMin.Y + (height - iconSpan) * 0.5f);
        IconIn(
            iconMin,
            iconMin + new Vector2(iconSpan),
            ChevronIcon,
            opacity: chevronOpacity);

        ImGui.SetCursorScreenPos(pos + new Vector2(0, height));

        // ---- .drop ----------------------------------------------------
        int visibleItems = Math.Min(items.Length, DropVisibleRows);
        float rowHeight = height;                              // .opt height: 26px
        float rowGap = theme.Floating.DropdownRowGap * scale;  // gap: 2px
        float itemListHeight =
            visibleItems * rowHeight + Math.Max(0, visibleItems - 1) * rowGap;
        // .drop is content-box: its own 1px border plus 4px padding sit
        // outside the row list, so the popup's content inset is both.
        float dropInset = borderPx + theme.Floating.PopupPadding * scale;
        float popupHeight = itemListHeight + dropInset * 2f;
        int popupSelected = selected;
        bool popupChanged = false;
        FloatingSurface.Popup(
            popupId,
            new FloatingSurfaceProps
            {
                Width = totalWidth / scale,
                Height = popupHeight / scale,
                Padding = dropInset / scale,
                AnchorMin = valueMin,
                // The shared anchored placement adds Floating.AnchorGap;
                // CmSelect asks for 4px, so the anchor carries the rest.
                AnchorMax = valueMax + new Vector2(
                    0f, (DropAnchorGap - theme.Floating.AnchorGap) * scale),
                Treatment = FloatingSurfaceTreatment.Unframed,
            },
            () =>
            {
                var popupMin = ImGui.GetWindowPos();
                var popupMax = popupMin + ImGui.GetWindowSize();
                var popupDrawList = ImGui.GetWindowDrawList();
                // --shadow-panel must escape the popup's own clip.
                popupDrawList.PushClipRect(
                    Vector2.Zero, ImGui.GetIO().DisplaySize, false);
                BoxRenderer.Draw(popupDrawList, popupMin, popupMax, new BoxStyle
                {
                    // PRODUCT DEVIATION from `.drop { background: var(--glass-bg) }`:
                    // the popup wears the TRIGGER's own surface so the open
                    // control reads as one object. Opaque by construction —
                    // see PopupFill — and the same border token as `.btn`;
                    // the CSS glass recipe is not used.
                    BackgroundColor = PopupFill(theme),
                    BorderWidth = 1f,
                    BorderRadius = radius,
                    BorderTopColor = triggerBorder,
                    BorderRightColor = triggerBorder,
                    BorderBottomColor = triggerBorder,
                    BorderLeftColor = triggerBorder,
                    BoxShadows = [theme.Shadows.Panel, theme.Shadows.PanelRing],
                });
                popupDrawList.PopClipRect();

                float regionWidth = ImGui.GetContentRegionAvail().X / scale;
                ScrollRegion(
                    $"{popupId}-scroll",
                    regionWidth,
                    itemListHeight / scale,
                    region =>
                    {
                        float optPad = theme.Spacing.Four * scale; // padding: 0 8px
                        float optRadius = theme.Radii.Medium * scale;
                        // .opt:hover is --color-menu-hover and .optActive
                        // is --color-hover-overlay — the SAME token, and
                        // :hover outranks .optActive on specificity, so
                        // one fill covers both states.
                        uint rowFill = ImGui.ColorConvertFloat4ToU32(
                            ColorEx.ApplyAlpha(theme.Chrome.WeakOverlay));
                        var spacing = ImGui.GetStyle().ItemSpacing;
                        ImGui.PushStyleVar(
                            ImGuiStyleVar.ItemSpacing,
                            new Vector2(spacing.X, 0f));

                        for (int i = 0; i < items.Length; i++)
                        {
                            bool isSelected = i == popupSelected;
                            var itemPos = ImGui.GetCursorScreenPos();
                            bool scrolls = items.Length > visibleItems;
                            var hitSize = new Vector2(
                                (scrolls ? region.ContentWidth : regionWidth) * scale,
                                rowHeight);
                            var fillSize = new Vector2(regionWidth * scale, rowHeight);

                            ImGui.PushID(i);
                            var itemHit = Interactive.Reserve(
                                "##item", hitSize, disabled: false);
                            if (itemHit.Clicked)
                            {
                                if (popupSelected != i || reselectFires)
                                {
                                    popupSelected = i;
                                    popupChanged = true;
                                    onChange(i);
                                }
                                ImGui.CloseCurrentPopup();
                            }
                            bool itemHovered = itemHit.Hovered;

                            if (isSelected || itemHovered)
                                popupDrawList.AddRectFilled(
                                    itemPos,
                                    itemPos + fillSize,
                                    rowFill,
                                    optRadius);

                            float optWidth = hitSize.X - optPad * 2f;
                            bool optClipped = false;
                            if (optWidth > 0f && items[i].Length > 0)
                            {
                                var optSize = MeasureText(items[i], labelStyle);
                                optClipped = optSize.X > optWidth;
                                TextAt(
                                    new Vector2(
                                        itemPos.X + optPad,
                                        itemPos.Y + (rowHeight - optSize.Y) * 0.5f),
                                    items[i],
                                    labelStyle,
                                    TextConstraint.Truncate(optWidth));
                            }
                            if (optClipped && itemHovered)
                                HoverHelp.Preview(
                                    $"{id}-item-{i}",
                                    itemPos,
                                    itemPos + hitSize,
                                    items[i]);

                            ImGui.PopID();
                            if (i < items.Length - 1)
                                ImGui.SetCursorScreenPos(new Vector2(
                                    itemPos.X,
                                    itemPos.Y + rowHeight + rowGap));
                        }

                        ImGui.PopStyleVar();
                    });
            });

        if (popupChanged)
        {
            changed = true;
        }

        if (!string.IsNullOrEmpty(help) && valueHovered)
            HoverHelp.Explain(id, valueMin, valueMax, help!);

        return changed;
    }
}
