using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Poser.Config;
using Poser.Data;
using Poser.Data.Config;
using Poser.Entities;
using Poser.Services;
using Poser.UI.Controls;

namespace Poser.UI.Components;

/// <summary>
/// Unified entity list showing all scene entities in a hierarchical table.
/// Uses TreeListItem hierarchy for clean, type-specific rendering.
/// </summary>
public class EntityList
{
    // Use Flex constants for consistent sizing
    private static float CheckboxColumnWidth => Flex.RowHeight;
    private const float CellPaddingX = 4f; // Table cell specific

    private readonly IActorManager _actorManager;
    private readonly ISelectionService _selectionService;
    private readonly IAnimationService _animationService;
    private readonly ISkeletonService _skeletonService;
    private readonly IGPoseService _gPoseService;
    private readonly IEditorState _editorState;
    private readonly ILightingService? _lightingService;
    private readonly CategoryConfig _categoryConfig;

    // Cached tree items - rebuilt when actors change
    private readonly List<TreeListItem> _items = new();
    private int _lastActorCount = -1;
    private int _lastLightCount = -1;
    private bool _lastShowNsfw = false;

    public EntityList(
        IActorManager actorManager,
        ISelectionService selectionService,
        IAnimationService animationService,
        ISkeletonService skeletonService,
        IGPoseService gPoseService,
        IEditorState editorState,
        ILightingService? lightingService = null)
    {
        _actorManager = actorManager;
        _selectionService = selectionService;
        _animationService = animationService;
        _skeletonService = skeletonService;
        _gPoseService = gPoseService;
        _editorState = editorState;
        _lightingService = lightingService;
        _categoryConfig = CategoryReader.ReadEmbeddedResource();
    }

    public void Draw()
    {
        float checkboxColWidth = CheckboxColumnWidth * ImGuiHelpers.GlobalScale;
        float cellPadding = CellPaddingX * ImGuiHelpers.GlobalScale;

        var actors = _actorManager.Actors;
        var lightCount = _lightingService?.SpawnedLights.Count ?? 0;
        int totalEntities = actors.Count + lightCount + (_gPoseService.IsGPosing ? 1 : 0); // +1 for camera

        // Rebuild tree items only if actor count or light count changed
        if (actors.Count != _lastActorCount || lightCount != _lastLightCount)
        {
            RebuildItems(actors);
            _lastActorCount = actors.Count;
            _lastLightCount = lightCount;
        }

        // Check if NSFW setting changed - update category visibility without full rebuild
        var currentShowNsfw = ConfigurationService.Instance?.Config.Display.ShowNsfwBones ?? false;
        if (_lastShowNsfw != currentShowNsfw)
        {
            _lastShowNsfw = currentShowNsfw;
            // Notify skeleton items to rebuild their categories (preserves actor/skeleton collapse state)
            foreach (var item in _items)
            {
                if (item is ActorListItem actorItem)
                {
                    foreach (var child in actorItem.Children)
                    {
                        if (child is SkeletonListItem skeletonItem)
                        {
                            // If NSFW is being disabled, hide any visible NSFW bones first
                            if (!currentShowNsfw)
                            {
                                skeletonItem.HideNsfwBones();
                            }
                            skeletonItem.RebuildCategories();
                        }
                    }
                }
            }
        }

        // Check if any actors need their skeleton added (e.g., after Penumbra mod loads)
        foreach (var item in _items)
        {
            if (item is ActorListItem actorItem)
            {
                actorItem.TryAddSkeleton();
            }
        }

        // Draw column header icons outside the list
        DrawColumnHeaders(checkboxColWidth);

        // Draw entity list with ControlBackground and border (no rounded corners)
        var tableRowBg = UIColors.ControlBackground;

        using (ImRaii.PushStyle(ImGuiStyleVar.ChildRounding, 0f))
        using (ImRaii.PushStyle(ImGuiStyleVar.ChildBorderSize, 1f))
        using (ImRaii.PushStyle(ImGuiStyleVar.WindowPadding, Vector2.Zero))
        using (ImRaii.PushColor(ImGuiCol.ChildBg, UIColors.ControlBackground))
        using (ImRaii.PushColor(ImGuiCol.Border, UIColors.Border))
        {
            // Use available height for scrolling
            var availableSize = ImGui.GetContentRegionAvail();
            using var child = ImRaii.Child("##entity_list_container", availableSize, true, ImGuiWindowFlags.AlwaysVerticalScrollbar);

            if (child.Success)
            {
                // Compact row padding - horizontal for column spacing, vertical for row height
                using (ImRaii.PushStyle(ImGuiStyleVar.CellPadding, new Vector2(6f * ImGuiHelpers.GlobalScale, 2f * ImGuiHelpers.GlobalScale)))
                using (ImRaii.PushStyle(ImGuiStyleVar.FramePadding, new Vector2(2f * ImGuiHelpers.GlobalScale, 2f * ImGuiHelpers.GlobalScale)))
                using (ImRaii.PushColor(ImGuiCol.TableRowBg, tableRowBg))
                using (ImRaii.PushColor(ImGuiCol.TableRowBgAlt, tableRowBg))
                {
                    if (ImGui.BeginTable("##entities_table", 3, ImGuiTableFlags.RowBg))
                    {
                        ImGui.TableSetupColumn("##name", ImGuiTableColumnFlags.WidthStretch);
                        ImGui.TableSetupColumn("##freeze", ImGuiTableColumnFlags.WidthFixed, checkboxColWidth);
                        ImGui.TableSetupColumn("##visible", ImGuiTableColumnFlags.WidthFixed, checkboxColWidth);

                        if (_gPoseService.IsGPosing)
                        {
                            DrawCameraRow();
                        }

                        // Draw actors
                        foreach (var item in _items)
                        {
                            item.Draw(_selectionService);
                        }

                        // Draw lights
                        if (_lightingService != null)
                        {
                            foreach (var light in _lightingService.SpawnedLights)
                            {
                                DrawLightRow(light);
                            }
                        }

                        ImGui.EndTable();
                    }
                }

                if (totalEntities == 0)
                {
                    ImGui.TextDisabled("No entities in scene");
                }
            }
        }
    }

    private void DrawColumnHeaders(float checkboxColWidth)
    {
        // Header row: spacer for name column, then Lock and Eye icons right-aligned
        float availWidth = ImGui.GetContentRegionAvail().X;
        float cellPadding = 6f * ImGuiHelpers.GlobalScale; // Match table cell padding
        float edgeMargin = 8f * ImGuiHelpers.GlobalScale; // Match EntityListItem edge margin
        float scrollbarWidth = ImGui.GetStyle().ScrollbarSize; // Always visible now

        // Each column total width = checkboxColWidth + cellPadding * 2
        float columnTotalWidth = checkboxColWidth + cellPadding * 2;

        // Checkbox size for centering calculation
        float checkboxSize = PoserCheckbox.Size;
        float iconSize = ImGui.GetFontSize();

        // Position from right edge - account for scrollbar since it's always visible
        float visibleFromRight = scrollbarWidth + columnTotalWidth / 2 + edgeMargin / 4;
        float freezeFromRight = scrollbarWidth + columnTotalWidth * 1.5f - edgeMargin / 4;

        // Draw Lock icon centered over freeze checkbox
        ImGui.SetCursorPosX(availWidth - freezeFromRight - iconSize / 2);
        ImPoser.FontIcon(FontAwesomeIcon.Lock, UIColors.TextDisabled);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Lock animation");

        // Draw Eye icon centered over visible checkbox
        ImGui.SameLine();
        ImGui.SetCursorPosX(availWidth - visibleFromRight - iconSize / 2);
        ImPoser.FontIcon(FontAwesomeIcon.Eye, UIColors.TextDisabled);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Visibility");

        ImGui.Spacing();
    }

    private void RebuildItems(IReadOnlyList<IActor> actors)
    {
        _items.Clear();

        foreach (var actor in actors)
        {
            var actorItem = new ActorListItem(
                actor,
                0,
                _animationService,
                _skeletonService,
                _selectionService,
                _categoryConfig);
            _items.Add(actorItem);
        }
    }

    private void DrawCameraRow()
    {
        var config = new EntityListItemConfig
        {
            Id = "camera",
            Name = "Camera",
            Icon = FontAwesomeIcon.Camera,
            IconColor = UIConstants.DefaultIconColor,
            Depth = 0,
            IsSelected = false,
            IsCollapsible = false,
            IsCollapsed = false,
            ShowFreezeCheckbox = false,
            ShowVisibilityCheckbox = false,
            Tooltip = "Camera"
        };

        var result = EntityListItem.Draw(config);

        if (result.Clicked)
        {
            // TODO: Select camera when it's an entity
        }
    }

    private void DrawLightRow(LightEntity light)
    {
        var icon = light.LightType switch
        {
            Game.Structs.LightType.SpotLight => FontAwesomeIcon.Lightbulb,
            Game.Structs.LightType.AreaLight => FontAwesomeIcon.Sun,
            Game.Structs.LightType.FlatLight => FontAwesomeIcon.Square,
            _ => FontAwesomeIcon.Lightbulb
        };

        var config = new EntityListItemConfig
        {
            Id = light.Id.Unique,
            Name = light.Name,
            Icon = icon,
            IconColor = light.IsLightOn ? UIColors.Yellow : UIConstants.DefaultIconColor,
            Depth = 0,
            IsSelected = _selectionService.IsSelected(light),
            IsCollapsible = false,
            IsCollapsed = false,
            ShowFreezeCheckbox = false,
            ShowVisibilityCheckbox = true,
            IsVisible = light.IsLightOn,
            Tooltip = light.Name
        };

        var result = EntityListItem.Draw(config);

        if (result.Clicked)
        {
            _selectionService.Select(light);
        }

        if (result.VisibilityToggled)
        {
            light.IsLightOn = result.NewVisibilityValue;
        }
    }
}
