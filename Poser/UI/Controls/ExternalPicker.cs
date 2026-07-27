using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Poser.Domain.Integration;
using Poser.UI.Views;

namespace Poser.UI.Controls;

/// <summary>
/// The one anchored searchable picker for external entities (Penumbra
/// collections, Glamourer designs, Customize+ profiles). Lists load on
/// open through the caller's loader and are cached only for that open;
/// rows filter case-insensitively, show at most ten before scrolling, and
/// draw with the retained glass chrome, one-pixel separators, and optical
/// baselines. The popover never resizes under the pointer — it is sized to
/// the current result count when it opens or the filter changes.
/// </summary>
public sealed class ExternalPicker
{
    private const string PopupId = "##ext-picker";
    private const float Width = 300f;
    private const float RowHeight = 26f;
    private const int MaxListRows = 10;
    private const int MinListRows = 3;

    private bool _openRequested;
    private Vector2 _anchorMin;
    private Vector2 _anchorMax;
    private string _owner = string.Empty;
    private string _caption = string.Empty;
    private string _search = string.Empty;
    private IReadOnlyList<ExternalItem> _items = Array.Empty<ExternalItem>();
    private string? _loadError;

    /// <summary>The row currently owning the popover, or null when closed —
    /// callers use this to route the pick.</summary>
    public string? Owner { get; private set; }

    /// <summary>
    /// Opens the picker anchored under the item drawn immediately before
    /// this call. The loader runs once, now; a load failure shows inside
    /// the popover instead of pretending an empty list.
    /// </summary>
    public void Open(
        string owner,
        string caption,
        Func<IntegrationValue<IReadOnlyList<ExternalItem>>> load)
    {
        _anchorMin = ImGui.GetItemRectMin();
        _anchorMax = ImGui.GetItemRectMax();
        _owner = owner;
        _caption = caption;
        _search = string.Empty;
        var loaded = load();
        _items = loaded.Success && loaded.Value is { } items
            ? items
            : Array.Empty<ExternalItem>();
        _loadError = loaded.Success ? null : loaded.Detail;
        // Deferred: opening inside a scrolling child would parent the popup
        // to that child and close it the same frame.
        _openRequested = true;
    }

    /// <summary>Draws the popover when open; returns the pick, tagged with
    /// the owning row, exactly once.</summary>
    public (string Owner, ExternalItem Item)? Draw()
    {
        if (_openRequested)
        {
            _openRequested = false;
            ImGui.OpenPopup(PopupId);
        }
        if (!ImGui.IsPopupOpen(PopupId))
        {
            Owner = null;
            return null;
        }
        Owner = _owner;

        var filtered = Filter();
        ExternalItem? picked = null;
        var props = new PopoverProps
        {
            Width = Width,
            Height = HeightFor(filtered.Count),
            AnchorMin = _anchorMin,
            AnchorMax = _anchorMax,
        };
        Crystarium.Popover(PopupId, props, () => picked = DrawBody(filtered));
        return picked is { } item ? (_owner, item) : null;
    }

    private List<ExternalItem> Filter()
    {
        if (_search.Length == 0)
            return new List<ExternalItem>(_items);
        var result = new List<ExternalItem>();
        foreach (var item in _items)
            if (item.Name.Contains(_search, StringComparison.OrdinalIgnoreCase))
                result.Add(item);
        return result;
    }

    private static float HeightFor(int resultCount)
    {
        const float chrome = 18f + 32f + 16f;
        int rows = Math.Clamp(resultCount, MinListRows, MaxListRows);
        return chrome + rows * RowHeight;
    }

    private ExternalItem? DrawBody(List<ExternalItem> filtered)
    {
        float s = ImGuiHelpers.GlobalScale;
        float inner = Width - 16f;
        float rowWidth = inner - Views.AppShellView.ScrollbarWidth;
        var origin = ImGui.GetCursorScreenPos();
        var cursor = origin;

        ViewText.Label(cursor, _caption, 11f, FontWeight.Medium, InspectorLayout.LabelColor);
        cursor.Y += 18f * s;

        ImGui.SetCursorScreenPos(cursor);
        Crystarium.FilterPill("##ext-pick-search", ref _search, "Search by name", inner);
        cursor.Y += 32f * s;

        if (_loadError is { } error)
        {
            ImGui.SetCursorScreenPos(cursor + new Vector2(0f, 6f * s));
            ViewText.Label(cursor + new Vector2(0f, 6f * s), error, 11f,
                FontWeight.Regular, InspectorLayout.HintColor);
            return null;
        }

        float listHeight = MathF.Max(
            RowHeight * MinListRows * s,
            ImGui.GetWindowSize().Y - 16f * s - (cursor.Y - origin.Y));
        ImGui.SetCursorScreenPos(cursor);
        Crystarium.PushScrollbarStyle();
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        ImGui.BeginChild("##ext-pick-list", new Vector2(inner * s, listHeight),
            false, ImGuiWindowFlags.NoSavedSettings);
        ExternalItem? picked = null;
        if (filtered.Count == 0)
        {
            var empty = ImGui.GetCursorScreenPos() + new Vector2(8f, 6f) * s;
            ViewText.Label(empty, "No matches.", 11f,
                FontWeight.Regular, InspectorLayout.HintColor);
        }
        var dl = ImGui.GetWindowDrawList();
        uint separator = ImGui.ColorConvertFloat4ToU32(new Vector4(1f, 1f, 1f, 0.06f));
        for (int i = 0; i < filtered.Count; i++)
        {
            var item = filtered[i];
            if (Crystarium.SidebarRow($"##ext-pick-{item.Id:N}", item.Name,
                new SidebarRowProps { NoExpanderSlot = true, Width = rowWidth }))
                picked = item;
            if (i < filtered.Count - 1)
            {
                var max = ImGui.GetItemRectMax();
                var min = ImGui.GetItemRectMin();
                dl.AddLine(new Vector2(min.X, max.Y), new Vector2(min.X + rowWidth * s, max.Y),
                    separator, 1f);
            }
        }
        ImGui.EndChild();
        ImGui.PopStyleVar();
        Crystarium.PopScrollbarStyle();

        if (picked != null)
            ImGui.CloseCurrentPopup();
        return picked;
    }
}
