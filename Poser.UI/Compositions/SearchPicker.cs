using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;

namespace Poser.UI;

/// <summary>A segmented control shown under the search field.</summary>
public readonly record struct PickerStrip(
    string[] Labels, int Selected, Action<int> OnChange);

/// <summary>Optional picker behavior and row presentation.</summary>
public record struct PickerOptions<T> where T : class
{
    /// <summary>Returns the visible items for a query.</summary>
    public Func<string, IReadOnlyList<T>>? Query;

    /// <summary>Returns a row texture. A glyph is used when this returns zero.</summary>
    public Func<T, nint>? Texture;

    public Func<T, TablerIcon?>? Glyph;

    /// <summary>Right-aligned mono readout.</summary>
    public Func<T, string?>? Badge;

    /// <summary>Optional hierarchy hooks. Expandable rows get a disclosure
    /// affordance instead of a selection checkbox.</summary>
    public Func<T, bool>? IsExpandable;
    public Func<T, bool>? IsExpanded;
    public Action<T>? OnExpand;
    public Func<T, int>? Depth;

    public PickerStrip? Strip;
    public PickerStrip? SecondStrip;

    /// <summary>Logical panel width; 0 takes the theme's picker width.</summary>
    public float Width;
}

public static partial class Crystarium
{
    /// <summary>Caption band height.</summary>
    private const float PickerHeaderHeight = 40f;

    /// <summary>Search row height.</summary>
    private const float PickerSearchHeight = 36f;

    /// <summary>Selectable row height.</summary>
    private const float PickerRowHeight = 28f;

    /// <summary>Vertical gap above and below each row.</summary>
    private const float PickerPillVGap = 2f;

    private const float PickerRowPitch = PickerRowHeight + PickerPillVGap * 2f;

    /// <summary>Shared checkbox and selection-mark slot.</summary>
    private const float PickerCheckSlot = 14f;

    /// <summary>Centers the selection slot within a row.</summary>
    private const float PickerRowPadding =
        (PickerRowHeight - PickerCheckSlot) * 0.5f;

    /// <summary>Scrollbar gutter share reserved beside rows.</summary>
    private const float PickerBarShare = 0.5f;

    /// <summary>Vertical list padding.</summary>
    private const float PickerListVPad = 4f;

    /// <summary>Right padding for the search clear button.</summary>
    private const float PickerSearchClearPad = 6f;

    /// <summary>Search field inner padding.</summary>
    private const float PickerSearchInnerPad = 10f;

    /// <summary>Selection-mark size and stroke.</summary>
    private const float PickerCheckGlyph = 10f;

    private const float PickerCheckStroke = 3f;

    /// <summary>
    /// Shared picker implementation for single- and multi-select surfaces.
    /// The picker owns only popup and query state; callers own selections.
    /// </summary>
    public sealed class SearchPicker<T> where T : class
    {
        private readonly string _popupId;
        private readonly string _filterId;
        private readonly string _listId;

        // Retained callbacks avoid per-frame allocations.
        private readonly Action _body;
        private readonly Action<ScrollRegionScope> _list;
        private readonly Action<string> _onQuery;

        // Used by the built-in label filter.
        private readonly List<T> _filtered = new();

        private bool _openRequested;
        private Vector2 _anchorMin;
        private Vector2 _anchorMax;
        private string _owner = string.Empty;
        private string _query = string.Empty;

        private string? _caption;
        private IReadOnlyList<T> _items = Array.Empty<T>();
        private Func<T, string> _label = static _ => string.Empty;
        private Func<T, string> _key = static _ => string.Empty;
        private string? _selectedKey;
        private IReadOnlySet<string>? _selectedKeys;
        private Action<T, bool>? _onToggle;
        private string? _loadError;
        private PickerOptions<T> _options;

        // Draw callbacks share this per-frame state.
        private IReadOnlyList<T> _visible = Array.Empty<T>();
        private T? _picked;
        private float _panelWidth;
        private float _listHeight;

        public SearchPicker(string id)
        {
            _popupId = $"##search-picker-{id}";
            _filterId = $"{_popupId}-filter";
            _listId = $"{_popupId}-list";
            _body = DrawBody;
            _list = DrawRows;
            _onQuery = next => _query = next;
        }

        /// <summary>Owner of the open surface, or null while closed.</summary>
        public string? Owner { get; private set; }

        public bool IsOpen => ImGui.IsPopupOpen(_popupId);

        /// <summary>
        /// A row picks its item and closes.
        /// </summary>
        public void Open(
            string owner,
            IReadOnlyList<T> items,
            Func<T, string> label,
            Func<T, string>? key = null,
            string? selectedKey = null,
            string? loadError = null,
            in PickerOptions<T> options = default)
        {
            Arm(owner, null, items, label, key, loadError, in options);
            _selectedKey = selectedKey;
            _selectedKeys = null;
            _onToggle = null;
        }

        /// <summary>
        /// A row toggles its checkbox and the surface stays open.
        /// </summary>
        public void OpenMulti(
            string owner,
            string? caption,
            IReadOnlyList<T> items,
            Func<T, string> label,
            Func<T, string> key,
            IReadOnlySet<string> selectedKeys,
            Action<T, bool> onToggle,
            string? loadError = null,
            in PickerOptions<T> options = default)
        {
            ArgumentNullException.ThrowIfNull(selectedKeys);
            ArgumentNullException.ThrowIfNull(onToggle);
            Arm(owner, caption, items, label, key, loadError, in options);
            _selectedKey = null;
            _selectedKeys = selectedKeys;
            _onToggle = onToggle;
        }

        /// <summary>Updates options for an open surface.</summary>
        public void Update(in PickerOptions<T> options)
        {
            if (ImGui.IsPopupOpen(_popupId))
                _options = options;
        }

        /// <summary>Replaces the structural rows of an open picker while a
        /// disclosure gesture is being handled. Selection remains owned by
        /// the caller, so refreshing the hierarchy cannot change it.</summary>
        public void UpdateItems(IReadOnlyList<T> items)
        {
            _items = items;
        }

        /// <summary>Restates the caller-owned leaf selection while the popup
        /// remains open, dropping checks for bones removed by reconciliation.</summary>
        public void UpdateSelection(IReadOnlySet<string> selectedKeys)
        {
            if (_selectedKeys is not null)
                _selectedKeys = selectedKeys;
        }

        /// <summary>Updates the visible loading or failure status.</summary>
        public void SetLoadStatus(string? loadError)
        {
            if (ImGui.IsPopupOpen(_popupId))
                _loadError = loadError;
        }

        /// <summary>Draws the surface and returns a single-select result.</summary>
        public (string Owner, T Item)? Draw()
        {
            if (_openRequested)
            {
                _openRequested = false;
                OpenPopover(_popupId);
            }
            if (!ImGui.IsPopupOpen(_popupId))
            {
                Owner = null;
                return null;
            }
            Owner = _owner;

            var theme = ActiveTheme;
            _panelWidth = _options.Width > 0f ? _options.Width : theme.Picker.Width;
            // A caller query supplies the visible list.
            _visible = _options.Query is { } query ? query(_query) : Filter();

            int rows = Math.Clamp(
                _visible.Count,
                theme.Picker.MinimumRows,
                theme.Picker.MaximumRows);
            // Height includes chrome, list padding, and visible row bodies.
            _listHeight = PickerListVPad * 2f + rows * PickerRowHeight;
            float panelHeight =
                (_caption is null ? 0f : PickerHeaderHeight)
                + StripCount() * StripHeight()
                + PickerSearchHeight
                + _listHeight;

            _picked = null;
            // The picker paints its own opaque panel.
            FloatingSurface.Popup(
                _popupId,
                new FloatingSurfaceProps
                {
                    Width = _panelWidth,
                    Height = panelHeight,
                    Padding = 0f,
                    AnchorMin = _anchorMin,
                    AnchorMax = _anchorMax,
                    Treatment = FloatingSurfaceTreatment.Unframed,
                },
                _body);
            _visible = Array.Empty<T>();
            return _picked is { } item ? (_owner, item) : null;
        }

        private void Arm(
            string owner,
            string? caption,
            IReadOnlyList<T> items,
            Func<T, string> label,
            Func<T, string>? key,
            string? loadError,
            in PickerOptions<T> options)
        {
            _anchorMin = ImGui.GetItemRectMin();
            _anchorMax = ImGui.GetItemRectMax();
            _owner = owner;
            _caption = caption;
            _items = items;
            _label = label;
            if (key != null)
                _key = key;
            _loadError = loadError;
            _options = options;
            // Each open starts with an empty query. Callers retain strip state.
            _query = string.Empty;
            _openRequested = true;
        }

        private int StripCount() =>
            (_options.Strip is null ? 0 : 1)
            + (_options.SecondStrip is null ? 0 : 1);

        /// <summary>Height of a strip with vertical padding.</summary>
        private static float StripHeight() =>
            ActiveTheme.Controls.NavigationHeight + PickerListVPad * 2f;

        private IReadOnlyList<T> Filter()
        {
            _filtered.Clear();
            for (int i = 0; i < _items.Count; i++)
            {
                T item = _items[i];
                if (_query.Length == 0
                    || _label(item).Contains(
                        _query, StringComparison.OrdinalIgnoreCase))
                    _filtered.Add(item);
            }
            return _filtered;
        }

        private void DrawBody()
        {
            float scale = ImGuiHelpers.GlobalScale;
            var theme = ActiveTheme;
            var draw = ImGui.GetWindowDrawList();
            var min = ImGui.GetWindowPos();
            PaintPanel(draw, min, min + ImGui.GetWindowSize(), theme);

            var origin = ImGui.GetCursorScreenPos();
            // The fixed gutter keeps content aligned when the scrollbar appears.
            float inset = theme.Scrollbar.GutterWidth;
            float pillInset = inset * PickerBarShare;
            float rowWidth = MathF.Max(0f, _panelWidth - pillInset * 2f);
            float y = 0f;

            if (_caption is { } caption)
            {
                var band = new Vector2(_panelWidth, PickerHeaderHeight) * scale;
                PaintRule(draw, origin, band, scale, theme);
                LabelInBand(
                    origin + new Vector2(inset * scale, 0f),
                    new Vector2((_panelWidth - inset * 2f) * scale, band.Y),
                    caption,
                    new TextStyle
                    {
                        Size = theme.Typography.LabelSize,
                        Color = theme.TextMuted,
                    });
                y += PickerHeaderHeight;
            }

            // Align the search glyph and text with row marks and labels.
            float searchMargin = MathF.Max(
                0f, pillInset + PickerRowPadding - PickerSearchInnerPad);
            var searchOrigin = origin + new Vector2(0f, y * scale);
            PaintRule(
                draw,
                searchOrigin,
                new Vector2(_panelWidth, PickerSearchHeight) * scale,
                scale,
                theme);
            ImGui.SetCursorScreenPos(
                searchOrigin + new Vector2(searchMargin * scale, 0f));
            FilterPill(
                _filterId,
                _query,
                _onQuery,
                "Search by name",
                new ControlStyle
                {
                    Width = UiWidth.Region(
                        _panelWidth - searchMargin - inset - PickerSearchClearPad),
                });
            y += PickerSearchHeight;

            // Strips share the row inset below the search field.
            y = DrawStrip(_options.Strip, 0, origin, y, pillInset, rowWidth, scale);
            y = DrawStrip(
                _options.SecondStrip, 1, origin, y, pillInset, rowWidth, scale);

            ImGui.SetCursorScreenPos(origin + new Vector2(0f, y * scale));
            // The bar and its padding occupy the left gutter.
            ScrollRegion(_listId, _panelWidth, _listHeight, _list, pillInset);

            if (_picked != null)
                ImGui.CloseCurrentPopup();
        }

        private static float DrawStrip(
            PickerStrip? strip, int ordinal, Vector2 origin, float y,
            float pillInset, float rowWidth, float scale)
        {
            if (strip is not { } band)
                return y;
            ImGui.SetCursorScreenPos(origin + new Vector2(
                pillInset * scale, (y + PickerListVPad) * scale));
            SegmentedControl(
                $"##picker-strip-{ordinal}",
                band.Labels,
                band.Selected,
                band.OnChange,
                new ControlStyle
                {
                    Width = UiWidth.Region(rowWidth),
                    Height = UiHeight.Fixed(ActiveTheme.Controls.NavigationHeight),
                });
            return y + StripHeight();
        }

        private void DrawRows(ScrollRegionScope region)
        {
            _ = region;
            float scale = ImGuiHelpers.GlobalScale;
            // Rows include their own vertical spacing.
            var spacing = ImGui.GetStyle().ItemSpacing;
            ImGui.PushStyleVar(
                ImGuiStyleVar.ItemSpacing, new Vector2(spacing.X, 0f));
            try
            {
                float pad = PickerListVPad * scale;
                ImGui.Dummy(new Vector2(0f, pad));
                if (_loadError is { } error)
                    EmptyLine(error);
                else if (_visible.Count == 0)
                    EmptyLine("No matches.");
                else
                    ClippedRows(scale);
                // The trailing dummy keeps bottom padding in the scroll extent.
                ImGui.Dummy(new Vector2(0f, pad));
            }
            finally
            {
                ImGui.PopStyleVar();
            }
        }

        /// <summary>Draws only rows visible in the current clip range.</summary>
        private void ClippedRows(float scale)
        {
            var clipper = new ImGuiListClipper();
            clipper.Begin(_visible.Count, PickerRowPitch * scale);
            while (clipper.Step())
            {
                for (int i = clipper.DisplayStart; i < clipper.DisplayEnd; i++)
                    Row(_visible[i], scale);
            }
            clipper.End();
        }

        private void Row(T item, float scale)
        {
            var theme = ActiveTheme;
            var draw = ImGui.GetWindowDrawList();
            var bandMin = ImGui.GetCursorScreenPos();
            float pillInset = theme.Scrollbar.GutterWidth * PickerBarShare;
            var pillMin = new Vector2(
                bandMin.X + pillInset * scale,
                bandMin.Y + PickerPillVGap * scale);
            var pillSize = new Vector2(
                MathF.Max(0f, _panelWidth - pillInset * 2f),
                PickerRowHeight) * scale;

            string key = _key(item);
            bool multi = _onToggle is not null;
            bool active = multi
                ? _selectedKeys is { } keys && keys.Contains(key)
                : string.Equals(key, _selectedKey, StringComparison.Ordinal);

            ImGui.SetCursorScreenPos(pillMin);
            var hit = Interactive.Reserve(
                $"{_popupId}-{key}", pillSize, disabled: false);
            ImGui.SetCursorScreenPos(
                new Vector2(bandMin.X, bandMin.Y + PickerRowPitch * scale));

            // Hover and press use the same overlay above selection.
            var fill = hit.Hovered || hit.Active
                ? theme.Chrome.WeakOverlay
                : active ? theme.Chrome.ActiveOverlay : Vector4.Zero;
            if (fill.W > 0f)
                BoxRenderer.Draw(draw, pillMin, pillMin + pillSize, new BoxStyle
                {
                    BackgroundColor = fill,
                    BorderRadius = theme.Radii.Control,
                });

            float gap = theme.Spacing.Three * scale;
            float x = pillMin.X + PickerRowPadding * scale;
            float centerY = pillMin.Y + pillSize.Y * 0.5f;

            // Both modes reserve the same mark slot to align labels.
            float slot = PickerCheckSlot * scale;
            var slotMin = new Vector2(x, centerY - slot * 0.5f);
            bool expandable = _options.IsExpandable?.Invoke(item) == true;
            bool disclosureClicked = false;
            if (expandable)
            {
                ImGui.SetItemAllowOverlap();
                ImGui.SetCursorScreenPos(new Vector2(
                    slotMin.X, pillMin.Y));
                var disclosure = Interactive.Reserve(
                    $"{_popupId}-{key}-disclosure",
                    new Vector2(slot, pillSize.Y), disabled: false);
                if (disclosure.Clicked)
                {
                    disclosureClicked = true;
                    _options.OnExpand?.Invoke(item);
                }
                IconIn(
                    slotMin,
                    slotMin + new Vector2(slot),
                    _options.IsExpanded?.Invoke(item) == true
                        ? TablerIcon.ChevronDown
                        : TablerIcon.ChevronRight,
                    theme.TextMuted);
            }
            else if (multi)
                PaintCheckBox(
                    draw, slotMin, slotMin + new Vector2(slot), active, theme);
            if (!expandable && active)
            {
                float tick = PickerCheckGlyph * scale;
                var tickMin = new Vector2(
                    slotMin.X + (slot - tick) * 0.5f, centerY - tick * 0.5f);
                IconIn(
                    tickMin,
                    tickMin + new Vector2(tick),
                    TablerIcon.Check,
                    multi ? theme.Chrome.Checkmark : theme.Text,
                    strokeWidth: PickerCheckStroke);
            }
            x += slot + gap;
            if (_options.Depth is { } depth)
                x += Math.Max(0, depth(item)) * 16f * scale;

            // A glyph is the fallback when no texture is available.
            nint texture = _options.Texture is { } toTexture ? toTexture(item) : 0;
            TablerIcon? glyph = _options.Glyph is { } toGlyph ? toGlyph(item) : null;
            if (texture != 0 || glyph is not null)
            {
                float side = theme.Controls.IconSize * scale;
                var markMin = theme.Optical.Snap(
                    new Vector2(x, centerY - side * 0.5f));
                if (texture != 0)
                    draw.AddImage(
                        new ImTextureID(texture),
                        markMin,
                        markMin + new Vector2(side),
                        Vector2.Zero,
                        Vector2.One,
                        ImGui.ColorConvertFloat4ToU32(
                            ColorEx.ApplyAlpha(Vector4.One)));
                else
                    IconIn(
                        markMin, markMin + new Vector2(side), glyph!.Value,
                        theme.Text);
                x += side + gap;
            }

            float contentRight = pillMin.X + pillSize.X - PickerRowPadding * scale;
            float labelRight = contentRight;
            string? badge = _options.Badge is { } toBadge ? toBadge(item) : null;
            if (!string.IsNullOrEmpty(badge))
            {
                var badgeStyle = new TextStyle
                {
                    Size = theme.Typography.CaptionSize,
                    Family = FontFamily.Mono,
                    Color = theme.FormLabel,
                };
                float width = MeasureText(badge!, badgeStyle).X;
                labelRight = contentRight - width - gap;
                TextInBand(
                    new Vector2(contentRight - width, pillMin.Y),
                    new Vector2(width, pillSize.Y),
                    badge!,
                    badgeStyle,
                    TextAlign.Start,
                    besideIcon: true);
            }

            if (labelRight > x)
                LabelInBand(
                    new Vector2(x, pillMin.Y),
                    new Vector2(labelRight - x, pillSize.Y),
                    _label(item),
                    new TextStyle
                    {
                        Size = theme.Typography.BodySize,
                        Color = theme.Text,
                    },
                    besideIcon: true);

            // Single-select closes; multi-select remains open.
            if (!hit.Clicked || disclosureClicked)
                return;
            if (_onToggle is { } toggle)
                toggle(item, !active);
            else
                _picked = item;
        }

        /// <summary>Draws an empty-state caption aligned with row labels.</summary>
        private void EmptyLine(string text)
        {
            float scale = ImGuiHelpers.GlobalScale;
            var theme = ActiveTheme;
            var bandMin = ImGui.GetCursorScreenPos();
            float pillInset = theme.Scrollbar.GutterWidth * PickerBarShare;
            float left = pillInset + PickerRowPadding + PickerCheckSlot
                + theme.Spacing.Three;
            float right = MathF.Max(
                0f, _panelWidth - pillInset - PickerRowPadding);
            LabelInBand(
                new Vector2(
                    bandMin.X + left * scale, bandMin.Y + PickerPillVGap * scale),
                new Vector2(
                    MathF.Max(0f, right - left) * scale, PickerRowHeight * scale),
                text,
                new TextStyle
                {
                    Size = theme.Typography.CaptionSize,
                    Color = FormHintColor,
                });
            ImGui.Dummy(new Vector2(0f, PickerRowPitch * scale));
        }
    }

    /// <summary>Paints the picker panel and its border.</summary>
    private static void PaintPanel(
        ImDrawListPtr draw, Vector2 min, Vector2 max, Theme theme)
    {
        // Panel shadows extend beyond the popup clip.
        draw.PushClipRect(Vector2.Zero, ImGui.GetIO().DisplaySize, false);
        try
        {
            BoxRenderer.Draw(draw, min, max, new BoxStyle
            {
                BackgroundColor = ColorEx.FlattenOver(
                    FloatingSurface.FillColor, theme.Surface),
                BorderWidth = 1f,
                BorderRadius = theme.Radii.Surface,
                BorderTopColor = theme.Border,
                BorderRightColor = theme.Border,
                BorderBottomColor = theme.Border,
                BorderLeftColor = theme.Border,
                BoxShadows = [theme.Shadows.Panel, theme.Shadows.PanelRing],
            });
        }
        finally
        {
            draw.PopClipRect();
        }
    }

    /// <summary>Paints an inset bottom divider without changing layout.</summary>
    private static void PaintRule(
        ImDrawListPtr draw, Vector2 min, Vector2 band, float scale, Theme theme)
    {
        var max = min + band;
        draw.AddRectFilled(
            new Vector2(min.X, max.Y - scale),
            max,
            ImGui.ColorConvertFloat4ToU32(ColorEx.ApplyAlpha(theme.Border)));
    }

    /// <summary>Paints the shared checked and unchecked mark box.</summary>
    private static void PaintCheckBox(
        ImDrawListPtr draw, Vector2 min, Vector2 max, bool @checked, Theme theme)
    {
        // Derive the pressed overlay from the available chrome colors.
        Vector4? outline = @checked
            ? null
            : theme.Chrome.ActiveOverlay with { W = 0.20f };
        BoxRenderer.Draw(draw, min, max, new BoxStyle
        {
            BackgroundColor =
                @checked ? theme.Chrome.Primary : theme.Chrome.InputWell,
            BorderRadius = theme.Radii.Medium,
            BorderWidth = outline is null ? 0f : 1f,
            BorderTopColor = outline,
            BorderRightColor = outline,
            BorderBottomColor = outline,
            BorderLeftColor = outline,
        });
    }

    /// <summary>Centers a label and clips it only when it overflows.</summary>
    private static void LabelInBand(
        Vector2 min, Vector2 band, string text, in TextStyle style,
        bool besideIcon = false)
    {
        if (!(band.X > 0f))
            return;
        if (MeasureText(text, style).X <= band.X)
            TextInBand(min, band, text, style, TextAlign.Start, besideIcon);
        else
            TextInBand(
                min, band, text, style, TextConstraint.Truncate(band.X),
                TextAlign.Start, besideIcon);
    }
}
