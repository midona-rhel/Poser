using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;

namespace Poser.UI;

public static partial class Crystarium
{
    public sealed class SearchPicker<T> where T : class
    {
        private readonly string _popupId;
        private bool _openRequested;
        private Vector2 _anchorMin;
        private Vector2 _anchorMax;
        private string _owner = string.Empty;
        private string _caption = string.Empty;
        private string _search = string.Empty;
        private IReadOnlyList<T> _items = Array.Empty<T>();
        private Func<T, string> _label = static _ => string.Empty;
        private string? _loadError;

        public SearchPicker(string id) => _popupId = $"##search-picker-{id}";

        public string? Owner { get; private set; }

        public void Open(
            string owner,
            string caption,
            IReadOnlyList<T> items,
            Func<T, string> label,
            string? loadError = null)
        {
            _anchorMin = ImGui.GetItemRectMin();
            _anchorMax = ImGui.GetItemRectMax();
            _owner = owner;
            _caption = caption;
            _search = string.Empty;
            _items = items;
            _label = label;
            _loadError = loadError;
            _openRequested = true;
        }

        public (string Owner, T Item)? Draw()
        {
            if (_openRequested)
            {
                _openRequested = false;
                ImGui.OpenPopup(_popupId);
            }
            if (!ImGui.IsPopupOpen(_popupId))
            {
                Owner = null;
                return null;
            }
            Owner = _owner;

            var filtered = Filter();
            T? picked = null;
            Popover(
                _popupId,
                new PopoverProps
                {
                    Width = Theme.Metrics.Picker.Width,
                    Height = HeightFor(filtered.Count),
                    AnchorMin = _anchorMin,
                    AnchorMax = _anchorMax,
                    Padding = Theme.Metrics.Floating.PopoverPadding,
                },
                () => picked = DrawBody(filtered));
            return picked != null ? (_owner, picked) : null;
        }

        private List<T> Filter()
        {
            if (_search.Length == 0)
                return new List<T>(_items);
            var result = new List<T>();
            foreach (var item in _items)
            {
                if (_label(item).Contains(
                        _search,
                        StringComparison.OrdinalIgnoreCase))
                    result.Add(item);
            }
            return result;
        }

        private static float HeightFor(int resultCount)
        {
            int rows = Math.Clamp(
                resultCount,
                Theme.Metrics.Picker.MinimumRows,
                Theme.Metrics.Picker.MaximumRows);
            return Theme.Metrics.Floating.PopoverPadding * 2f
                + Theme.Metrics.Control.ListRow
                + Theme.Metrics.Space.Two
                + Theme.Metrics.Control.Workspace
                + Theme.Metrics.Space.Two
                + rows * Theme.Metrics.Control.ListRow;
        }

        private T? DrawBody(List<T> filtered)
        {
            float scale = ImGuiHelpers.GlobalScale;
            float inner = Theme.Metrics.Picker.Width
                - Theme.Metrics.Floating.PopoverPadding * 2f;
            var origin = ImGui.GetCursorScreenPos();
            DrawTextCentered(
                origin,
                new Vector2(
                    inner * scale,
                    Theme.Metrics.Control.ListRow * scale),
                Theme.Metrics.Typography.Caption,
                FontWeight.Medium,
                FormLabelColor,
                _caption);

            float searchY = origin.Y
                + (Theme.Metrics.Control.ListRow
                    + Theme.Metrics.Space.Two) * scale;
            ImGui.SetCursorScreenPos(new Vector2(origin.X, searchY));
            FilterPill(
                $"{_popupId}-filter",
                ref _search,
                "Search by name",
                inner);

            float listY = searchY
                + (Theme.Metrics.Control.Workspace
                    + Theme.Metrics.Space.Two) * scale;
            float listHeight = MathF.Max(
                Theme.Metrics.Picker.MinimumRows
                    * Theme.Metrics.Control.ListRow,
                ImGui.GetWindowSize().Y / scale
                    - Theme.Metrics.Floating.PopoverPadding
                    - (listY - origin.Y) / scale);
            ImGui.SetCursorScreenPos(new Vector2(origin.X, listY));

            T? picked = null;
            ScrollRegion(
                $"{_popupId}-list",
                inner,
                listHeight,
                region =>
                {
                    if (_loadError is { } error)
                    {
                        region.Empty(error);
                        return;
                    }
                    if (filtered.Count == 0)
                    {
                        region.Empty("No matches.");
                        return;
                    }
                    foreach (var item in filtered)
                    {
                        if (region.ListRow(
                                $"{_popupId}-{_label(item)}",
                                _label(item)))
                            picked = item;
                    }
                });

            if (picked != null)
                ImGui.CloseCurrentPopup();
            return picked;
        }
    }
}
