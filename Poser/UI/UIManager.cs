using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Keys;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Poser.Application.Animation;
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
    private readonly IVirtualCameraService _cameras;
    private readonly AnimationSceneActions _sceneActions;
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
        PoseFileInspectorSection poseFileSection,
        IVirtualCameraService cameras,
        AnimationSceneActions sceneActions)
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
        _cameras = cameras;
        _sceneActions = sceneActions;

        // Bound ONCE, in registry order: the delegates and their parsed
        // chords are the whole per-frame keybind state, so a frame that fires
        // nothing allocates nothing. Every registered action is bound here —
        // the registry is the list, and an id it names with no handler is a
        // build-time hole, which is why the lookup throws rather than
        // skipping.
        _keybinds = BuildKeybinds();

        _windows.Main.OnSettingsRequested += ToggleSettingsWindow;
        _windows.Main.OnSpawnBrowserRequested += OpenSpawnBrowserAt;
        _poseFileSection.OnLibraryRequested += OpenPoseLibrary;
        _configService.OnConfigurationChanged += ApplyConfiguration;

        _pluginInterface.UiBuilder.Draw += DrawUI;
        _pluginInterface.UiBuilder.OpenMainUi += ToggleMainWindow;
        ApplyUiHidePolicy();

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
        // Outside the window system's draw pass: a reference picture closed
        // from its own bar leaves the system here, and the dialog that adds
        // one belongs to no window.
        _windows.PumpReferenceImages();
        Crystarium.FloatingMenu.EndFrame();
        // The one hover-help card renders after every window has drawn,
        // so registrations from any pane are complete and the card sits
        // on the foreground list above all of them.
        Crystarium.HoverHelp.Render();
        Interactive.EndFrame();
        HandleKeybinds();
    }

    /// <summary>
    /// The delegate behind every registered action. Each one is a call the UI
    /// already makes from a button, a menu row or a strip — a chord binds an
    /// existing command, it never becomes the only way to reach one.
    /// </summary>
    private Keybind[] BuildKeybinds()
    {
        var handlers = new Dictionary<string, Action>(StringComparer.Ordinal)
        {
            ["Undo"] = () =>
            {
                if (_cleanTransforms.CanUndo)
                    _cleanTransforms.Undo();
            },
            ["Redo"] = () =>
            {
                if (_cleanTransforms.CanRedo)
                    _cleanTransforms.Redo();
            },
            ["Translate mode"] =
                () => _editorState.TransformTool = TransformTool.Move,
            ["Rotate mode"] =
                () => _editorState.TransformTool = TransformTool.Rotate,
            ["Scale mode"] =
                () => _editorState.TransformTool = TransformTool.Scale,
            ["Universal mode"] =
                () => _editorState.TransformTool = TransformTool.Universal,
            ["Cycle gizmo mode"] = () => _editorState.TransformTool =
                _editorState.TransformTool switch
                {
                    TransformTool.Move => TransformTool.Rotate,
                    TransformTool.Rotate => TransformTool.Scale,
                    TransformTool.Scale => TransformTool.Universal,
                    _ => TransformTool.Move,
                },
            ["Toggle transform space"] = () =>
                _editorState.TransformOrientation =
                    _editorState.TransformOrientation
                        == TransformOrientation.Local
                        ? TransformOrientation.Global
                        : TransformOrientation.Local,
            ["Cycle rotation pivot"] = () => _editorState.RotationPivot =
                _editorState.RotationPivot == RotationPivot.Self
                    ? RotationPivot.Parent
                    : RotationPivot.Self,
            ["Cycle symmetry"] = () => _editorState.SymmetryMode =
                _editorState.SymmetryMode switch
                {
                    SymmetryMode.Off => SymmetryMode.Copy,
                    SymmetryMode.Copy => SymmetryMode.Mirror,
                    _ => SymmetryMode.Off,
                },
            ["Toggle bone overlay"] = () =>
                _windows.SkeletonOverlay.UserVisible =
                    !_windows.SkeletonOverlay.UserVisible,
            ["Selected bones only"] = () =>
                _editorState.ShowSelectedBonesOnly =
                    !_editorState.ShowSelectedBonesOnly,
            ["Cycle skeleton view"] = () => _editorState.SkeletonViewMode =
                _editorState.SkeletonViewMode switch
                {
                    SkeletonViewMode.Default => SkeletonViewMode.Octahedra,
                    SkeletonViewMode.Octahedra => SkeletonViewMode.Joints,
                    _ => SkeletonViewMode.Default,
                },
            ["Hide UI"] = ToggleAllUi,
            ["Toggle workspace"] = ToggleMainWindow,
            ["Toggle settings"] = ToggleSettingsWindow,
            ["Toggle scene panel"] = _windows.ToggleSceneWindow,
            ["Open pose library"] = OpenPoseLibrary,
            ["Next tab"] = () => _windows.Main.CycleTab(1),
            ["Previous tab"] = () => _windows.Main.CycleTab(-1),
            ["Next camera"] = () => CycleCamera(1),
            ["Previous camera"] = () => CycleCamera(-1),
            ["Freeze all actors"] = () => _sceneActions.FreezeAll(),
            ["Resume all actors"] = () => _sceneActions.ResumeAll(),
        };

        var binds = new Keybind[KeybindRegistry.Actions.Count];
        for (int i = 0; i < binds.Length; i++)
        {
            string id = KeybindRegistry.Actions[i].Id;
            binds[i] = new Keybind(id, handlers[id]);
        }
        return binds;
    }

    /// <summary>Steps the live camera, wrapping. The list is default-first
    /// then creation order, so stepping it is stepping the camera rail the
    /// user already reads.</summary>
    private void CycleCamera(int delta)
    {
        var cameras = _cameras.Cameras;
        if (cameras.Count < 2 || _cameras.LiveCamera is not { } live)
            return;
        int index = -1;
        for (int i = 0; i < cameras.Count; i++)
        {
            if (!ReferenceEquals(cameras[i], live))
                continue;
            index = i;
            break;
        }
        if (index < 0)
            return;
        int next = ((index + delta) % cameras.Count + cameras.Count)
            % cameras.Count;
        _cameras.SetLive(cameras[next]);
    }

    /// <summary>
    /// Settings-configured keybinds. They are edge-triggered, GPose-only, and
    /// suppressed while an ImGui text field owns the keyboard.
    /// </summary>
    private void HandleKeybinds()
    {
        // The acceptance gate blocks every ImGui path into the workspace;
        // chords are the one workspace input that does not travel through
        // ImGui, so they are gated here rather than by the modal.
        if (Views.FirstRunNoticeView.Pending
            || !_gPoseService.IsGPosing
            || ImGui.GetIO().WantTextInput)
        {
            foreach (var bind in _keybinds)
                bind.Down = false;
            return;
        }

        foreach (var bind in _keybinds)
        {
            // The SAME resolver the hover badges display, so a shown chord
            // always matches one that fires. The resolver hands back the
            // stored strings, so unchanged bindings compare equal and neither
            // chord is re-parsed.
            var slots = PoserKeybinds.Slots(bind.Name);
            bind.Sync(slots);

            bool active = ChordDown(bind.Primary) || ChordDown(bind.Secondary);
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

    private bool ChordDown(KeyChord chord)
    {
        if (!chord.IsBound)
            return false;
        if (chord.Ctrl != _keyState[VirtualKey.CONTROL])
            return false;
        if (chord.Shift != _keyState[VirtualKey.SHIFT])
            return false;
        if (chord.Alt != _keyState[VirtualKey.MENU])
            return false;
        return _keyState[chord.Key];
    }

    /// <summary>
    /// One configured keybind: the action id the resolver and the hover
    /// badges key on, the delegate it runs, and its TWO chords parsed —
    /// string work happens only when the configured text actually changes.
    ///
    /// <para>The edge is the action's, not the slot's: both chords are the
    /// same command, so holding one while tapping the other must not fire
    /// twice.</para>
    /// </summary>
    private sealed class Keybind(string name, Action run)
    {
        public string Name { get; } = name;
        public Action Run { get; } = run;
        public KeyChord Primary { get; private set; }
        public KeyChord Secondary { get; private set; }

        private string _primaryText = string.Empty;
        private string _secondaryText = string.Empty;

        /// <summary>Edge state: the action was down on the previous frame.</summary>
        public bool Down { get; set; }

        public void Sync(KeybindSlots slots)
        {
            if (!string.Equals(slots.Primary, _primaryText, StringComparison.Ordinal))
            {
                _primaryText = slots.Primary;
                Primary = KeyChord.Parse(slots.Primary);
            }
            if (!string.Equals(slots.Secondary, _secondaryText, StringComparison.Ordinal))
            {
                _secondaryText = slots.Secondary;
                Secondary = KeyChord.Parse(slots.Secondary);
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

    private void ApplyConfiguration()
    {
        ThemeSelection.Apply(
            _configService.Config.UI.Theme,
            _configService.Config.UI.AccentIndex);
        ApplyUiHidePolicy();
    }

    /// <summary>
    /// The four Dalamud hide flags Poser gets a say in, restated from config
    /// whenever it changes. Dalamud states them as DISABLE-the-hide, so each
    /// one is the negation of the setting the user reads: "Show in GPose" ON
    /// means the GPose hide is disabled. The automatic hide (cutscene, duty)
    /// and the user's own Scroll Lock hide are one decision here — a
    /// photographer hiding the HUD wants the same answer either way, and
    /// splitting them would be two rows nobody could tell apart.
    /// </summary>
    private void ApplyUiHidePolicy()
    {
        var ui = _configService.Config.UI;
        var builder = _pluginInterface.UiBuilder;
        builder.DisableGposeUiHide = ui.ShowInGPose;
        builder.DisableCutsceneUiHide = ui.ShowInCutscene;
        builder.DisableAutomaticUiHide = ui.ShowWhenGameUiHidden;
        builder.DisableUserUiHide = ui.ShowWhenGameUiHidden;
    }

    public void Dispose()
    {
        _eventBus.Unsubscribe<GPoseStateChangedEvent>(OnGPoseStateChanged);

        _windows.Main.OnSettingsRequested -= ToggleSettingsWindow;
        _windows.Main.OnSpawnBrowserRequested -= OpenSpawnBrowserAt;
        _poseFileSection.OnLibraryRequested -= OpenPoseLibrary;
        _configService.OnConfigurationChanged -= ApplyConfiguration;

        _pluginInterface.UiBuilder.Draw -= DrawUI;
        _pluginInterface.UiBuilder.OpenMainUi -= ToggleMainWindow;
    }
}
