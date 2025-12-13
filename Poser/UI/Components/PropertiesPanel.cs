using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Utility;
using Dalamud.Plugin.Services;
using Dalamud.Interface.Utility.Raii;
using Poser.Config;
using Poser.Data.Config;
using Poser.Entities;
using Poser.Entities.Capabilities;
using Poser.History;
using Poser.Services;
using Poser.UI.Controls;

namespace Poser.UI.Components;

/// <summary>
/// Renders the Properties panel showing details of the selected entity.
/// Uses capability interfaces to determine what UI to show.
/// Can operate in live mode (follows selection) or frozen mode (locked to specific entities).
/// </summary>
public class PropertiesPanel : IDisposable
{
    private const float TabBarWidth = 48f;
    private const float TabHeight = 48f;

    private readonly ISelectionService _selectionService;

    // Tab panes
    private readonly TransformTabPane _transformTabPane;
    private readonly AnimationTabPane _animationTabPane;
    private readonly TabbedPanel _tabbedPanel;

    // Frozen mode: when set, panel shows these entities instead of live selection
    private List<IEntity>? _frozenEntities;

    /// <summary>
    /// Event fired when user clicks pop-out button. Passes current selection to create detached window.
    /// </summary>
    public event Action<IReadOnlyList<IEntity>>? OnPopOutRequested;

    public PropertiesPanel(
        ISelectionService selectionService,
        IActorManager actorManager,
        IPosingService posingService,
        IBonePosingService bonePosingService,
        IAnimationService animationService,
        IAnimationDataService animationDataService,
        IHistoryService historyService,
        IGazeService gazeService,
        ICameraService cameraService,
        ITextureProvider textureProvider)
    {
        _selectionService = selectionService;

        // Create tab panes
        _transformTabPane = new TransformTabPane(posingService, bonePosingService, animationService, historyService);
        _animationTabPane = new AnimationTabPane(animationService, animationDataService, historyService, gazeService, actorManager, cameraService, textureProvider);

        // Create tabbed panel with narrow tabs
        _tabbedPanel = new TabbedPanel(TabBarWidth, TabHeight, _transformTabPane, _animationTabPane);
    }

    /// <summary>
    /// Freezes this panel to show specific entities instead of following live selection.
    /// Used for detached/pop-out windows.
    /// </summary>
    public void FreezeToEntities(IReadOnlyList<IEntity> entities)
    {
        _frozenEntities = entities.ToList();
    }

    /// <summary>
    /// Gets the entities this panel is currently showing (frozen or live selection).
    /// </summary>
    private IReadOnlyList<IEntity> GetCurrentEntities()
    {
        if (_frozenEntities != null)
            return _frozenEntities;
        return _selectionService.Selected;
    }

    /// <summary>
    /// Gets the primary entity this panel is currently showing.
    /// </summary>
    private IEntity? GetPrimaryEntity()
    {
        if (_frozenEntities != null)
            return _frozenEntities.FirstOrDefault();
        return _selectionService.Primary;
    }

    public void Draw()
    {
        // Push UIColors for consistent theming
        using (ImRaii.PushColor(ImGuiCol.Text, UIColors.Text))
        using (ImRaii.PushColor(ImGuiCol.TextDisabled, UIColors.TextDisabled))
        using (ImRaii.PushColor(ImGuiCol.FrameBg, UIColors.ControlBackground))
        using (ImRaii.PushColor(ImGuiCol.FrameBgHovered, UIColors.ControlBackgroundHovered))
        using (ImRaii.PushColor(ImGuiCol.FrameBgActive, UIColors.ControlBackground))
        using (ImRaii.PushColor(ImGuiCol.Button, UIColors.Button))
        using (ImRaii.PushColor(ImGuiCol.ButtonHovered, UIColors.ButtonHovered))
        using (ImRaii.PushColor(ImGuiCol.ButtonActive, UIColors.ButtonActive))
        using (ImRaii.PushColor(ImGuiCol.Header, UIColors.SelectionActive))
        using (ImRaii.PushColor(ImGuiCol.HeaderHovered, UIColors.SelectionHovered))
        using (ImRaii.PushColor(ImGuiCol.HeaderActive, UIColors.SelectionActiveHovered))
        using (ImRaii.PushColor(ImGuiCol.CheckMark, UIColors.Text))
        using (ImRaii.PushColor(ImGuiCol.SliderGrab, UIColors.Button))
        using (ImRaii.PushColor(ImGuiCol.SliderGrabActive, UIColors.ButtonActive))
        using (ImRaii.PushColor(ImGuiCol.Border, UIColors.Border))
        {
            var entities = GetCurrentEntities();
            var entity = GetPrimaryEntity();

            DrawEntity(entity, entities);
        }
    }

    private void DrawEmptyHeader()
    {
        var headerText = "Properties";
        var headerWidth = ImGui.CalcTextSize(headerText).X;
        var availWidth = ImGui.GetContentRegionAvail().X;

        // Only show pop-out button if not already frozen (i.e., in main panel)
        float buttonWidth = _frozenEntities == null ? 24 * ImGuiHelpers.GlobalScale : 0;

        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + (availWidth - headerWidth) * 0.5f);
        ImGui.Text(headerText);
    }

    private void DrawEntity(IEntity? primaryEntity, IReadOnlyList<IEntity> allEntities)
    {
        // Header with selection summary and pop-out button
        DrawSelectionHeader(allEntities);

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        // Update tab panes with current entity (null renders disabled state)
        _transformTabPane.SetEntity(primaryEntity);
        _animationTabPane.SetEntity(primaryEntity);

        // Draw tabbed panel
        _tabbedPanel.Draw();
    }

    /// <summary>
    /// Draws the selection header with smart formatting and pop-out button.
    /// </summary>
    private void DrawSelectionHeader(IReadOnlyList<IEntity> entities)
    {
        var headerText = FormatSelectionText(entities);
        var headerWidth = ImGui.CalcTextSize(headerText).X;
        var availWidth = ImGui.GetContentRegionAvail().X;

        // Pop-out button (only in live mode, not frozen/popped-out)
        float buttonSize = 20 * ImGuiHelpers.GlobalScale;
        bool showPopOut = _frozenEntities == null;
        bool canPopOut = entities.Count > 0;

        if (showPopOut)
        {
            // Center text, button right-aligned
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + (availWidth - headerWidth) * 0.5f);
            ImGui.AlignTextToFramePadding();
            ImGui.Text(headerText);

            ImGui.SameLine();

            // Right-align the button
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X - buttonSize);

            using (ImRaii.Disabled(!canPopOut))
            {
                if (ImPoser.CenteredIconButton("pop_out", FontAwesomeIcon.ExternalLinkAlt, new Vector2(buttonSize, buttonSize), canPopOut ? "Pop out to separate window" : null))
                {
                    if (canPopOut)
                        OnPopOutRequested?.Invoke(entities);
                }
            }
        }
        else
        {
            // Just center the text (popped-out window, no button)
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + (availWidth - headerWidth) * 0.5f);
            ImGui.Text(headerText);
        }
    }

    /// <summary>
    /// Formats selection text based on entity count and types.
    /// </summary>
    private static string FormatSelectionText(IReadOnlyList<IEntity> entities)
    {
        if (entities.Count == 0)
            return "Properties";

        if (entities.Count == 1)
            return ConfigurationService.Instance.GetDisplayName(entities[0]);

        // Check if all entities are the same type
        // Note: Bone and VirtualBone are both IBone, BoneCategory is separate
        var first = entities[0];
        var firstType = GetEntityTypeCategory(first);

        bool allSameType = entities.All(e => GetEntityTypeCategory(e) == firstType);

        if (entities.Count == 2)
        {
            // Two entities: show both names
            if (allSameType)
                return $"{ConfigurationService.Instance.GetDisplayName(entities[0])}, {ConfigurationService.Instance.GetDisplayName(entities[1])}";
            else
                return $"{ConfigurationService.Instance.GetDisplayName(entities[0])} + 1 entity";
        }

        // 3+ entities
        int otherCount = entities.Count - 1;
        string typeName = allSameType ? GetTypePluralName(firstType, otherCount) : "entities";

        return $"{ConfigurationService.Instance.GetDisplayName(first)} + {otherCount} {typeName}";
    }

    /// <summary>
    /// Gets a category string for grouping entity types.
    /// </summary>
    private static string GetEntityTypeCategory(IEntity entity)
    {
        return entity switch
        {
            IBone => "bone",
            IActor => "actor",
            // BoneCategoryListItem selections would be VirtualBone
            _ => entity.GetType().Name
        };
    }

    /// <summary>
    /// Gets the plural name for an entity type category.
    /// </summary>
    private static string GetTypePluralName(string typeCategory, int count)
    {
        return typeCategory switch
        {
            "bone" => count == 1 ? "bone" : "bones",
            "actor" => count == 1 ? "actor" : "actors",
            _ => count == 1 ? "entity" : "entities"
        };
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
    }
}
