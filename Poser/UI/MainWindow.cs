using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using Poser.Entities;
using Poser.History;
using Poser.Services;
using Poser.UI.Controls;

namespace Poser.UI;

public class MainWindow : Window
{
    private const float SidebarWidth = 280f;

    private readonly IGPoseService _gPoseService;
    private readonly IActorManager _actorManager;
    private readonly IAnimationService _animationService;
    private readonly IHistoryService _historyService;
    private readonly TemplateList<ActorBase> _actorList;

    public MainWindow(
        IGPoseService gPoseService,
        IActorManager actorManager,
        IAnimationService animationService,
        IHistoryService historyService)
        : base($"{Poser.PluginName}###poser_sidebar_window",
            ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoMove)
    {
        _gPoseService = gPoseService;
        _actorManager = actorManager;
        _animationService = animationService;
        _historyService = historyService;

        _actorList = new TemplateList<ActorBase>(
            "actors",
            actor => actor.Name,
            onSelectionChanged: OnActorSelectionChanged,
            onDoubleClick: actor => { /* Handle actor double click */ }
        );

        // Subscribe to actor changes
        _actorManager.OnActorsChanged += RefreshActorList;

        // Initial refresh
        RefreshActorList();

        // Fixed width sidebar
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(SidebarWidth, 400),
            MaximumSize = new Vector2(SidebarWidth, 4000)
        };
    }

    private void OnActorSelectionChanged(IReadOnlyList<ActorBase> selectedActors)
    {
        _actorManager.SelectMultiple(selectedActors);
    }

    public override void PreDraw()
    {
        base.PreDraw();

        // Position window stuck to right edge of screen
        var displaySize = ImGui.GetIO().DisplaySize;
        var windowHeight = displaySize.Y;

        Position = new Vector2(displaySize.X - SidebarWidth, 0);
        Size = new Vector2(SidebarWidth, windowHeight);
    }

    private void RefreshActorList()
    {
        _actorList.SetItems(_actorManager.Actors.ToList());
    }

    public override void Draw()
    {
        DrawTopBar();

        ImGui.Separator();
        ImGui.Spacing();

        DrawActorSection();
    }

    private void DrawTopBar()
    {
        var windowWidth = ImGui.GetContentRegionAvail().X;

        // Left side: GPose status
        DrawGPoseStatus();

        // Right side: Undo/Redo buttons
        ImGui.SameLine();
        DrawUndoRedoButtons(windowWidth);
    }

    private void DrawGPoseStatus()
    {
        if (_gPoseService.IsGPosing)
        {
            ImGui.TextColored(new Vector4(0.4f, 1.0f, 0.4f, 1.0f), "GPose Active");
        }
        else
        {
            ImGui.TextDisabled("Not in GPose");
        }
    }

    private void DrawUndoRedoButtons(float windowWidth)
    {
        // Calculate position for right-aligned buttons
        float buttonSize = ImGui.GetFrameHeight();
        float spacing = ImGui.GetStyle().ItemSpacing.X;
        float buttonsWidth = (buttonSize * 2) + spacing;
        float rightX = windowWidth - buttonsWidth;

        ImGui.SetCursorPosX(rightX);

        // Push icon font for FontAwesome
        ImGui.PushFont(UiBuilder.IconFont);

        // Undo button with FontAwesome icon
        using (ImRaii.Disabled(!_historyService.CanUndo))
        {
            if (ImGui.Button(FontAwesomeIcon.Undo.ToIconString(), new Vector2(buttonSize, buttonSize)))
            {
                _historyService.Undo();
            }
        }

        ImGui.PopFont();

        if (_historyService.CanUndo && ImGui.IsItemHovered())
        {
            ImGui.SetTooltip($"Undo: {_historyService.UndoDescription}");
        }

        ImGui.SameLine();

        ImGui.PushFont(UiBuilder.IconFont);

        // Redo button with FontAwesome icon
        using (ImRaii.Disabled(!_historyService.CanRedo))
        {
            if (ImGui.Button(FontAwesomeIcon.Redo.ToIconString(), new Vector2(buttonSize, buttonSize)))
            {
                _historyService.Redo();
            }
        }

        ImGui.PopFont();

        if (_historyService.CanRedo && ImGui.IsItemHovered())
        {
            ImGui.SetTooltip($"Redo: {_historyService.RedoDescription}");
        }
    }

    private void DrawActorSection()
    {
        // Actors header
        if (_gPoseService.IsGPosing)
        {
            ImGui.Text($"Actors ({_actorManager.Actors.Count})");
        }
        else
        {
            ImGui.TextDisabled("Enter GPose to see actors");
        }
        ImGui.Spacing();

        // Actor list
        _actorList.Draw(new Vector2(ImGui.GetContentRegionAvail().X, 150));

        ImGui.Spacing();

        // Freeze button for selected actor
        DrawFreezeButton();
    }

    private void DrawFreezeButton()
    {
        var selectedActors = _actorManager.SelectedActors;
        bool hasSelection = selectedActors.Count > 0;

        using (ImRaii.Disabled(!hasSelection))
        {
            // Check if any selected actor is frozen
            bool anyFrozen = selectedActors.Any(a => _animationService.IsFrozen(a));
            bool allFrozen = hasSelection && selectedActors.All(a => _animationService.IsFrozen(a));

            string buttonText;
            if (selectedActors.Count > 1)
            {
                buttonText = allFrozen
                    ? $"Unfreeze {selectedActors.Count} Actors"
                    : $"Freeze {selectedActors.Count} Actors";
            }
            else
            {
                buttonText = anyFrozen ? "Unfreeze Animation" : "Freeze Animation";
            }

            if (ImGui.Button(buttonText, new Vector2(ImGui.GetContentRegionAvail().X, 0)))
            {
                bool freeze = !allFrozen;
                foreach (var actor in selectedActors)
                {
                    bool currentlyFrozen = _animationService.IsFrozen(actor);
                    if (currentlyFrozen != freeze)
                    {
                        var action = new FreezeAnimationAction(_animationService, actor, freeze);
                        _historyService.Push(action);
                    }
                }
            }
        }

        if (!hasSelection && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
        {
            ImGui.SetTooltip("Select an actor first (Ctrl+click for multiple)");
        }
    }
}
