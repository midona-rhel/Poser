using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
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
    private const float CheckboxColumnWidth = 32f;
    private const float CellPaddingX = 4f;

    private readonly IActorManager _actorManager;
    private readonly ISelectionService _selectionService;
    private readonly IAnimationService _animationService;
    private readonly ISkeletonService _skeletonService;
    private readonly IGPoseService _gPoseService;
    private readonly CategoryConfig _categoryConfig;

    // Cached tree items - rebuilt when actors change
    private readonly List<TreeListItem> _items = new();
    private int _lastActorCount = -1;
    private bool _lastShowNsfw = false;

    // Local UI state only
    private bool _isCollapsed = false;

    public EntityList(
        IActorManager actorManager,
        ISelectionService selectionService,
        IAnimationService animationService,
        ISkeletonService skeletonService,
        IGPoseService gPoseService,
        IEditorState editorState)
    {
        _actorManager = actorManager;
        _selectionService = selectionService;
        _animationService = animationService;
        _skeletonService = skeletonService;
        _gPoseService = gPoseService;
        _categoryConfig = CategoryReader.ReadEmbeddedResource();
    }

    public void Draw()
    {
        float checkboxColWidth = CheckboxColumnWidth * ImGuiHelpers.GlobalScale;
        float cellPadding = CellPaddingX * ImGuiHelpers.GlobalScale;

        var brighterBg = ImPoser.GetBrighterTableBg();
        var tabHovered = ImPoser.GetTabHoveredColor();
        var tabActive = ImPoser.GetTabActiveColor();

        var actors = _actorManager.Actors;
        int totalEntities = actors.Count + (_gPoseService.IsGPosing ? 1 : 0); // +1 for camera

        // Rebuild tree items only if actor count changed
        if (actors.Count != _lastActorCount)
        {
            RebuildItems(actors);
            _lastActorCount = actors.Count;
        }

        // Check if NSFW setting changed - update category visibility without full rebuild
        var currentShowNsfw = PoserSettings.Instance?.ShowNsfwBones ?? false;
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

        using (ImRaii.PushStyle(ImGuiStyleVar.CellPadding, new Vector2(cellPadding, 4f * ImGuiHelpers.GlobalScale)))
        using (ImRaii.PushColor(ImGuiCol.TableRowBg, brighterBg))
        using (ImRaii.PushColor(ImGuiCol.TableRowBgAlt, brighterBg))
        {
            if (ImGui.BeginTable("##entities_table", 3, ImGuiTableFlags.RowBg))
            {
                ImGui.TableSetupColumn("##name", ImGuiTableColumnFlags.WidthStretch);
                ImGui.TableSetupColumn("##freeze", ImGuiTableColumnFlags.WidthFixed, checkboxColWidth);
                ImGui.TableSetupColumn("##visible", ImGuiTableColumnFlags.WidthFixed, checkboxColWidth);

                DrawHeaderRow(totalEntities);

                if (!_isCollapsed)
                {
                    if (_gPoseService.IsGPosing)
                    {
                        DrawCameraRow(tabHovered, tabActive);
                    }

                    foreach (var item in _items)
                    {
                        item.Draw(tabHovered, tabActive, _selectionService);
                    }
                }

                ImGui.EndTable();
            }
        }

        if (!_isCollapsed && totalEntities == 0)
        {
            ImGui.TextDisabled("No entities in scene");
        }
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

    private void DrawHeaderRow(int totalEntities)
    {
        ImGui.TableNextRow();

        ImGui.TableNextColumn();
        float buttonSize = ImGui.GetFrameHeight();
        var arrowIcon = _isCollapsed ? FontAwesomeIcon.CaretRight : FontAwesomeIcon.CaretDown;
        if (ImPoser.IconButton("entities_collapse", arrowIcon, new Vector2(buttonSize, buttonSize)))
        {
            _isCollapsed = !_isCollapsed;
        }
        ImGui.SameLine();
        ImGui.AlignTextToFramePadding();
        ImGui.TextDisabled($"Entities ({totalEntities})");

        ImGui.TableNextColumn();
        ImPoser.CenterIconInCell(FontAwesomeIcon.Snowflake, null, "Freeze animation");

        ImGui.TableNextColumn();
        ImPoser.CenterIconInCell(FontAwesomeIcon.Eye, null, "Visibility");
    }

    private void DrawCameraRow(Vector4 tabHovered, Vector4 tabActive)
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

        var result = EntityListItem.Draw(config, tabHovered, tabActive);

        if (result.Clicked)
        {
            // TODO: Select camera when it's an entity
        }
    }
}
