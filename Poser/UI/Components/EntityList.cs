using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Poser.Controllers;
using Poser.Core;
using Poser.Core.BoneInfo;
using Poser.Entities;
using Poser.Services;
using Poser.UI.Controls;

namespace Poser.UI.Components;

/// <summary>
/// Unified entity list showing all scene entities (actors, camera, etc.) in a hierarchical table.
/// Uses the reusable EntityListItem component for consistent UI.
/// </summary>
public class EntityList : IDisposable
{
    private const float CheckboxColumnWidth = 32f;
    private const float CellPaddingX = 4f;

    private readonly IActorManager _actorManager;
    private readonly IAnimationService _animationService;
    private readonly IActorSpawnService _spawnService;
    private readonly ICameraService _cameraService;
    private readonly IGPoseService _gPoseService;
    private readonly ISkeletonService _skeletonService;
    private readonly IEditorState _editorState;
    private readonly IEventBus _eventBus;
    private readonly IPosingController _controller;

    private List<IActor> _actors = new();
    private bool _isCollapsed = false;

    // Reusable bone category list component
    private readonly BoneCategoryList _boneCategoryList;

    public EntityList(
        IActorManager actorManager,
        IAnimationService animationService,
        IActorSpawnService spawnService,
        ICameraService cameraService,
        IGPoseService gPoseService,
        ISkeletonService skeletonService,
        IEditorState editorState,
        IEventBus eventBus,
        IPosingController controller)
    {
        _actorManager = actorManager;
        _animationService = animationService;
        _spawnService = spawnService;
        _cameraService = cameraService;
        _gPoseService = gPoseService;
        _skeletonService = skeletonService;
        _editorState = editorState;
        _eventBus = eventBus;
        _controller = controller;

        _boneCategoryList = new BoneCategoryList(editorState);

        _eventBus.Subscribe<ActorListChangedEvent>(OnActorListChanged);
        _actors = _actorManager.Actors.ToList();
    }

    private void OnActorListChanged(ActorListChangedEvent evt)
    {
        _actors = evt.Actors.ToList();
    }

    public void Draw()
    {
        float checkboxColWidth = CheckboxColumnWidth * ImGuiHelpers.GlobalScale;
        float cellPadding = CellPaddingX * ImGuiHelpers.GlobalScale;

        var brighterBg = ImPoser.GetBrighterTableBg();
        var tabHovered = ImPoser.GetTabHoveredColor();
        var tabActive = ImPoser.GetTabActiveColor();

        // Count total top-level entities (actors + camera)
        int totalEntities = _actors.Count + (_gPoseService.IsGPosing ? 1 : 0);

        using (ImRaii.PushStyle(ImGuiStyleVar.CellPadding, new Vector2(cellPadding, 4f * ImGuiHelpers.GlobalScale)))
        using (ImRaii.PushColor(ImGuiCol.TableRowBg, brighterBg))
        using (ImRaii.PushColor(ImGuiCol.TableRowBgAlt, brighterBg))
        {
            var tableFlags = ImGuiTableFlags.RowBg;

            // 3 columns: name (with collapse+icon+indent), freeze, visibility
            if (ImGui.BeginTable("##entities_table", 3, tableFlags))
            {
                ImGui.TableSetupColumn("##name", ImGuiTableColumnFlags.WidthStretch);
                ImGui.TableSetupColumn("##freeze", ImGuiTableColumnFlags.WidthFixed, checkboxColWidth);
                ImGui.TableSetupColumn("##visible", ImGuiTableColumnFlags.WidthFixed, checkboxColWidth);

                // Header row
                ImGui.TableNextRow();

                // Name column with collapse button
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

                // Freeze column header
                ImGui.TableNextColumn();
                ImPoser.CenterIconInCell(FontAwesomeIcon.Snowflake, null, "Freeze animation");

                // Visibility column header
                ImGui.TableNextColumn();
                ImPoser.CenterIconInCell(FontAwesomeIcon.Eye, null, "Visibility");

                // Data rows (if not collapsed)
                if (!_isCollapsed)
                {
                    // Draw camera first (non-collapsible)
                    if (_gPoseService.IsGPosing)
                    {
                        DrawCameraRow(tabHovered, tabActive);
                    }

                    // Draw actors with their children
                    for (int i = 0; i < _actors.Count; i++)
                    {
                        var actor = _actors[i];
                        DrawEntityRow(actor, 0, i, tabHovered, tabActive);
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

    private void DrawCameraRow(Vector4 tabHovered, Vector4 tabActive)
    {
        var pos = _cameraService.GetCameraPosition();
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
            Tooltip = $"Position: {pos.X:F1}, {pos.Y:F1}, {pos.Z:F1}"
        };

        EntityListItem.Draw(config, tabHovered, tabActive);
    }

    private void DrawEntityRow(IEntity entity, int depth, int index, Vector4 tabHovered, Vector4 tabActive)
    {
        // Get actor-specific state
        IActor? actor = entity as IActor;
        bool isActorSelected = actor != null && _actorManager.IsSelected(actor);
        bool isFrozen = actor != null && _animationService.IsFrozen(actor);
        bool isActorVisible = actor != null && _spawnService.IsVisible(actor);

        // Ensure skeleton is created for actors
        if (actor != null)
        {
            _skeletonService.GetSkeleton(actor);
        }

        // Determine icon and colors
        var icon = GetIconForEntity(entity);
        Vector4 iconColor;
        Vector4? textColor = null;

        if (entity.EntityType == EntityType.Skeleton || entity.EntityType == EntityType.Bone)
        {
            iconColor = UIConstants.SkeletonColor;
        }
        else
        {
            iconColor = isActorVisible ? UIConstants.DefaultIconColor : UIConstants.HiddenIconColor;
            if (!isActorVisible)
            {
                textColor = new Vector4(0.5f, 0.5f, 0.5f, 0.7f);
            }
        }

        // Check selection via unified selection system
        bool isSelected = _editorState.IsSelected(entity);

        // Determine collapse state
        bool effectiveCollapsed = _editorState.DebugMode ? false : entity.IsCollapsed;

        // Build config based on entity type
        var config = new EntityListItemConfig
        {
            Id = entity.Id.ToString(),
            Name = entity.Name,
            Icon = icon,
            IconColor = iconColor,
            TextColor = textColor,
            Depth = depth,
            IsSelected = isSelected,
            IsCollapsible = entity.IsCollapsible,
            IsCollapsed = effectiveCollapsed,
            ShowFreezeCheckbox = actor != null,
            IsFrozen = isFrozen,
            ShowVisibilityCheckbox = actor != null || entity is Skeleton,
            IsVisible = actor != null ? isActorVisible : (entity is Skeleton skel ? skel.Actor.IsEditMode : true)
        };

        var result = EntityListItem.Draw(config, tabHovered, tabActive);

        // Handle interactions
        if (result.CollapseToggled)
        {
            entity.IsCollapsed = !entity.IsCollapsed;
        }

        if (result.Clicked)
        {
            HandleEntityClick(entity, index, result.CtrlHeld, result.ShiftHeld);
        }

        if (result.FreezeToggled && actor != null)
        {
            if (isActorSelected)
            {
                foreach (var selectedActor in _actorManager.SelectedActors)
                {
                    _controller.SetFrozen(selectedActor, result.NewFreezeValue);
                }
            }
            else
            {
                _controller.SetFrozen(actor, result.NewFreezeValue);
            }
        }

        if (result.VisibilityToggled)
        {
            // Cascade visibility to all children
            SetVisibilityRecursive(entity, result.NewVisibilityValue);

            // Special handling for actors
            if (actor != null)
            {
                _controller.SetActorVisibility(actor, result.NewVisibilityValue);
            }
            // Special handling for skeleton - also set edit mode
            else if (entity is Skeleton skeleton)
            {
                skeleton.Actor.IsEditMode = result.NewVisibilityValue;

                // Auto-freeze animation when enabling edit mode
                if (result.NewVisibilityValue && !_animationService.IsFrozen(skeleton.Actor))
                {
                    _controller.SetFrozen(skeleton.Actor, true);
                }
            }
        }

        // Draw children if not collapsed
        bool showChildren = _editorState.DebugMode || !entity.IsCollapsed;
        if (showChildren)
        {
            // Special handling for Skeleton in Category mode
            if (entity is Skeleton skeleton && _editorState.BoneDisplayMode == BoneDisplayMode.Category)
            {
                _boneCategoryList.Draw(skeleton, depth + 1, tabHovered, tabActive);
            }
            else if (entity.Children.Count > 0)
            {
                int childIndex = 0;
                foreach (var child in entity.Children)
                {
                    DrawEntityRow(child, depth + 1, childIndex++, tabHovered, tabActive);
                }
            }
        }
    }

    private void HandleEntityClick(IEntity entity, int index, bool ctrlHeld, bool shiftHeld)
    {
        if (ctrlHeld)
        {
            _editorState.ToggleSelection(entity);
        }
        else if (shiftHeld && _editorState.PrimarySelection != null)
        {
            _editorState.SelectRange(_editorState.PrimarySelection, entity);
        }
        else
        {
            _editorState.Select(entity);
        }
    }

    private static void SetVisibilityRecursive(IEntity entity, bool visible)
    {
        entity.IsVisible = visible;
        foreach (var child in entity.Children)
        {
            SetVisibilityRecursive(child, visible);
        }
    }

    private static FontAwesomeIcon GetIconForEntity(IEntity entity)
    {
        return entity.EntityType switch
        {
            EntityType.Player => FontAwesomeIcon.User,
            EntityType.Npc => FontAwesomeIcon.UserShield,
            EntityType.Companion => FontAwesomeIcon.Paw,
            EntityType.Camera => FontAwesomeIcon.Camera,
            EntityType.Skeleton => FontAwesomeIcon.CircleNodes,
            EntityType.Bone => entity is Bone bone && bone.ChildBones.Count > 0
                ? FontAwesomeIcon.CircleNodes
                : FontAwesomeIcon.Circle,
            _ => entity switch
            {
                IActor actor => actor.ActorKind switch
                {
                    ActorKind.Player => FontAwesomeIcon.User,
                    ActorKind.Companion => FontAwesomeIcon.Paw,
                    ActorKind.Mount => FontAwesomeIcon.Horse,
                    ActorKind.Ornament => FontAwesomeIcon.Gem,
                    ActorKind.BattleNpc => FontAwesomeIcon.UserShield,
                    ActorKind.EventNpc => FontAwesomeIcon.UserTie,
                    ActorKind.Retainer => FontAwesomeIcon.Store,
                    _ => FontAwesomeIcon.User
                },
                _ => FontAwesomeIcon.QuestionCircle
            }
        };
    }

    public void Dispose()
    {
        _eventBus.Unsubscribe<ActorListChangedEvent>(OnActorListChanged);
    }
}
