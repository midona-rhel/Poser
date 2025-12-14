using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Textures;
using Dalamud.Plugin.Services;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using Poser.Core;
using Poser.Entities;
using Poser.IPC;
using Poser.Services;
using Poser.UI.Components;
using Poser.UI.Controls;

namespace Poser.UI;

/// <summary>
/// Properties window with pin functionality.
/// When unpinned: follows selection changes.
/// When pinned: stays frozen to current entity, requests new window for new selections.
/// </summary>
public class PropertiesWindow : Window, IDisposable
{
    private static int _instanceCounter;
    private readonly int _instanceId;

    private const float DefaultWidth = 400f;
    private const float DefaultHeight = 600f;
    private const float MinWidth = 350f;
    private const float MinHeight = 300f;

    private readonly PropertiesPanel _propertiesPanel;
    private readonly ISelectionService _selectionService;
    private readonly IEventBus _eventBus;

    private bool _isEntityPinned;
    private IReadOnlyList<IEntity>? _pinnedEntities;

    /// <summary>
    /// Event fired when pinned and user selects a different entity - should open new window.
    /// </summary>
    public event Action<IReadOnlyList<IEntity>>? OnNewWindowRequested;

    /// <summary>
    /// Event fired when this window should be closed and removed.
    /// </summary>
    public event Action<PropertiesWindow>? OnCloseRequested;

    /// <summary>
    /// Whether this window is pinned to its current entity.
    /// </summary>
    public bool IsEntityPinned
    {
        get => _isEntityPinned;
        set
        {
            if (_isEntityPinned == value)
                return;

            _isEntityPinned = value;

            if (_isEntityPinned)
            {
                // Capture current selection when pinning
                var selection = _selectionService.Selected.ToList();
                _pinnedEntities = selection;
                _propertiesPanel.FreezeToEntities(selection);
            }
            else
            {
                // Unfreeze when unpinning
                _pinnedEntities = null;
                _propertiesPanel.Unfreeze();
            }
        }
    }

    public PropertiesWindow(
        ISelectionService selectionService,
        IActorManager actorManager,
        IPosingService posingService,
        IBonePosingService bonePosingService,
        IAnimationService animationService,
        IAnimationDataService animationDataService,
        IHistoryService historyService,
        IGazeService gazeService,
        ICameraService cameraService,
        ITextureProvider textureProvider,
        IEventBus eventBus,
        IPenumbraService? penumbraService = null,
        IGlamourerService? glamourerService = null,
        ICustomizePlusService? customizePlusService = null,
        IVirtualCameraService? virtualCameraService = null,
        ILightingService? lightingService = null)
        : base($"Properties###{Poser.PluginName}_properties_{_instanceCounter}",
            ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse)
    {
        _instanceId = _instanceCounter++;
        _selectionService = selectionService;
        _eventBus = eventBus;

        Size = new Vector2(DefaultWidth, DefaultHeight);
        SizeCondition = ImGuiCond.FirstUseEver;
        RespectCloseHotkey = true;

        _propertiesPanel = new PropertiesPanel(
            selectionService,
            actorManager,
            posingService,
            bonePosingService,
            animationService,
            animationDataService,
            historyService,
            gazeService,
            cameraService,
            textureProvider,
            penumbraService,
            glamourerService,
            customizePlusService,
            virtualCameraService,
            lightingService);

        // Subscribe to selection changes via EventBus
        _eventBus.Subscribe<SelectionChangedEvent>(OnSelectionChanged);
    }

    private void OnSelectionChanged(SelectionChangedEvent e)
    {
        if (!_isEntityPinned)
        {
            // Not pinned - just follow selection (PropertiesPanel does this automatically when not frozen)
            return;
        }

        // Entity-pinned - check if selection changed to a different entity
        if (e.Selected.Count > 0 && !IsSameSelection(e.Selected, _pinnedEntities))
        {
            // Request new window for the new selection
            OnNewWindowRequested?.Invoke(e.Selected.ToList());
        }
    }

    private static bool IsSameSelection(IReadOnlyList<IEntity>? a, IReadOnlyList<IEntity>? b)
    {
        if (a == null || b == null)
            return a == b;

        if (a.Count != b.Count)
            return false;

        for (int i = 0; i < a.Count; i++)
        {
            if (a[i].Id != b[i].Id)
                return false;
        }

        return true;
    }

    public override void PreDraw()
    {
        base.PreDraw();

        // Update window title
        string title = FormatWindowTitle();
        WindowName = $"{title}###{Poser.PluginName}_properties_{_instanceId}";

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
        // Draw pin button in title bar area
        DrawPinButton();

        ImGui.Separator();
        ImGui.Spacing();

        // Check if we have any valid entities to show
        var entities = _isEntityPinned ? _pinnedEntities : _selectionService.Selected.ToList();

        if (entities == null || entities.Count == 0)
        {
            ImGui.TextDisabled("Select an entity to view properties");
            return;
        }

        // Check validity of pinned entities
        if (_isEntityPinned && !entities.Any(IsEntityValid))
        {
            ImGui.TextDisabled("Pinned entity no longer exists");
            return;
        }

        // Get window draw list for shadows
        var windowDrawList = ImGui.GetWindowDrawList();

        _propertiesPanel.Draw(windowDrawList);
    }

    private void DrawPinButton()
    {
        var cursorPos = ImGui.GetCursorPos();
        var windowWidth = ImGui.GetWindowWidth();
        float buttonSize = Flex.RowHeight * ImGuiHelpers.GlobalScale;
        float padding = Flex.ContentPadding * ImGuiHelpers.GlobalScale;

        // Position pin button at top-right
        ImGui.SetCursorPosX(windowWidth - buttonSize - padding * 2);
        ImGui.SetCursorPosY(cursorPos.Y);

        var pinIcon = _isEntityPinned ? FontAwesomeIcon.Thumbtack : FontAwesomeIcon.MapPin;
        var pinColor = _isEntityPinned ? UIConstants.ActiveColor : UIConstants.InactiveColor;

        using (ImRaii.PushColor(ImGuiCol.Text, pinColor))
        {
            if (ImPoser.FontIconButton($"##pin_{_instanceId}", pinIcon, new Vector2(buttonSize, buttonSize)))
            {
                IsEntityPinned = !IsEntityPinned;
            }
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(_isEntityPinned
                ? "Pinned - click to unpin and follow selection"
                : "Unpinned - click to pin to current entity");
        }

        // Restore cursor for rest of content
        ImGui.SetCursorPos(cursorPos);
    }

    private string FormatWindowTitle()
    {
        var entities = _isEntityPinned ? _pinnedEntities : _selectionService.Selected.ToList();

        if (entities == null || entities.Count == 0)
            return "Properties";

        string prefix = _isEntityPinned ? "[Pinned] " : "";

        if (entities.Count == 1)
            return $"{prefix}{entities[0].Name}";

        if (entities.Count == 2)
            return $"{prefix}{entities[0].Name}, {entities[1].Name}";

        return $"{prefix}{entities[0].Name} + {entities.Count - 1}";
    }

    private static bool IsEntityValid(IEntity entity)
    {
        if (entity is IBone bone)
        {
            return bone.Skeleton is Skeleton skeleton && skeleton.IsValid;
        }

        if (entity is IActor actor)
        {
            return actor.Address != nint.Zero;
        }

        return true;
    }

    public override void OnClose()
    {
        base.OnClose();
        OnCloseRequested?.Invoke(this);
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
        _propertiesPanel.Dispose();
        GC.SuppressFinalize(this);
    }
}
