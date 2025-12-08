using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Poser.Entities;
using Poser.Services;
using Poser.UI.Controls;

namespace Poser.UI.Components;

/// <summary>
/// Unified entity list showing all scene entities in a hierarchical table.
/// Injects services directly - reads state from services, calls methods on services.
/// </summary>
public class EntityList
{
    private const float CheckboxColumnWidth = 32f;
    private const float CellPaddingX = 4f;

    private readonly IActorManager _actorManager;
    private readonly ISelectionService _selectionService;
    private readonly IAnimationService _animationService;
    private readonly IGPoseService _gPoseService;
    private readonly IEditorState _editorState;

    // Local UI state only
    private bool _isCollapsed = false;

    public EntityList(
        IActorManager actorManager,
        ISelectionService selectionService,
        IAnimationService animationService,
        IGPoseService gPoseService,
        IEditorState editorState)
    {
        _actorManager = actorManager;
        _selectionService = selectionService;
        _animationService = animationService;
        _gPoseService = gPoseService;
        _editorState = editorState;
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

                    for (int i = 0; i < actors.Count; i++)
                    {
                        DrawEntityRow(actors[i], 0, tabHovered, tabActive);
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

    private void DrawEntityRow(IEntity entity, int depth, Vector4 tabHovered, Vector4 tabActive)
    {
        IActor? actor = entity as IActor;

        // Read state directly from services
        bool isSelected = _selectionService.IsSelected(entity);
        bool isFrozen = actor != null && _animationService.IsFrozen(actor);
        bool isVisible = true; // TODO: Get from visibility service when created

        var icon = GetIconForEntity(entity);
        Vector4 iconColor = GetIconColor(entity, isVisible);
        Vector4? textColor = isVisible ? null : new Vector4(0.5f, 0.5f, 0.5f, 0.7f);

        bool effectiveCollapsed = _editorState.DebugMode ? false : entity.IsCollapsed;

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
            IsVisible = isVisible
        };

        var result = EntityListItem.Draw(config, tabHovered, tabActive);

        HandleInteractions(entity, actor, result);

        // Draw children if not collapsed
        if (_editorState.DebugMode || !entity.IsCollapsed)
        {
            foreach (var child in entity.Children)
            {
                DrawEntityRow(child, depth + 1, tabHovered, tabActive);
            }
        }
    }

    private void HandleInteractions(IEntity entity, IActor? actor, EntityListItemResult result)
    {
        if (result.CollapseToggled)
        {
            entity.IsCollapsed = !entity.IsCollapsed;
        }

        if (result.Clicked)
        {
            HandleSelectionClick(entity, result.CtrlHeld, result.ShiftHeld);
        }

        if (result.FreezeToggled && actor != null)
        {
            _animationService.ToggleFreeze(actor);
        }

        if (result.VisibilityToggled)
        {
            // TODO: Call visibility service when created
        }
    }

    private void HandleSelectionClick(IEntity entity, bool ctrlHeld, bool shiftHeld)
    {
        if (ctrlHeld)
        {
            _selectionService.ToggleSelection(entity);
        }
        else if (shiftHeld && _selectionService.Primary != null)
        {
            var displayOrder = GatherDisplayOrder();
            _selectionService.SelectRange(_selectionService.Primary, entity, displayOrder);
        }
        else
        {
            _selectionService.Select(entity);
        }
    }

    private IReadOnlyList<IEntity> GatherDisplayOrder()
    {
        var result = new List<IEntity>();
        foreach (var actor in _actorManager.Actors)
        {
            GatherEntitiesRecursive(actor, result);
        }
        return result;
    }

    private void GatherEntitiesRecursive(IEntity entity, List<IEntity> result)
    {
        result.Add(entity);
        if (!entity.IsCollapsed)
        {
            foreach (var child in entity.Children)
            {
                GatherEntitiesRecursive(child, result);
            }
        }
    }

    private static Vector4 GetIconColor(IEntity entity, bool isVisible)
    {
        if (entity.EntityType == EntityType.Skeleton || entity.EntityType == EntityType.Bone)
        {
            return UIConstants.SkeletonColor;
        }
        return isVisible ? UIConstants.DefaultIconColor : UIConstants.HiddenIconColor;
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
}
