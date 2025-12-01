using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Poser.Entities;
using Poser.UI.Controls;

namespace Poser.UI.Components;

/// <summary>
/// Renders a single actor row in the actors table.
/// </summary>
public static class ActorRow
{
    public static void Draw(
        ActorBase actor,
        int index,
        bool isSelected,
        bool isFrozen,
        bool isPhysicsFrozen,
        float rowHeight,
        Vector4 tabHovered,
        Vector4 tabActive,
        Action<int> onSelect,
        Action<ActorBase, bool> onAnimationFreezeToggle,
        Action<ActorBase, bool> onPhysicsFreezeToggle)
    {
        ImGui.TableNextRow();
        ImGui.PushID(index);

        // Apply selection highlight immediately
        if (isSelected)
        {
            ImGui.TableSetBgColor(ImGuiTableBgTarget.RowBg0, ImGui.GetColorU32(tabActive));
        }

        // Icon column - green if poseable (frozen), red if not
        ImGui.TableSetColumnIndex(0);
        var iconColor = isFrozen
            ? new Vector4(0.4f, 0.9f, 0.4f, 1.0f)  // Green - poseable
            : new Vector4(0.9f, 0.4f, 0.4f, 1.0f); // Red - not poseable
        var iconCellStart = ImGui.GetCursorScreenPos();
        ImPoser.CenterIconInCell(FontAwesomeIcon.User, iconColor);

        // Invisible button over icon for selection
        ImGui.SetCursorScreenPos(iconCellStart);
        if (ImGui.InvisibleButton($"##icon_sel_{index}", new Vector2(rowHeight, rowHeight)))
        {
            onSelect(index);
        }
        bool iconHovered = ImGui.IsItemHovered();

        // Name column (selectable)
        ImGui.TableSetColumnIndex(1);
        ImPoser.VerticalCenterText();

        var style = ImGui.GetStyle();
        if (ImGui.Selectable($"{actor.Name}##actor_{index}", isSelected, ImGuiSelectableFlags.None,
            new Vector2(ImGui.GetContentRegionAvail().X, rowHeight - style.CellPadding.Y * 2)))
        {
            onSelect(index);
        }
        bool nameHovered = ImGui.IsItemHovered();

        // Physics checkbox column
        ImGui.TableSetColumnIndex(2);
        bool physicsFrozen = isPhysicsFrozen;
        if (ImPoser.DrawCenteredCheckbox($"##physics_{index}", ref physicsFrozen))
        {
            onPhysicsFreezeToggle(actor, physicsFrozen);
        }
        bool physicsHovered = ImGui.IsItemHovered();

        // Animation checkbox column
        ImGui.TableSetColumnIndex(3);
        bool animFrozen = isFrozen;
        if (ImPoser.DrawCenteredCheckbox($"##anim_{index}", ref animFrozen))
        {
            onAnimationFreezeToggle(actor, animFrozen);
        }
        bool animHovered = ImGui.IsItemHovered();

        // Apply hover highlight at the end (only if not selected)
        bool rowHovered = iconHovered || nameHovered || physicsHovered || animHovered;
        if (rowHovered && !isSelected)
        {
            ImGui.TableSetBgColor(ImGuiTableBgTarget.RowBg0, ImGui.GetColorU32(tabHovered));
        }

        ImGui.PopID();
    }
}
