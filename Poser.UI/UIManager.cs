using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Keys;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Poser.Application.Animation;
using Poser.Application.Scene;
using Poser.UI.Composition;
using System;
using System.Collections.Generic;
using System.Linq;
using Poser.Application.Transforms;
using Poser.UI.Widgets;
using Poser.Domain.Preferences;
using Poser.Documents.Config;
using Poser.Application.Events;
using Poser.Application.Lifecycle;
using Poser.Application.Settings;
using static Poser.UI.Widgets.TextWidgets;
using static Poser.UI.Widgets.Themes;
using static Poser.UI.Widgets.ValueEdits;

namespace Poser.UI;

public sealed class UIManager : IUIManager
{
    private readonly IDalamudPluginInterface _pluginInterface;
    private readonly IGPoseService _gPoseService;
    private readonly IEventBus _eventBus;
    private readonly ITransformFacade _cleanTransforms;
    private readonly UserNotices _notices;
    private readonly IKeyState _keyState;
    private readonly global::Poser.Application.Input.IKeyEvents _keyEvents;
    private readonly global::Poser.Application.Transforms.ValueJournal _values;
    private readonly EditorState _editorState;
    private readonly ConfigurationService _configService;
    private readonly UiWindowSet _windows;
    private readonly PoseFileInspectorSection _poseFileSection;
    private readonly Application.Input.CameraInputState _cameraInput;
    private readonly Application.Presentation.ICameraControl _cameraControl;
    private readonly SceneSession _scene;
    private readonly AnimationSceneActions _sceneActions;
    private readonly IAnimationPlayback _animation;
    private readonly Dalamud.Plugin.Services.IPluginLog _log;
    private readonly Keybind[] _keybinds;
    private bool _uiHidden;
    private bool _capturingShortcutThisFrame;

    private readonly UiContext _ui;
    private readonly FrameProfiler _profiler;
    private readonly Controls.ManipulationState _manipulation;
    private readonly global::Poser.Application.Diagnostics.ActionRecorder _recorder;
    private readonly Controls.IssueReportModal _issueReport;
    private readonly Views.ReleaseNotesView _releaseNotes;

    public UIManager(
        UiContext ui,
        FrameProfiler profiler,
        Controls.ManipulationState manipulation,
        global::Poser.Application.Diagnostics.ActionRecorder recorder,
        Controls.IssueReportModal issueReport,
        Views.ReleaseNotesView releaseNotes,
        IDalamudPluginInterface pluginInterface,
        IGPoseService gPoseService,
        IEventBus eventBus,
        ITransformFacade cleanTransforms,
        UserNotices notices,
        IKeyState keyState,
        global::Poser.Application.Input.IKeyEvents keyEvents,
        global::Poser.Application.Transforms.ValueJournal values,
        EditorState editorState,
        ConfigurationService configService,
        UiWindowSet windows,
        PropertiesContent properties,
        Application.Input.CameraInputState cameraInput,
        Application.Presentation.ICameraControl cameraControl,
        SceneSession scene,
        AnimationSceneActions sceneActions,
        IAnimationPlayback animation,
        Dalamud.Plugin.Services.IPluginLog log,
        Dalamud.Plugin.Services.IGameGui gameGui,
        ActionGate gate)
    {
        _gate = gate;
        _gameGui = gameGui;
        _ui = ui;
        _profiler = profiler;
        _manipulation = manipulation;
        _recorder = recorder;
        _issueReport = issueReport;
        _releaseNotes = releaseNotes;
        _log = log;
        _pluginInterface = pluginInterface;
        _gPoseService = gPoseService;
        _eventBus = eventBus;
        _cleanTransforms = cleanTransforms;
        _notices = notices;
        _keyState = keyState;
        _editorState = editorState;
        _configService = configService;
        _windows = windows;
        _poseFileSection = properties.PoseFiles;
        _cameraInput = cameraInput;
        _cameraControl = cameraControl;
        _scene = scene;
        _sceneActions = sceneActions;
        _animation = animation;

        _keybinds = BuildKeybinds();
        _keyEvents = keyEvents;
        _values = values;
        using var startup = new global::Poser.Application.Lifecycle.StartupCleanup(
            error => log.Error(error, "UI activation cleanup failed"));
        startup.OnFailure(Dispose);
        _keyEvents.KeyEvent += OnKeyEvent;
        // Releasing a drag or confirming a typed value closes the current
        // undo step, so each edit to a control is one undo step, press to release.
        _ui.ValueCommitted += _values.CommitEdit;
        _ui.ValueEditBegan += _values.BeginEdit;
        _ui.ValueEditEnded += _values.EndEdit;
        _ui.ValueEditingIdle += _values.Seal;

        _windows.Main.OnSettingsRequested += ToggleSettingsWindow;
        _windows.Main.OnSkeletonSettingsRequested += OpenSkeletonSettings;
        _windows.Main.OnLibrarySettingsRequested += OpenLibrarySettings;
        _windows.Main.OnSpawnBrowserRequested += OpenSpawnBrowserAt;
        _poseFileSection.OnLibraryRequested += OpenActorPoseLibrary;
        _configService.OnConfigurationChanged += ApplyConfiguration;

        _pluginInterface.UiBuilder.Draw += DrawUI;
        _pluginInterface.UiBuilder.OpenMainUi += ToggleMainWindow;
        ApplyUiHidePolicy();

        _eventBus.Subscribe<GPoseStateChangedEvent>(OnGPoseStateChanged);
        startup.Complete();
    }

    private void OnGPoseStateChanged(GPoseStateChangedEvent e)
    {
        var config = _configService.Config;
        if (e.IsGPosing)
        {
            if (config.OpenOnGPoseEnter)
                _windows.SetPrimaryOpen(true);
        }
        else
        {
            // Reference pictures belong to the GPose session, so they close
            // with it whether or not the windows do.
            _windows.CloseReferenceImages();
            _windows.CloseProperties();
            if (config.CloseWithGPose)
                _windows.CloseAll();
        }
    }

    private readonly Dalamud.Plugin.Services.IGameGui _gameGui;
    private readonly ActionGate _gate;

    /// <summary>True when Poser's windows are hidden: Poser itself was hidden, or the
    /// game's interface is hidden and Poser is not set to show then. Drawing still
    /// runs while hidden, so shortcuts such as undo and redo still work.</summary>
    private bool WindowsHidden =>
        _uiHidden || (_gameGui.GameUiHidden && !_configService.Config.UI.ShowWhenGameUiHidden);

    private void DrawUI()
    {
        bool hidden = WindowsHidden;
        _capturingShortcutThisFrame = _windows.Settings.IsRebindingShortcut;
        _cameraInput.PointerDragHeld = ImGui.IsMouseDown(ImGuiMouseButton.Left);
        _windows.PumpPropertiesInteractions(_cameraInput.PointerDragHeld && !hidden);
        if (!AdvanceTheme())
            return;

        // Load icons before the main windows draw, timed separately in the profiler.
        using (_profiler.Scope("Shell · icon pump"))
            _ui.Icons.PumpStartupIcons(_configService.Config.Library.IconSize);
        bool previewBackingReady;
        using (_profiler.Scope("Shell · preview backing"))
            previewBackingReady = !_windows.IsPrimaryOpen
                || _poseFileSection.PrewarmPreviewBacking();
        _windows.AdvancePrimaryOpen(previewBackingReady);
        // Measure window drawing and always close the profiler frame,
        // including when a window throws.
        _profiler.BeginFrame();
        BeginTextFrame();
        try
        {
            Interactive.BeginFrame();
            // While an action runs, the controls start nothing new. A drag only holds the
            // shortcuts, since the mouse is busy with it. A long action also greys them out.
            var running = _gate.Running;
            Interactive.SetBusy(
                running is { Kind: not ActionKind.Drag }, running is { Kind: ActionKind.Long });
            try
            {
                try
                {
                    if (!hidden)
                        _windows.System.Draw();
                }
                catch (Exception ex)
                {
                    // Recorded for the issue report, then thrown again, because
                    // Dalamud decides what happens to a frame that failed.
                    _recorder.Exception(ex);
                    throw;
                }
                if (!hidden)
                {
                    using (_profiler.Scope("Shell · reference images"))
                        _windows.PumpReferenceImages();
                    // The report dialog is a popup, not a window.
                    _issueReport.Draw();
                    _releaseNotes.Draw();
                }
                using (_profiler.Scope("Shell · floating menus"))
                    FloatingMenu.EndFrame();
            }
            finally { Interactive.EndBusy(); }
            using (_profiler.Scope("Shell · hover help"))
                if (!hidden)
                    HoverHelp.Render();
            using (_profiler.Scope("Shell · interactive end"))
                Interactive.EndFrame();
        }
        finally
        {
            EndValueFrame();
            _profiler.EndFrame();
        }
        // The windows fade out while something in the scene is being
        // dragged, so the scene is clear to see. Only a held drag hides them;
        // hovering does not.
        bool held = _manipulation.DragHeld;
        bool shellHeld = _manipulation.ShellDragHeld;
        bool active = _configService.Config.UI.HideWhileManipulating
            && (held || shellHeld);
        // Hide while the camera moves: the free camera's own input, or a
        // drag that began on empty space and turns whatever camera is live.
        if (_configService.Config.UI.HideWhileMovingCamera
            && (_cameraInput.FlightActive || EmptySpaceDrag()))
            active = true;
        _manipulation.HideActive = active;
        _manipulation.HideGizmo =
            _configService.Config.UI.HideGizmoWhileManipulating;
        _manipulation.AdvanceHide();
        // Typing suppresses camera flight keys; hovering or dragging UI does not.
        _cameraInput.TextInputActive = ImGui.GetIO().WantTextInput;
        HandleKeybinds();
    }

    /// <summary>A drag with either mouse button that started where there was
    /// no window or handle, and has moved since. The game turns its camera
    /// with such a drag.</summary>
    private readonly bool[] _emptyPress = new bool[2];
    private readonly System.Numerics.Vector2[] _pressAt = new System.Numerics.Vector2[2];
    private bool EmptySpaceDrag()
    {
        bool dragging = false;
        var io = ImGui.GetIO();
        for (int button = 0; button < 2; button++)
        {
            var which = (ImGuiMouseButton)button;
            if (ImGui.IsMouseClicked(which))
            {
                _emptyPress[button] = !io.WantCaptureMouse
                    && !_manipulation.PointerOwned;
                _pressAt[button] = io.MousePos;
            }
            if (!ImGui.IsMouseDown(which))
                _emptyPress[button] = false;
            if (_emptyPress[button]
                && (io.MousePos - _pressAt[button]).LengthSquared() > 9f)
                dragging = true;
        }
        return dragging;
    }

    private void SelectedHistory(bool before)
    {
        // Undo history reports its own failures, including ones that come
        // later. Report a failure here only if it was not already reported
        // during this action.
        bool reported = false;
        void Posted(string kind, string message) => reported = true;
        _notices.Posted += Posted;
        try
        {
            var result = before ? _cleanTransforms.UndoSelected() : _cleanTransforms.RedoSelected();
            if (!reported)
                _notices.Report(result.Outcome, before ? "Undo selected entity" : "Redo selected entity", refusal: true);
        }
        finally { _notices.Posted -= Posted; }
    }

    private Keybind[] BuildKeybinds()
    {
        var handlers = new Dictionary<string, Action>(StringComparer.Ordinal)
        {
            ["Undo"] = () =>
            {
                _values.Seal();
                if (_cleanTransforms.CanUndo)
                    _cleanTransforms.Undo();
            },
            ["Redo"] = () =>
            {
                _values.Seal();
                if (_cleanTransforms.CanRedo)
                    _cleanTransforms.Redo();
            },
            ["Deselect"] = () => _scene.Selection.Clear(),
            ["Undo selected entity"] = () => SelectedHistory(before: true),
            ["Redo selected entity"] = () => SelectedHistory(before: false),
            ["Import pose"] = () => PoseShortcut(
                actor => _poseFileSection.RequestImportMenu(withPresets: true, target: actor), opensWindow: true),
            ["Import pose from file"] = () => PoseShortcut(_poseFileSection.OpenImportFromFile, opensWindow: true),
            ["Export pose"] = () => PoseShortcut(actor => _poseFileSection.RequestExportMenu(actor), opensWindow: true),
            ["Export pose to file"] = () => PoseShortcut(_poseFileSection.OpenExport, opensWindow: true),
            ["Copy pose"] = () => PoseShortcut(_poseFileSection.CopyToClipboard),
            ["Paste pose"] = () => PoseShortcut(_poseFileSection.ImportFromClipboard),
            ["Play / pause actor"] = () => PoseShortcut(actor =>
            {
                if (!_animation.IsSupported(actor)) return;
                if (_animation.AnyPlaying(actor)) _animation.Pause(actor);
                else _animation.Resume(actor);
            }, requiresPose: false),
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

    private void CycleCamera(int delta) => _cameraControl.Cycle(delta);

    private void PoseShortcut(Action<global::Poser.Domain.Identity.ActorId> action,
        bool opensWindow = false, bool requiresPose = true)
    {
        // Shortcuts act on the main window's selection, never on the Library
        // or on whichever pop-out Properties window drew last.
        if (_scene.Selection.PrimaryActor is not { } actor
            || !_scene.Selection.Selected.All(id => id.OwningActor == actor)
            || !_scene.Snapshot.Actors.Any(item => item.Id == actor)
            || (requiresPose && !_poseFileSection.HasPosableTarget(actor)))
            return;
        if (opensWindow)
        {
            _uiHidden = false;
            _windows.SetPrimaryOpen(true); // The main shell pumps these dialogs.
        }
        action(actor);
    }

    /// <summary>A shortcut does nothing while an action is running, a held drag included.</summary>
    private void RunShortcut(Keybind bind)
    {
        if (_gate.Running == null)
            bind.Run();
    }

    private void HandleKeybinds()
    {
        if (ShortcutsSuppressed)
        {
            foreach (var bind in _keybinds)
            {
                bind.Sync(PoserKeybinds.Slots(_configService.Config.UI, bind.Name));
                // Keys still held from recording a shortcut or typing text do
                // not count as a new press when shortcuts work again. The same
                // applies when keys are polled because the key hook is missing.
                bind.Down = ChordDown(bind.Primary) || ChordDown(bind.Secondary);
                bind.Queued = false;
            }
            return;
        }

        foreach (var bind in _keybinds)
        {
            var slots = PoserKeybinds.Slots(_configService.Config.UI, bind.Name);
            bind.Sync(slots);
            if (_keyEvents.Available)
            {
                // The key hook already took the key from the game; the
                // shortcut itself runs here, during drawing, like every other
                // shortcut.
                if (bind.Queued)
                {
                    bind.Queued = false;
                    RunShortcut(bind);
                }
                continue;
            }

            bool active = ChordDown(bind.Primary) || ChordDown(bind.Secondary);
            if (active && !bind.Down)
            {
                bind.Down = true;
                RunShortcut(bind);
                // A shortcut that ran takes its key away from the game, so
                // the game does not act on it too. Dalamud only lets a key be
                // set to not pressed, which is exactly what this needs.
                var fired = ChordDown(bind.Primary)
                    ? bind.Primary
                    : bind.Secondary;
                if (fired.IsBound
                    && _keyState.IsVirtualKeyValid((int)fired.Key))
                    _keyState[(int)fired.Key] = false;
            }
            else if (!active)
            {
                bind.Down = false;
            }
        }
    }

    /// <summary>Matches a key press from the game against the shortcuts, using the
    /// modifier keys held right now. A match is queued to run during drawing.
    /// ProcessShortcutKey decides whether the game also gets the key.</summary>
    private bool OnKeyEvent(Domain.Preferences.KeyCode code, global::Poser.Application.Input.KeyEventKind kind)
        => ProcessShortcutKey(new KeyChord(_keyState[VirtualKey.CONTROL],
            _keyState[VirtualKey.SHIFT], _keyState[VirtualKey.MENU], code), kind);

    private bool ShortcutsSuppressed => !_gPoseService.IsGPosing
        || ImGui.GetIO().WantTextInput || _windows.Settings.IsRebindingShortcut
        || _capturingShortcutThisFrame;

    private bool ProcessShortcutKey(KeyChord chord, global::Poser.Application.Input.KeyEventKind kind)
    {
        var key = (VirtualKey)chord.Key;
        if (ShortcutsSuppressed)
            return false;
        bool handled = false;
        // When disabled, bindings still run but the game also gets the key.
        // Always pass Escape and Enter through for leaving GPose and dialogs.
        bool keep = _configService.Config.Camera.KeepBoundKeysFromGame
            && key != VirtualKey.ESCAPE && key != VirtualKey.RETURN;
        foreach (var bind in _keybinds)
        {
            bind.Sync(PoserKeybinds.Slots(_configService.Config.UI, bind.Name));
            if (!chord.IsBound || (bind.Primary != chord && bind.Secondary != chord))
                continue;
            switch (kind)
            {
                case global::Poser.Application.Input.KeyEventKind.Down:
                    if (!bind.Down)
                    {
                        bind.Down = true;
                        bind.Queued = true;
                    }
                    handled = true;
                    break;
                case global::Poser.Application.Input.KeyEventKind.Held:
                    handled = true;
                    break;
                case global::Poser.Application.Input.KeyEventKind.Released:
                    if (bind.Down)
                    {
                        bind.Down = false;
                        handled = true;
                    }
                    break;
            }
        }
        return handled && keep;
    }

#if DEBUG
    // Test input for debugging. It goes through the same shortcut matching and
    // runs at the same point during drawing, but sends no keys to the game or desktop.
    public object DebugShortcut(string chord, global::Poser.Application.Input.KeyEventKind kind)
    {
        bool suppressed = ShortcutsSuppressed;
        bool consumed = ProcessShortcutKey(KeyChord.Parse(chord), kind);
        return new { suppressed, consumed,
            queued = _keybinds.Where(bind => bind.Queued).Select(bind => bind.Name).ToArray() };
    }
#endif

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
        return _keyState[(VirtualKey)(int)chord.Key];
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

        /// <summary>The hook took the press; the next draw frame runs it.</summary>
        public bool Queued { get; set; }

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

    // Hiding is not closing: each window stays open underneath, Settings
    // keeps its unsaved changes, and nothing that runs on opening is triggered.
    private void ToggleAllUi() => _uiHidden = !_uiHidden;

    public void ToggleMainWindow()
        => _windows.SetPrimaryOpen(!_windows.IsPrimaryOpen);

    private void ToggleSettingsWindow()
        => _windows.Settings.IsOpen = !_windows.Settings.IsOpen;

    private void OpenLibrarySettings() => _windows.Settings.OpenLibrary();
    private void OpenSkeletonSettings() => _windows.Settings.OpenSkeleton();

    private void OpenSpawnBrowserAt(
        System.Numerics.Vector2 anchor, Views.SpawnBrowserTab tab)
        => _windows.SpawnBrowser.OpenAt(anchor, tab);

    private void OpenPoseLibrary()
    {
        _windows.SetPrimaryOpen(true);
        _windows.Main.ShowLibrary();
    }

    private void OpenActorPoseLibrary(Poser.Domain.Identity.ActorId? actor)
    {
        _windows.SetPrimaryOpen(true);
        _windows.Main.ShowActorLibrary(actor);
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
        // Poser hides its own windows when the game's interface is hidden
        // (WindowsHidden), because drawing must keep running for shortcuts to work.
        builder.DisableUserUiHide = true;
    }

    public void Dispose()
    {
        _eventBus.Unsubscribe<GPoseStateChangedEvent>(OnGPoseStateChanged);
        _keyEvents.KeyEvent -= OnKeyEvent;
        _ui.ValueCommitted -= _values.CommitEdit;
        _ui.ValueEditBegan -= _values.BeginEdit;
        _ui.ValueEditEnded -= _values.EndEdit;
        _ui.ValueEditingIdle -= _values.Seal;

        _windows.Main.OnSettingsRequested -= ToggleSettingsWindow;
        _windows.Main.OnSkeletonSettingsRequested -= OpenSkeletonSettings;
        _windows.Main.OnLibrarySettingsRequested -= OpenLibrarySettings;
        _windows.Main.OnSpawnBrowserRequested -= OpenSpawnBrowserAt;
        _poseFileSection.OnLibraryRequested -= OpenActorPoseLibrary;
        _configService.OnConfigurationChanged -= ApplyConfiguration;

        _pluginInterface.UiBuilder.Draw -= DrawUI;
        _pluginInterface.UiBuilder.OpenMainUi -= ToggleMainWindow;
        // The shared UI state is disposed elsewhere, not here. Closing the panes
        // still closes menus through it after drawing stops, so it must live until then.
    }
}
