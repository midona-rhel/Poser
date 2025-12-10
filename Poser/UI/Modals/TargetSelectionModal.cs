using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Poser.Entities;
using Poser.Services;
using Poser.UI.Controls;

namespace Poser.UI.Modals;

/// <summary>
/// Modal for selecting an entity as an orbit target.
/// Shows all actors, bones, and pivot points in a searchable list.
/// </summary>
public class TargetSelectionModal
{
    private readonly IActorManager _actorManager;
    private readonly ISkeletonService _skeletonService;
    private readonly IEditorState _editorState;

    private bool _isOpen = false;
    private string _searchFilter = "";
    private Action<IEntity?>? _onSelected;

    public TargetSelectionModal(
        IActorManager actorManager,
        ISkeletonService skeletonService,
        IEditorState editorState)
    {
        _actorManager = actorManager;
        _skeletonService = skeletonService;
        _editorState = editorState;
    }

    public void Open(Action<IEntity?> onSelected)
    {
        _isOpen = true;
        _searchFilter = "";
        _onSelected = onSelected;
        ImGui.OpenPopup("Select Orbit Target");
    }

    public void Draw()
    {
        if (!_isOpen)
            return;

        var modalSize = new Vector2(400, 500) * ImGuiHelpers.GlobalScale;
        ImGui.SetNextWindowSize(modalSize, ImGuiCond.Appearing);

        var center = ImGui.GetMainViewport().GetCenter();
        ImGui.SetNextWindowPos(center, ImGuiCond.Appearing, new Vector2(0.5f, 0.5f));

        if (ImGui.BeginPopupModal("Select Orbit Target", ref _isOpen, ImGuiWindowFlags.NoResize))
        {
            // Search filter
            ImGui.SetNextItemWidth(-1);
            ImGui.InputTextWithHint("##search", "Search...", ref _searchFilter, 256);

            ImGui.Spacing();

            // Clear target button
            if (ImGui.Button("Clear Target", new Vector2(-1, 0)))
            {
                _onSelected?.Invoke(null);
                _isOpen = false;
                ImGui.CloseCurrentPopup();
            }

            ImGui.Spacing();
            ImGui.Separator();
            ImGui.Spacing();

            // Scrollable entity list
            float footerHeight = ImGui.GetFrameHeightWithSpacing() + ImGui.GetStyle().ItemSpacing.Y;
            using (var child = ImRaii.Child("entity_list", new Vector2(-1, -footerHeight), true))
            {
                if (child.Success)
                {
                    DrawEntityList();
                }
            }

            ImGui.Spacing();

            // Cancel button
            if (ImGui.Button("Cancel", new Vector2(-1, 0)))
            {
                _isOpen = false;
                ImGui.CloseCurrentPopup();
            }

            ImGui.EndPopup();
        }
        else
        {
            _isOpen = false;
        }
    }

    private void DrawEntityList()
    {
        var currentTarget = _editorState.OrbitTarget;
        var filter = _searchFilter.ToLowerInvariant();

        // Draw pivot points first (at top level)
        foreach (var pivot in _editorState.PivotPoints)
        {
            if (!string.IsNullOrEmpty(filter) && !pivot.Name.ToLowerInvariant().Contains(filter))
                continue;

            var isSelected = pivot == currentTarget;
            DrawEntityRow(pivot, FontAwesomeIcon.Crosshairs, UIConstants.SkeletonColor, 0, isSelected);
        }

        // Draw actors and their skeletons/bones
        foreach (var actor in _actorManager.Actors)
        {
            var actorMatches = string.IsNullOrEmpty(filter) || actor.Name.ToLowerInvariant().Contains(filter);

            // Draw actor
            if (actorMatches)
            {
                var isSelected = actor == currentTarget;
                var actorIcon = GetActorIcon(actor);
                DrawEntityRow(actor, actorIcon, UIConstants.DefaultIconColor, 0, isSelected);
            }

            // Draw skeleton bones
            var skeleton = _skeletonService.GetSkeleton(actor) as Skeleton;
            if (skeleton == null || !skeleton.IsValid)
                continue;

            foreach (var bone in skeleton.Bones)
            {
                if (bone.IsHiddenBone)
                    continue;

                var boneMatches = string.IsNullOrEmpty(filter) || bone.Name.ToLowerInvariant().Contains(filter);
                if (!boneMatches && !actorMatches)
                    continue;

                var isSelected = bone == currentTarget;
                DrawEntityRow(bone, FontAwesomeIcon.Bone, UIConstants.DefaultBoneColor, 1, isSelected);
            }
        }
    }

    private void DrawEntityRow(IEntity entity, FontAwesomeIcon icon, Vector4 iconColor, int depth, bool isSelected)
    {
        var indent = depth * 16f * ImGuiHelpers.GlobalScale;
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + indent);

        var flags = ImGuiSelectableFlags.SpanAllColumns;
        if (ImGui.Selectable($"##entity_{entity.Id}", isSelected, flags, new Vector2(0, ImGui.GetFrameHeight())))
        {
            _onSelected?.Invoke(entity);
            _isOpen = false;
            ImGui.CloseCurrentPopup();
        }

        ImGui.SameLine();
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + indent);

        using (ImRaii.PushColor(ImGuiCol.Text, iconColor))
        {
            ImPoser.FontIcon(icon);
        }

        ImGui.SameLine();
        ImGui.Text(entity.Name);
    }

    private static FontAwesomeIcon GetActorIcon(IActor actor)
    {
        return actor.ActorKind switch
        {
            ActorKind.Player => FontAwesomeIcon.User,
            ActorKind.Companion => FontAwesomeIcon.Paw,
            ActorKind.Mount => FontAwesomeIcon.Horse,
            ActorKind.Retainer => FontAwesomeIcon.UserTie,
            ActorKind.BattleNpc => FontAwesomeIcon.Crosshairs,
            ActorKind.EventNpc => FontAwesomeIcon.Comment,
            _ => FontAwesomeIcon.Question
        };
    }
}
