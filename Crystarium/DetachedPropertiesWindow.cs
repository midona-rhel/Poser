using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Textures;
using Dalamud.Plugin.Services;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Windowing;
using Poser.Entities;
using Poser.IPC;
using Poser.Services;
using Poser.UI.Components;

namespace Poser.UI;

/// <summary>
/// A detached/pop-out properties window that is frozen to specific entities.
/// Multiple instances can exist simultaneously.
/// </summary>
public class DetachedPropertiesWindow : Window, IDisposable
{
    private static int _instanceCounter;

    private readonly PropertiesPanel _propertiesPanel;
    private readonly IReadOnlyList<IEntity> _frozenEntities;
    private readonly string _titleText;

    /// <summary>
    /// Event fired when this window should be closed and removed.
    /// </summary>
    public event Action<DetachedPropertiesWindow>? OnCloseRequested;

    public DetachedPropertiesWindow(
        IReadOnlyList<IEntity> entities,
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
        IPenumbraService? penumbraService = null,
        IGlamourerService? glamourerService = null,
        ICustomizePlusService? customizePlusService = null,
        IVirtualCameraService? virtualCameraService = null,
        ILightingService? lightingService = null)
        : base($"Properties##detached_{_instanceCounter++}",
            ImGuiWindowFlags.NoCollapse)
    {
        _frozenEntities = entities.ToList();
        _titleText = FormatWindowTitle(entities);

        // Create a new properties panel for this window
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

        // Freeze it to the captured entities
        _propertiesPanel.FreezeToEntities(entities);

        // Default size
        Size = new Vector2(400, 500);
        SizeCondition = ImGuiCond.FirstUseEver;

        // Allow closing
        RespectCloseHotkey = true;
    }

    public override void PreDraw()
    {
        base.PreDraw();

        // Update window title with entity info
        WindowName = $"{_titleText}###detached_{GetHashCode()}";
    }

    public override void Draw()
    {
        // Check if any frozen entities are still valid
        bool anyValid = _frozenEntities.Any(IsEntityValid);
        if (!anyValid)
        {
            ImGui.TextDisabled("Entity no longer exists");
            return;
        }

        _propertiesPanel.Draw();
    }

    public override void OnClose()
    {
        base.OnClose();
        OnCloseRequested?.Invoke(this);
    }

    private static bool IsEntityValid(IEntity entity)
    {
        // Check if entity is still valid
        // For bones, check if skeleton is still valid
        if (entity is IBone bone)
        {
            return bone.Skeleton is Skeleton skeleton && skeleton.IsValid;
        }

        // For actors, check address is still valid
        if (entity is IActor actor)
        {
            return actor.Address != nint.Zero;
        }

        return true;
    }

    private static string FormatWindowTitle(IReadOnlyList<IEntity> entities)
    {
        if (entities.Count == 0)
            return "Properties";

        if (entities.Count == 1)
            return entities[0].Name;

        if (entities.Count == 2)
            return $"{entities[0].Name}, {entities[1].Name}";

        return $"{entities[0].Name} + {entities.Count - 1}";
    }

    public void Dispose()
    {
        _propertiesPanel.Dispose();
        GC.SuppressFinalize(this);
    }
}
