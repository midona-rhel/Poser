using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Keys;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Poser.Config;
using Poser.Core;
using Poser.Game;
using Poser.Game.Transforms;
using Poser.Services;
using Poser.UI.Composition;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Poser.UI;

/// <summary>
/// Coordinates the lifetime of Poser's presentation surfaces. Window
/// construction and ownership belong to <see cref="UiWindowSet"/>.
/// </summary>
public sealed class UIManager : IUIManager
{
    private readonly IDalamudPluginInterface _pluginInterface;
    private readonly IGPoseService _gPoseService;
    private readonly IEventBus _eventBus;
    private readonly CleanTransformFacade _cleanTransforms;
    private readonly IKeyState _keyState;
    private readonly IEditorState _editorState;
    private readonly ConfigurationService _configService;
    private readonly UiWindowSet _windows;
    private readonly HashSet<string> _keybindsDown = new();
    private List<Dalamud.Interface.Windowing.IWindow>? _hiddenWindows;

    public UIManager(
        IDalamudPluginInterface pluginInterface,
        IGPoseService gPoseService,
        IEventBus eventBus,
        CleanTransformFacade cleanTransforms,
        IKeyState keyState,
        IEditorState editorState,
        ConfigurationService configService,
        UiWindowSet windows)
    {
        _pluginInterface = pluginInterface;
        _gPoseService = gPoseService;
        _eventBus = eventBus;
        _cleanTransforms = cleanTransforms;
        _keyState = keyState;
        _editorState = editorState;
        _configService = configService;
        _windows = windows;

        _windows.Main.OnSettingsRequested += ToggleSettingsWindow;

        _pluginInterface.UiBuilder.Draw += DrawUI;
        _pluginInterface.UiBuilder.OpenMainUi += ToggleMainWindow;
        _pluginInterface.UiBuilder.DisableGposeUiHide = true;
        _pluginInterface.UiBuilder.DisableCutsceneUiHide = true;

        _eventBus.Subscribe<GPoseStateChangedEvent>(OnGPoseStateChanged);
    }

    private void OnGPoseStateChanged(GPoseStateChangedEvent e)
    {
        _windows.SetPrimaryOpen(e.IsGPosing);
    }

    private void DrawUI()
    {
        _windows.System.Draw();
        HandleKeybinds();
    }

    /// <summary>
    /// Settings-configured keybinds. They are edge-triggered, GPose-only, and
    /// suppressed while an ImGui text field owns the keyboard.
    /// </summary>
    private void HandleKeybinds()
    {
        if (!_gPoseService.IsGPosing || ImGui.GetIO().WantTextInput)
        {
            _keybindsDown.Clear();
            return;
        }

        var overrides = _configService.Config.UI.Keybinds;
        Fire("Undo", "Ctrl+Z", () =>
        {
            if (_cleanTransforms.CanUndo)
                _cleanTransforms.Undo();
        });
        Fire("Redo", "Ctrl+Y", () =>
        {
            if (_cleanTransforms.CanRedo)
                _cleanTransforms.Redo();
        });
        Fire("Translate mode", "Ctrl+1", () => _editorState.TransformTool = TransformTool.Move);
        Fire("Rotate mode", "Ctrl+2", () => _editorState.TransformTool = TransformTool.Rotate);
        Fire("Scale mode", "Ctrl+3", () => _editorState.TransformTool = TransformTool.Scale);
        Fire("Hide UI", "Ctrl+H", ToggleAllUi);
        return;

        void Fire(string action, string fallback, Action run)
        {
            string chord = overrides.TryGetValue(action, out var bound) ? bound : fallback;
            bool active = ChordDown(chord);
            if (active && _keybindsDown.Add(action))
                run();
            else if (!active)
                _keybindsDown.Remove(action);
        }
    }

    private bool ChordDown(string chord)
    {
        bool needCtrl = false;
        bool needShift = false;
        bool needAlt = false;
        VirtualKey key = VirtualKey.NO_KEY;

        foreach (var part in chord.Split('+'))
        {
            switch (part.Trim().ToUpperInvariant())
            {
                case "CTRL":
                    needCtrl = true;
                    break;
                case "SHIFT":
                    needShift = true;
                    break;
                case "ALT":
                    needAlt = true;
                    break;
                case { Length: 1 } token when token[0] is >= 'A' and <= 'Z':
                    key = (VirtualKey)((int)VirtualKey.A + (token[0] - 'A'));
                    break;
                case { Length: 1 } token when token[0] is >= '0' and <= '9':
                    key = (VirtualKey)((int)VirtualKey.KEY_0 + (token[0] - '0'));
                    break;
                default:
                    if (Enum.TryParse<VirtualKey>(part.Trim(), true, out var parsed))
                        key = parsed;
                    break;
            }
        }

        if (key == VirtualKey.NO_KEY)
            return false;
        if (needCtrl != _keyState[VirtualKey.CONTROL])
            return false;
        if (needShift != _keyState[VirtualKey.SHIFT])
            return false;
        if (needAlt != _keyState[VirtualKey.MENU])
            return false;
        return _keyState[key];
    }

    private void ToggleAllUi()
    {
        if (_hiddenWindows == null)
        {
            _hiddenWindows = _windows.System.Windows.Where(window => window.IsOpen).ToList();
            foreach (var window in _hiddenWindows)
                window.IsOpen = false;
            return;
        }

        foreach (var window in _hiddenWindows)
            window.IsOpen = true;
        _hiddenWindows = null;
    }

    public void ToggleMainWindow()
        => _windows.Main.IsOpen = !_windows.Main.IsOpen;

    private void ToggleSettingsWindow()
        => _windows.Settings.IsOpen = !_windows.Settings.IsOpen;

    public void Dispose()
    {
        _eventBus.Unsubscribe<GPoseStateChangedEvent>(OnGPoseStateChanged);

        _windows.Main.OnSettingsRequested -= ToggleSettingsWindow;

        _pluginInterface.UiBuilder.Draw -= DrawUI;
        _pluginInterface.UiBuilder.OpenMainUi -= ToggleMainWindow;
    }
}
