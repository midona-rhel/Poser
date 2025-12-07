using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Poser.Services;

namespace Poser.UI.Controls;

/// <summary>
/// Reusable animation selector dropdown with search functionality.
/// </summary>
public class AnimationSelector
{
    private readonly IAnimationDataService _animationDataService;
    private string _searchText = "";
    private List<AnimationEntry> _filteredAnimations = new();

    public AnimationSelector(IAnimationDataService animationDataService)
    {
        _animationDataService = animationDataService;
        _filteredAnimations = animationDataService.Animations.Take(50).ToList();
    }

    /// <summary>
    /// Draw the animation selector.
    /// </summary>
    /// <param name="id">Unique ImGui ID</param>
    /// <param name="currentId">Currently selected timeline ID (null if none)</param>
    /// <param name="onSelect">Callback when an animation is selected</param>
    /// <param name="width">Width of the button (-1 for auto)</param>
    /// <returns>True if an animation was selected this frame</returns>
    public bool Draw(string id, ushort? currentId, Action<ushort> onSelect, float width = -1)
    {
        bool selected = false;

        string buttonLabel = "Select...";
        if (currentId.HasValue)
        {
            var entry = _animationDataService.GetById(currentId.Value);
            buttonLabel = entry != null ? entry.Name : $"#{currentId}";
        }

        if (width > 0)
            ImGui.SetNextItemWidth(width);

        if (ImGui.Button($"{buttonLabel}##{id}_btn", new Vector2(width, 0)))
        {
            ImGui.OpenPopup($"{id}_popup");
            _searchText = "";
            _filteredAnimations = _animationDataService.Animations.Take(50).ToList();
        }

        if (ImGui.BeginPopup($"{id}_popup"))
        {
            // Search box
            ImGui.SetNextItemWidth(280 * ImGuiHelpers.GlobalScale);
            if (ImGui.InputTextWithHint($"##{id}_search", "Search animations...", ref _searchText, 256))
            {
                _filteredAnimations = _animationDataService.Search(_searchText, 50).ToList();
            }

            // Focus the search box when popup opens
            if (ImGui.IsWindowAppearing())
            {
                ImGui.SetKeyboardFocusHere(-1);
            }

            ImGui.Separator();

            // Animation list
            float listHeight = 250 * ImGuiHelpers.GlobalScale;
            float listWidth = 280 * ImGuiHelpers.GlobalScale;

            using (var child = ImRaii.Child($"{id}_list", new Vector2(listWidth, listHeight)))
            {
                if (child.Success)
                {
                    foreach (var entry in _filteredAnimations)
                    {
                        DrawAnimationEntry(id, entry, currentId, onSelect, ref selected);
                    }
                }
            }

            ImGui.EndPopup();
        }

        return selected;
    }

    private void DrawAnimationEntry(string id, AnimationEntry entry, ushort? currentId, Action<ushort> onSelect, ref bool selected)
    {
        bool isSelected = currentId.HasValue && currentId.Value == entry.TimelineId;

        // Category icon
        var icon = entry.Category switch
        {
            AnimationCategory.Emote => FontAwesomeIcon.SmileBeam,
            AnimationCategory.Action => FontAwesomeIcon.Bolt,
            _ => FontAwesomeIcon.Film
        };

        var iconColor = entry.Category switch
        {
            AnimationCategory.Emote => new Vector4(0.4f, 0.8f, 0.4f, 1f),
            AnimationCategory.Action => new Vector4(0.8f, 0.4f, 0.4f, 1f),
            _ => new Vector4(0.6f, 0.6f, 0.6f, 1f)
        };

        // Draw icon
        using (ImRaii.PushFont(UiBuilder.IconFont))
        {
            ImGui.TextColored(iconColor, icon.ToIconString());
        }

        ImGui.SameLine();

        // Draw selectable with name
        if (ImGui.Selectable($"{entry.Name}##{id}_{entry.TimelineId}", isSelected))
        {
            onSelect(entry.TimelineId);
            ImGui.CloseCurrentPopup();
            selected = true;
        }

        // Show ID on the right
        string idText = $"[{entry.TimelineId}]";
        ImGui.SameLine(ImGui.GetContentRegionAvail().X - ImGui.CalcTextSize(idText).X + 20 * ImGuiHelpers.GlobalScale);
        ImGui.TextDisabled(idText);
    }

    /// <summary>
    /// Reset the selector state (e.g., when switching actors).
    /// </summary>
    public void Reset()
    {
        _searchText = "";
        _filteredAnimations = _animationDataService.Animations.Take(50).ToList();
    }
}
