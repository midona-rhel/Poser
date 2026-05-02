using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using Poser.Core;
using Poser.Entities;
using Poser.Services;
using Poser.UI.Components;
using Poser.UI.Controls;

namespace Poser.UI;

/// <summary>
/// Standalone window for the entity list.
/// Clicking an entity triggers OnEntityActivated to open/focus a properties window.
/// </summary>
public class EntityListWindow : Window
{
    private const float DefaultWidth = 300f;
    private const float DefaultHeight = 500f;
    private const float MinWidth = 250f;
    private const float MinHeight = 200f;

    private readonly IGPoseService _gPoseService;
    private readonly ISelectionService _selectionService;
    private readonly IActorSpawnService _spawnService;
    private readonly IEventBus _eventBus;
    private readonly EntityList _entityList;

    /// <summary>
    /// Event fired when user clicks/activates an entity (should open properties window).
    /// </summary>
    public event Action<IEntity>? OnEntityActivated;

    public EntityListWindow(
        IGPoseService gPoseService,
        IActorManager actorManager,
        ISelectionService selectionService,
        IAnimationService animationService,
        ISkeletonService skeletonService,
        IEditorState editorState,
        IActorSpawnService spawnService,
        IEventBus eventBus)
        : base($"Scene###{Poser.PluginConstants.PluginName}_entity_list",
            ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse)
    {
        Size = new Vector2(DefaultWidth, DefaultHeight);
        SizeCondition = ImGuiCond.FirstUseEver;

        _gPoseService = gPoseService;
        _selectionService = selectionService;
        _spawnService = spawnService;
        _eventBus = eventBus;

        _entityList = new EntityList(
            actorManager,
            selectionService,
            animationService,
            skeletonService,
            gPoseService,
            editorState);

        // Subscribe to selection changes via EventBus
        _eventBus.Subscribe<SelectionChangedEvent>(OnSelectionChanged);
    }

    private void OnSelectionChanged(SelectionChangedEvent e)
    {
        // When selection changes (user clicked an entity), fire activation event
        if (e.Selected.Count > 0)
        {
            OnEntityActivated?.Invoke(e.Selected[0]);
        }
    }

    public override void PreDraw()
    {
        base.PreDraw();

        // Apply UI colors
        ImGui.PushStyleColor(ImGuiCol.WindowBg, UIColors.Background);
        ImGui.PushStyleColor(ImGuiCol.ChildBg, UIColors.Background);
        ImGui.PushStyleColor(ImGuiCol.Text, UIColors.Text);
        ImGui.PushStyleColor(ImGuiCol.TextDisabled, UIColors.TextDisabled);
        ImGui.PushStyleColor(ImGuiCol.Border, UIColors.Border);
        ImGui.PushStyleColor(ImGuiCol.TitleBg, UIColors.TitleBar);
        ImGui.PushStyleColor(ImGuiCol.TitleBgActive, UIColors.TitleBarActive);
        ImGui.PushStyleColor(ImGuiCol.Button, UIColors.Button);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, UIColors.ButtonHovered);
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, UIColors.ButtonActive);
        ImGui.PushStyleColor(ImGuiCol.FrameBg, UIColors.ControlBackground);
        ImGui.PushStyleColor(ImGuiCol.Header, UIColors.SelectionActive);
        ImGui.PushStyleColor(ImGuiCol.HeaderHovered, UIColors.SelectionHovered);
        ImGui.PushStyleColor(ImGuiCol.HeaderActive, UIColors.SelectionActiveHovered);

        float padding = Flex.ContentPadding * ImGuiHelpers.GlobalScale;
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(padding, padding));

        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(MinWidth, MinHeight),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue)
        };
    }

    public override void Draw()
    {
        if (!_gPoseService.IsGPosing)
        {
            ImGui.TextDisabled("Enter GPose to see scene");
            return;
        }

        float padding = Flex.ContentPadding * ImGuiHelpers.GlobalScale;

        // Calculate height for scrollable region (leave room for buttons)
        float buttonHeight = UIConstants.ScaledButtonSize + ImGui.GetStyle().ItemSpacing.Y * 2;
        float availableHeight = ImGui.GetContentRegionAvail().Y - buttonHeight;

        // Scrollable entity list region
        using (var child = ImRaii.Child("entity_list_scroll", new Vector2(-1, availableHeight), false))
        {
            if (child.Success)
            {
                _entityList.Draw();
            }
        }

        ImGui.Spacing();

        DrawBottomButtons();
    }

    private void DrawBottomButtons()
    {
        var primarySelected = _selectionService.GetFirstSelected<IActor>();

        using var row = Flex.Row(gap: Flex.ItemGap);

        // Add button on the left
        row.Fixed(Flex.RowHeight, () =>
        {
            if (PoserButton.DrawIcon("add_entity", FontAwesomeIcon.Plus, "Add entity"))
            {
                ImGui.OpenPopup("##add_entity_popup");
            }
        });

        // Popup menu for add options
        if (ImGui.BeginPopup("##add_entity_popup"))
        {
            if (ImGui.MenuItem("Spawn Actor Clone"))
            {
                _spawnService.SpawnPlayerClone();
            }

            ImGui.EndPopup();
        }

        row.Spacer();

        // Delete button on the right
        bool canDelete = primarySelected != null && _spawnService.IsSpawnedActor(primarySelected);
        string deleteTooltip = canDelete
            ? "Delete selected entity"
            : "Can only delete spawned entities";

        row.Fixed(Flex.ButtonWidth, (w, h) =>
        {
            using (ImRaii.Disabled(!canDelete))
            {
                if (PoserButton.DrawWithWidth("delete_selected", "Delete", w))
                {
                    if (canDelete && primarySelected != null)
                    {
                        _spawnService.DestroyActor(primarySelected);
                    }
                }
            }
        });
    }

    public override void PostDraw()
    {
        ImGui.PopStyleVar(1);
        ImGui.PopStyleColor(14);
        base.PostDraw();
    }

    public void Dispose()
    {
        _eventBus.Unsubscribe<SelectionChangedEvent>(OnSelectionChanged);
    }
}
