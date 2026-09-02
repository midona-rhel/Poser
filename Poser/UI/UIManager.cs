using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Keys;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Poser.Application.Animation;
using Poser.Application.Scene;
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
    private readonly SceneSession _scene;
    private readonly AnimationSceneActions _sceneActions;
    private readonly Dalamud.Plugin.Services.IPluginLog _log;
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
        SceneSession scene,
        AnimationSceneActions sceneActions,
        Dalamud.Plugin.Services.IPluginLog log)
    {
        _log = log;
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
        _scene = scene;
        _sceneActions = sceneActions;

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
            _windows.CloseAll();
        }
    }

    private void DrawUI()
    {
        if (!Crystarium.AdvanceTheme())
            return;

        // Cache warming runs before primary windows can draw. Scoped so the
        // ledger can see it: the un-attributed spikes lived exactly here.
        using (FrameProfiler.Scope("Shell · icon pump"))
            Crystarium.PumpStartupIcons(_configService.Config.Library.IconSize);
        bool previewBackingReady;
        using (FrameProfiler.Scope("Shell · preview backing"))
            previewBackingReady = !_windows.IsPrimaryOpen
                || _poseFileSection.PrewarmPreviewBacking();
        _windows.AdvancePrimaryOpen(previewBackingReady);
        // THE frame boundary for the draw-cost ledger: everything Poser draws
        // is drawn from here, so this is the only place that can answer for a
        // whole frame. The close is unconditional — a window that threw must
        // not leave the ledger open across frames.
        FrameProfiler.BeginFrame();
        global::Poser.UI.Crystarium.BeginTextFrame();
        try
        {
            Interactive.BeginFrame();
            _windows.System.Draw();
            using (FrameProfiler.Scope("Shell · reference images"))
                _windows.PumpReferenceImages();
            using (FrameProfiler.Scope("Shell · floating menus"))
                Crystarium.FloatingMenu.EndFrame();
            using (FrameProfiler.Scope("Shell · hover help"))
                Crystarium.HoverHelp.Render();
            using (FrameProfiler.Scope("Shell · interactive end"))
                Interactive.EndFrame();
        }
        finally
        {
            FrameProfiler.EndFrame();
        }
        // Hide-while-manipulating (#77): the windows fade down while a
        // world drag is HELD — hover never hides — so the scene is clear
        // under the gesture.
        bool held = Controls.ManipulationDrag.Held;
        bool shellHeld = Controls.ManipulationDrag.ShellHeld;
        bool active = _configService.Config.UI.HideWhileManipulating
            && (held || shellHeld);
        if (active != Controls.ManipulationHide.Active)
            _log.Debug(
                $"[ManipulationHide] active={active} held={held} shell={shellHeld} "
                + $"opacity={Controls.ManipulationHide.Opacity:0.00} frame={ImGui.GetFrameCount()}");
        Controls.ManipulationHide.Active = active;
        Controls.ManipulationHide.HideGizmo =
            _configService.Config.UI.HideGizmoWhileManipulating;
        Controls.ManipulationHide.Advance();
        // The focus rule, published once per frame: typing, an active
        // ImGui item (a drag in the UI), or a live gizmo gesture owns the
        // keyboard, and the free camera's flight keys stand down.
        _cameras.SuppressFlightKeys =
            ImGui.GetIO().WantTextInput
            || ImGui.IsAnyItemActive()
            || Controls.GizmoPointerOwnership.Owned;
        HandleKeybinds();
    }

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
            ["Deselect"] = () => _scene.Selection.Clear(),
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

    private void HandleKeybinds()
    {
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
            var slots = PoserKeybinds.Slots(bind.Name);
            bind.Sync(slots);

            bool active = ChordDown(bind.Primary) || ChordDown(bind.Secondary);
            if (active && !bind.Down)
            {
                bind.Down = true;
                bind.Run();
                // A fired chord is CONSUMED: the key comes off the game's
                // state so the game does not also act on it — our binds
                // are ours (the setter only accepts false, which is
                // exactly the suppression Dalamud offers).
                var fired = ChordDown(bind.Primary)
                    ? bind.Primary
                    : bind.Secondary;
                if (fired.IsBound
                    && _keyState.IsVirtualKeyValid(fired.Key))
                    _keyState[fired.Key] = false;
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

    private sealed class Keybind(string name, Action run)
    {
        public string Name { get; } = name;
        public Action Run { get; } = run;
        public KeyChord Primary { get; private set; }
        public KeyChord Secondary { get; private set; }

        private string _primaryText = string.Empty;
        private string _secondaryText = string.Empty;

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
        => _windows.SetPrimaryOpen(!_windows.IsPrimaryOpen);

    private void ToggleSettingsWindow()
        => _windows.Settings.IsOpen = !_windows.Settings.IsOpen;

    private void OpenSpawnBrowserAt(
        System.Numerics.Vector2 anchor, Views.SpawnBrowserTab tab)
        => _windows.SpawnBrowser.OpenAt(anchor, tab);

    private void OpenPoseLibrary()
    {
        _windows.SetPrimaryOpen(true);
        _windows.Main.ShowLibrary();
    }

    private void ApplyConfiguration()
    {
        ThemeSelection.Apply(
            _configService.Config.UI.Theme,
            _configService.Config.UI.AccentIndex);
        ApplyUiHidePolicy();
    }

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
