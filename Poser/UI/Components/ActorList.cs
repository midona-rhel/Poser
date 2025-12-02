using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Poser.Core;
using Poser.Entities;
using Poser.Services;
using Poser.UI.Controls;

namespace Poser.UI.Components;

/// <summary>
/// Renders the actor list within the scene panel.
/// Uses EventBus for state updates - no local selection state.
/// </summary>
public class ActorList : IDisposable
{
    private const float IconColumnWidth = 32f;
    private const float CellPaddingX = 4f;

    private readonly IActorManager _actorManager;
    private readonly IAnimationService _animationService;
    private readonly EventBus _eventBus;

    private List<ActorBase> _actors = new();
    private bool _isCollapsed = false;

    public ActorList(IActorManager actorManager, IAnimationService animationService, EventBus eventBus)
    {
        _actorManager = actorManager;
        _animationService = animationService;
        _eventBus = eventBus;

        // Subscribe to events via EventBus
        _eventBus.Subscribe<ActorListChangedEvent>(OnActorListChanged);

        // Initialize with current actors
        _actors = _actorManager.Actors.ToList();
    }

    private void OnActorListChanged(ActorListChangedEvent evt)
    {
        _actors = evt.Actors.ToList();
    }

    public void Draw()
    {
        float iconColWidth = IconColumnWidth * ImGuiHelpers.GlobalScale;
        float cellPadding = CellPaddingX * ImGuiHelpers.GlobalScale;

        var brighterBg = ImPoser.GetBrighterTableBg();
        var tabHovered = ImPoser.GetTabHoveredColor();
        var tabActive = ImPoser.GetTabActiveColor();

        using (ImRaii.PushStyle(ImGuiStyleVar.CellPadding, new Vector2(cellPadding, 4f * ImGuiHelpers.GlobalScale)))
        using (ImRaii.PushColor(ImGuiCol.TableRowBg, brighterBg))
        using (ImRaii.PushColor(ImGuiCol.TableRowBgAlt, brighterBg))
        {
            var tableFlags = ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV;

            if (ImGui.BeginTable("##actors_table", 2, tableFlags))
            {
                ImGui.TableSetupColumn("##icon", ImGuiTableColumnFlags.WidthFixed, iconColWidth);
                ImGui.TableSetupColumn("##name", ImGuiTableColumnFlags.WidthStretch);

                // Header row
                ImGui.TableNextRow();
                ImGui.TableNextColumn();

                // Collapse button - center in cell using actual content region (like CenterIconInCell)
                float buttonSize = ImGui.GetFrameHeight();
                float cellWidth = ImGui.GetContentRegionAvail().X;
                float offsetX = (cellWidth - buttonSize) / 2;
                ImGui.SetCursorPosX(ImGui.GetCursorPosX() + offsetX);

                var arrowIcon = _isCollapsed ? FontAwesomeIcon.CaretRight : FontAwesomeIcon.CaretDown;
                if (ImPoser.CenteredIconButton("actors_collapse", arrowIcon, new Vector2(buttonSize, buttonSize)))
                {
                    _isCollapsed = !_isCollapsed;
                }

                ImGui.TableNextColumn();
                ImGui.AlignTextToFramePadding();
                ImGui.TextDisabled($"Actors ({_actors.Count})");

                // Data rows (if not collapsed)
                if (!_isCollapsed)
                {
                    for (int i = 0; i < _actors.Count; i++)
                    {
                        var actor = _actors[i];
                        bool isSelected = _actorManager.IsSelected(actor);
                        bool isFrozen = _animationService.IsFrozen(actor);

                        DrawActorRow(i, actor, isSelected, isFrozen, tabHovered, tabActive);
                    }
                }

                ImGui.EndTable();
            }
        }

        if (!_isCollapsed && _actors.Count == 0)
        {
            ImGui.TextDisabled("No actors in scene");
        }
    }

    private void DrawActorRow(int index, ActorBase actor, bool isSelected, bool isFrozen,
        Vector4 tabHovered, Vector4 tabActive)
    {
        // Determine icon color based on poseable state
        var iconColor = isFrozen ? UIConstants.PoseableColor : UIConstants.NotPoseableColor;

        TableRow.Begin(index, isSelected, tabActive, tabHovered);

        // Icon column - display only, no selection
        TableRow.IconColumn(FontAwesomeIcon.User, iconColor);

        // Name column - this triggers selection
        if (TableRow.TextColumn(actor.Name))
        {
            HandleSelection(index);
        }

        TableRow.End();
    }

    private void HandleSelection(int index)
    {
        if (index < 0 || index >= _actors.Count)
            return;

        var io = ImGui.GetIO();
        bool ctrlHeld = io.KeyCtrl;
        bool shiftHeld = io.KeyShift;

        var actor = _actors[index];

        if (ctrlHeld)
        {
            // Toggle selection
            if (_actorManager.IsSelected(actor))
                _actorManager.RemoveFromSelection(actor);
            else
                _actorManager.AddToSelection(actor);
        }
        else if (shiftHeld && _actorManager.SelectedActors.Count > 0)
        {
            // Range selection - find first selected and select range
            var firstSelected = _actorManager.SelectedActors.First();
            var firstIndex = _actors.IndexOf(firstSelected);
            if (firstIndex >= 0)
            {
                int start = Math.Min(firstIndex, index);
                int end = Math.Max(firstIndex, index);

                var rangeActors = new List<ActorBase>();
                for (int j = start; j <= end; j++)
                {
                    rangeActors.Add(_actors[j]);
                }
                _actorManager.SelectMultiple(rangeActors);
            }
        }
        else
        {
            // Single selection
            _actorManager.Select(actor);
        }
    }

    public void Dispose()
    {
        _eventBus.Unsubscribe<ActorListChangedEvent>(OnActorListChanged);
    }
}
