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
    private readonly PoseFileInspectorSection _poseFileSection;
    private readonly Keybind[] _keybinds;
    private List<Dalamud.Interface.Windowing.IWindow>? _hiddenWindows;

    public UIManager(
        IDalamudPluginInterface pluginInterface,
        IGPoseService gPoseService,
        IEventBus eventBus,
        CleanTransformFacade cleanTransforms,
        IKeyState keyState,
        IEditorState editorState,
        ConfigurationService configService,
        UiWindowSet windows,
        PoseFileInspectorSection poseFileSection)
    {
        _pluginInterface = pluginInterface;
        _gPoseService = gPoseService;
        _eventBus = eventBus;
        _cleanTransforms = cleanTransforms;
        _keyState = keyState;
        _editorState = editorState;
        _configService = configService;
        _windows = windows;
        _poseFileSection = poseFileSection;

        // Bound ONCE: the seven delegates and their parsed chords are the whole
        // per-frame keybind state, so a frame that fires nothing allocates
        // nothing.
        _keybinds =
        [
            new Keybind("Undo", () =>
            {
                if (_cleanTransforms.CanUndo)
                    _cleanTransforms.Undo();
            }),
            new Keybind("Redo", () =>
            {
                if (_cleanTransforms.CanRedo)
                    _cleanTransforms.Redo();
            }),
            new Keybind(
                "Translate mode",
                () => _editorState.TransformTool = TransformTool.Move),
            new Keybind(
                "Rotate mode",
                () => _editorState.TransformTool = TransformTool.Rotate),
            new Keybind(
                "Scale mode",
                () => _editorState.TransformTool = TransformTool.Scale),
            new Keybind(
                "Universal mode",
                () => _editorState.TransformTool = TransformTool.Universal),
            new Keybind("Hide UI", ToggleAllUi),
        ];

        _windows.Main.OnSettingsRequested += ToggleSettingsWindow;
        _windows.Main.OnSpawnBrowserRequested += OpenSpawnBrowserAt;
        _poseFileSection.OnLibraryRequested += OpenPoseLibrary;
        _configService.OnConfigurationChanged += ApplyConfiguredTheme;

        _pluginInterface.UiBuilder.Draw += DrawUI;
        _pluginInterface.UiBuilder.OpenMainUi += ToggleMainWindow;
        _pluginInterface.UiBuilder.DisableGposeUiHide = true;
        _pluginInterface.UiBuilder.DisableCutsceneUiHide = true;

        _eventBus.Subscribe<GPoseStateChangedEvent>(OnGPoseStateChanged);
    }

    private void OnGPoseStateChanged(GPoseStateChangedEvent e)
    {
        var config = _configService.Config;
        if (e.IsGPosing)
        {
            if (config.OpenOnGPoseEnter)
                _windows.SetPrimaryOpen(true);
        }
        else if (config.CloseWithGPose)
        {
            // The setting promises "hide ALL Poser windows", and settings is
            // the one surface SetPrimaryOpen does not own.
            _windows.CloseAll();
        }
    }

    private void DrawUI()
    {
        if (!FontRegistry.Ready)
            return;

        Interactive.BeginFrame();
        _windows.System.Draw();
        Crystarium.FloatingMenu.EndFrame();
        // The one hover-help card renders after every window has drawn,
        // so registrations from any pane are complete and the card sits
        // on the foreground list above all of them.
        Crystarium.HoverHelp.Render();
        Interactive.EndFrame();
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
            foreach (var bind in _keybinds)
                bind.Down = false;
            return;
        }

        foreach (var bind in _keybinds)
        {
            // The SAME resolver the hover badges display, so a shown
            // chord always matches the one that fires. The resolver hands back
            // the stored string, so an unchanged binding compares equal and the
            // chord is never re-parsed.
            string chord = PoserKeybinds.Effective(bind.Name);
            if (!ReferenceEquals(chord, bind.Chord) &&
                !string.Equals(chord, bind.Chord, StringComparison.Ordinal))
                bind.Parse(chord);

            bool active = ChordDown(bind);
            if (active && !bind.Down)
            {
                bind.Down = true;
                bind.Run();
            }
            else if (!active)
            {
                bind.Down = false;
            }
        }
    }

    private bool ChordDown(Keybind bind)
    {
        if (bind.Key == VirtualKey.NO_KEY)
            return false;
        if (bind.Ctrl != _keyState[VirtualKey.CONTROL])
            return false;
        if (bind.Shift != _keyState[VirtualKey.SHIFT])
            return false;
        if (bind.Alt != _keyState[VirtualKey.MENU])
            return false;
        return _keyState[bind.Key];
    }

    /// <summary>
    /// One configured keybind: the action name the resolver and the hover
    /// badges key on, the delegate it runs, and its chord PARSED — string work
    /// happens only when the configured chord text actually changes.
    /// </summary>
    private sealed class Keybind(string name, Action run)
    {
        public string Name { get; } = name;
        public Action Run { get; } = run;
        public string Chord { get; private set; } = "";
        public bool Ctrl { get; private set; }
        public bool Shift { get; private set; }
        public bool Alt { get; private set; }
        public VirtualKey Key { get; private set; } = VirtualKey.NO_KEY;

        /// <summary>Edge state: the chord was down on the previous frame.</summary>
        public bool Down { get; set; }

        public void Parse(string chord)
        {
            Chord = chord;
            Ctrl = false;
            Shift = false;
            Alt = false;
            Key = VirtualKey.NO_KEY;

            foreach (var part in chord.Split('+'))
            {
                switch (part.Trim().ToUpperInvariant())
                {
                    case "CTRL":
                        Ctrl = true;
                        break;
                    case "SHIFT":
                        Shift = true;
                        break;
                    case "ALT":
                        Alt = true;
                        break;
                    case { Length: 1 } token when token[0] is >= 'A' and <= 'Z':
                        Key = (VirtualKey)((int)VirtualKey.A + (token[0] - 'A'));
                        break;
                    case { Length: 1 } token when token[0] is >= '0' and <= '9':
                        Key = (VirtualKey)((int)VirtualKey.KEY_0 + (token[0] - '0'));
                        break;
                    default:
                        if (Enum.TryParse<VirtualKey>(part.Trim(), true, out var parsed))
                            Key = parsed;
                        break;
                }
            }
        }
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

    // Open-or-move, never toggle: the unpinned browser already closes on
    // focus loss, so a plus click while it is open MOVES it to that plus
    // (and its tab) instead of silently swallowing the click.
    private void OpenSpawnBrowserAt(
        System.Numerics.Vector2 anchor, Views.SpawnBrowserTab tab)
        => _windows.SpawnBrowser.OpenAt(anchor, tab);

    // Open, not toggle: "Library…" and a redirected "Import…" are openers, so
    // a second press must not close a library the user is already looking at.
    // The library is workspace content, so the shell has to be showing for it
    // to be reachable at all.
    private void OpenPoseLibrary()
    {
        _windows.Main.IsOpen = true;
        _windows.Main.ShowLibrary();
    }

    private void ApplyConfiguredTheme() =>
        ThemeSelection.Apply(
            _configService.Config.UI.Theme,
            _configService.Config.UI.AccentIndex);

    public void Dispose()
    {
        _eventBus.Unsubscribe<GPoseStateChangedEvent>(OnGPoseStateChanged);

        _windows.Main.OnSettingsRequested -= ToggleSettingsWindow;
        _windows.Main.OnSpawnBrowserRequested -= OpenSpawnBrowserAt;
        _poseFileSection.OnLibraryRequested -= OpenPoseLibrary;
        _configService.OnConfigurationChanged -= ApplyConfiguredTheme;

        _pluginInterface.UiBuilder.Draw -= DrawUI;
        _pluginInterface.UiBuilder.OpenMainUi -= ToggleMainWindow;
    }
}
