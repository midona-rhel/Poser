using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;

namespace Poser.UI.Controls;

public class TemplateList<T>
{
    private readonly string _id;
    private readonly Func<T, string> _getLabel;
    private readonly Action<T>? _onSelect;
    private readonly Action<T>? _onDoubleClick;

    private int _selectedIndex = -1;
    private List<T> _items = new();

    public T? SelectedItem => _selectedIndex >= 0 && _selectedIndex < _items.Count ? _items[_selectedIndex] : default;
    public int SelectedIndex => _selectedIndex;

    public TemplateList(string id, Func<T, string> getLabel, Action<T>? onSelect = null, Action<T>? onDoubleClick = null)
    {
        _id = id;
        _getLabel = getLabel;
        _onSelect = onSelect;
        _onDoubleClick = onDoubleClick;
    }

    public void SetItems(List<T> items)
    {
        _items = items;
        if (_selectedIndex >= _items.Count)
            _selectedIndex = _items.Count - 1;
    }

    public void Draw(Vector2 size)
    {
        // Use ImGui's built-in ListBox which respects theme colors (FrameBg)
        if (ImGui.BeginListBox($"##{_id}", size))
        {
            for (int i = 0; i < _items.Count; i++)
            {
                var item = _items[i];
                bool isSelected = i == _selectedIndex;
                string label = _getLabel(item);

                if (ImGui.Selectable($"{label}##{_id}_{i}", isSelected, ImGuiSelectableFlags.AllowDoubleClick))
                {
                    _selectedIndex = i;
                    _onSelect?.Invoke(item);

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
        _selectedIndex = -1;
    }

    public void Select(int index)
    {
        if (index >= 0 && index < _items.Count)
            _selectedIndex = index;
    }
}
