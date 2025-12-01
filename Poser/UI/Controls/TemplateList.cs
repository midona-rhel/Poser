using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace Poser.UI.Controls;

public class TemplateList<T>
{
    private readonly string _id;
    private readonly Func<T, string> _getLabel;
    private readonly Action<IReadOnlyList<T>>? _onSelectionChanged;
    private readonly Action<T>? _onDoubleClick;

    private readonly HashSet<int> _selectedIndices = new();
    private List<T> _items = new();

    public IReadOnlyList<T> SelectedItems => _selectedIndices
        .Where(i => i >= 0 && i < _items.Count)
        .Select(i => _items[i])
        .ToList();

    public T? PrimarySelection => _selectedIndices.Count > 0 && _selectedIndices.First() < _items.Count
        ? _items[_selectedIndices.First()]
        : default;

    public TemplateList(string id, Func<T, string> getLabel, Action<IReadOnlyList<T>>? onSelectionChanged = null, Action<T>? onDoubleClick = null)
    {
        _id = id;
        _getLabel = getLabel;
        _onSelectionChanged = onSelectionChanged;
        _onDoubleClick = onDoubleClick;
    }

    public void SetItems(List<T> items)
    {
        _items = items;
        // Remove invalid indices
        _selectedIndices.RemoveWhere(i => i >= _items.Count);
    }

    public void Draw(Vector2 size)
    {
        if (ImGui.BeginListBox($"##{_id}", size))
        {
            for (int i = 0; i < _items.Count; i++)
            {
                var item = _items[i];
                bool isSelected = _selectedIndices.Contains(i);
                string label = _getLabel(item);

                if (ImGui.Selectable($"{label}##{_id}_{i}", isSelected, ImGuiSelectableFlags.AllowDoubleClick))
                {
                    bool ctrlHeld = ImGui.GetIO().KeyCtrl;
                    bool shiftHeld = ImGui.GetIO().KeyShift;

                    if (ctrlHeld)
                    {
                        // Toggle selection
                        if (isSelected)
                            _selectedIndices.Remove(i);
                        else
                            _selectedIndices.Add(i);
                    }
                    else if (shiftHeld && _selectedIndices.Count > 0)
                    {
                        // Range selection
                        int anchor = _selectedIndices.Min();
                        int start = Math.Min(anchor, i);
                        int end = Math.Max(anchor, i);
                        _selectedIndices.Clear();
                        for (int j = start; j <= end; j++)
                            _selectedIndices.Add(j);
                    }
                    else
                    {
                        // Single selection
                        _selectedIndices.Clear();
                        _selectedIndices.Add(i);
                    }

                    _onSelectionChanged?.Invoke(SelectedItems);

                    if (ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
                    {
                        _onDoubleClick?.Invoke(item);
                    }
                }
            }

            if (_items.Count == 0)
            {
                ImGui.TextDisabled("No items");
            }

            ImGui.EndListBox();
        }
    }

    public void ClearSelection()
    {
        _selectedIndices.Clear();
        _onSelectionChanged?.Invoke(SelectedItems);
    }

    public void Select(int index)
    {
        if (index >= 0 && index < _items.Count)
        {
            _selectedIndices.Clear();
            _selectedIndices.Add(index);
            _onSelectionChanged?.Invoke(SelectedItems);
        }
    }

    public void SelectMultiple(IEnumerable<int> indices)
    {
        _selectedIndices.Clear();
        foreach (var i in indices.Where(i => i >= 0 && i < _items.Count))
            _selectedIndices.Add(i);
        _onSelectionChanged?.Invoke(SelectedItems);
    }
}
