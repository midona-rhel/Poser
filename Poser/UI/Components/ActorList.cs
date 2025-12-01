using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
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
    private readonly IActorManager _actorManager;
    private readonly IAnimationService _animationService;
    private readonly EventBus _eventBus;

    private List<ActorBase> _actors = new();

    public event Action<ActorBase, bool>? OnAnimationFreezeToggle;
    public event Action<ActorBase, bool>? OnPhysicsFreezeToggle;
    public event Action? OnSpawnClone;

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
        // Collapsible header with spawn button
        if (CollapsibleSection.Draw(
            "Actors",
            _actors.Count,
            FontAwesomeIcon.Plus,
            () => OnSpawnClone?.Invoke(),
            "Spawn clone of player"))
        {
            DrawActorsTable();
        }
    }

    private void DrawActorsTable()
    {
        var brighterBg = ImPoser.GetBrighterTableBg();
        var tabHovered = ImPoser.GetTabHoveredColor();
        var tabActive = ImPoser.GetTabActiveColor();

        ImGui.PushStyleColor(ImGuiCol.TableRowBg, brighterBg);
        ImGui.PushStyleColor(ImGuiCol.TableRowBgAlt, brighterBg);

        var tableFlags = ImGuiTableFlags.RowBg |
                         ImGuiTableFlags.BordersInnerV |
                         ImGuiTableFlags.SizingStretchProp;

        if (ImGui.BeginTable("##actors_table", 4, tableFlags))
        {
            // Setup columns
            ImGui.TableSetupColumn("##icon", ImGuiTableColumnFlags.WidthFixed, UIConstants.ScaledRowHeight);
            ImGui.TableSetupColumn("Name", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("##physics", ImGuiTableColumnFlags.WidthFixed, UIConstants.ScaledRowHeight);
            ImGui.TableSetupColumn("##anim", ImGuiTableColumnFlags.WidthFixed, UIConstants.ScaledRowHeight);

            // Header row
            DrawTableHeader();

            // Data rows
            for (int i = 0; i < _actors.Count; i++)
            {
                var actor = _actors[i];
                bool isSelected = _actorManager.IsSelected(actor);
                bool isFrozen = _animationService.IsFrozen(actor);
                bool isPhysicsFrozen = _animationService.IsPhysicsFrozen(actor);

                DrawActorRow(i, actor, isSelected, isFrozen, isPhysicsFrozen, tabHovered, tabActive);
            }

            ImGui.EndTable();
        }

        ImGui.PopStyleColor(2);

        if (_actors.Count == 0)
        {
            ImGui.TextDisabled("No actors in scene");
        }
    }

    private void DrawTableHeader()
    {
        ImGui.TableNextRow();
        ImGui.TableSetColumnIndex(0);
        // Empty for icon column

        ImGui.TableSetColumnIndex(1);
        ImGui.Text("Name");

        ImGui.TableSetColumnIndex(2);
        ImPoser.CenterIconInCell(FontAwesomeIcon.Wind, null, "Freeze Physics");

        ImGui.TableSetColumnIndex(3);
        ImPoser.CenterIconInCell(FontAwesomeIcon.Snowflake, null, "Freeze Animation");
    }

    private void DrawActorRow(int index, ActorBase actor, bool isSelected, bool isFrozen, bool isPhysicsFrozen,
        System.Numerics.Vector4 tabHovered, System.Numerics.Vector4 tabActive)
    {
        // Determine icon color based on poseable state
        var iconColor = isFrozen ? UIConstants.PoseableColor : UIConstants.NotPoseableColor;

        TableRow.Begin(index, isSelected, tabActive, tabHovered);

        // Icon column
        if (TableRow.IconColumn(FontAwesomeIcon.User, iconColor))
        {
            HandleSelection(index);
        }

        // Name column
        if (TableRow.TextColumn(actor.Name))
        {
            HandleSelection(index);
        }

        // Physics freeze checkbox
        bool physicsFrozen = isPhysicsFrozen;
        if (TableRow.CheckboxColumn("physics", ref physicsFrozen, 2))
        {
            OnPhysicsFreezeToggle?.Invoke(actor, physicsFrozen);
        }

        // Animation freeze checkbox
        bool animFrozen = isFrozen;
        if (TableRow.CheckboxColumn("anim", ref animFrozen, 3))
        {
            OnAnimationFreezeToggle?.Invoke(actor, animFrozen);
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
