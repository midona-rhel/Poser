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
    private readonly SceneSession _scene;
    private readonly AnimationSceneActions _sceneActions;
    private readonly IFramework _framework;
    private readonly KeybindDispatcher _keybinds;
    private readonly HashSet<VirtualKey> _pollableKeys;
    private List<Dalamud.Interface.Windowing.IWindow>? _hiddenWindows;

    // The draw pass's answer to "is an ImGui text field eating the keyboard",
    // stamped with the tick it was taken on. The pump runs on the framework
    // tick and must not touch ImGui, and a stamp that stops being refreshed
    // means the UI stopped drawing — in which case nothing is being typed and
    // the suppression has to lapse rather than latch. See HandleKeybinds.
    private long _tick;
    private long _textInputTick = long.MinValue;
    private bool _textInput;

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
        IFramework framework)
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
        _scene = scene;
        _sceneActions = sceneActions;
        _framework = framework;

        // Bound ONCE, in registry order: the delegates and their parsed
        // chords are the whole per-frame keybind state, so a frame that fires
        // nothing allocates nothing. Every registered action is bound here —
        // the registry is the list, and an id it names with no handler is a
        // build-time hole, which is why the lookup throws rather than
        // skipping.
        _keybinds = new KeybindDispatcher(BuildKeybinds());

        // Dalamud's key state answers for the keys the GAME maps and throws
        // for every other one. The chord vocabulary is wider than that map —
        // the keypad and the OEM punctuation the Ktisis preset binds are not
        // guaranteed to be in it — and a throw from inside the pump would
        // take EVERY action down, silently, for the rest of the session. The
        // supported set is read once and an unsupported key simply reads up.
        _pollableKeys = [.. keyState.GetValidVirtualKeys()];

        _windows.Main.OnSettingsRequested += ToggleSettingsWindow;
        _windows.Main.OnSpawnBrowserRequested += OpenSpawnBrowserAt;
        _poseFileSection.OnLibraryRequested += OpenPoseLibrary;
        _configService.OnConfigurationChanged += ApplyConfiguration;

        _pluginInterface.UiBuilder.Draw += DrawUI;
        _pluginInterface.UiBuilder.OpenMainUi += ToggleMainWindow;
        // Chords are polled on the TICK, never from the draw pass — see
        // HandleKeybinds.
        _framework.Update += OnFrameworkUpdate;
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
        // The draw pass's ONLY keybind duty: report whether an ImGui text
        // field is eating the keyboard this frame. The chords themselves are
        // pumped from the tick.
        _textInput = ImGui.GetIO().WantTextInput;
        _textInputTick = _tick;
    }

    /// <summary>
    /// The delegate behind every registered action. Each one is a call the UI
    /// already makes from a button, a menu row or a strip — a chord binds an
    /// existing command, it never becomes the only way to reach one.
    /// </summary>
    private List<KeyValuePair<string, Action>> BuildKeybinds()
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
            // What Escape means now that the workspace no longer closes on it:
            // both references answer Escape with a deselect (Brio's Posing_Esc,
            // Ktisis's Select_None), and a clear on an empty selection is a
            // no-op, so the chord needs no gate of its own.
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

        var binds = new List<KeyValuePair<string, Action>>(
            KeybindRegistry.Actions.Count);
        foreach (var action in KeybindRegistry.Actions)
            binds.Add(new(action.Id, handlers[action.Id]));
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

    private void OnFrameworkUpdate(IFramework framework)
    {
        _tick++;
        HandleKeybinds();
    }

    /// <summary>
    /// Settings-configured keybinds. They are edge-triggered, GPose-only, and
    /// suppressed while an ImGui text field owns the keyboard.
    ///
    /// <para>Pumped from the FRAMEWORK TICK, never from the draw pass. Dalamud
    /// raises a plugin's draw callback only while that plugin's UI is
    /// drawable, and Poser leaves the user and automatic hides enabled by
    /// default ("Show Poser when the game UI is hidden" is off): polling
    /// chords from the end of the draw pass meant every chord went silent the
    /// moment the photographer hid the HUD, and any window that threw before
    /// the poll was reached silenced them for that frame too. Input does not
    /// depend on drawing, so it is not polled from a draw (user 2026-08-15).
    /// </para>
    /// </summary>
    private void HandleKeybinds()
    {
        // The acceptance gate blocks every ImGui path into the workspace;
        // chords are the one workspace input that does not travel through
        // ImGui, so they are gated here rather than by the modal.
        if (Views.FirstRunNoticeView.Pending
            || !_gPoseService.IsGPosing
            || TextInputActive())
        {
            _keybinds.Suspend();
            return;
        }

        // The SAME resolver the hover badges display, so a shown chord always
        // matches one that fires. The resolver hands back the stored strings,
        // so unchanged bindings compare equal and neither chord is re-parsed.
        _keybinds.Pump(PoserKeybinds.Slots, KeyDown);
    }

    /// <summary>The draw pass's text-input answer, and only while it is fresh:
    /// a stamp older than a tick means the UI is not drawing at all, so no
    /// field can be typing into and the suppression must not latch.</summary>
    private bool TextInputActive() => _textInput && _tick - _textInputTick <= 1;

    /// <summary>A key the game does not map reads UP rather than throwing —
    /// see the supported-set read in the constructor.</summary>
    private bool KeyDown(VirtualKey key) =>
        _pollableKeys.Contains(key) && _keyState[key];

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

    /// <summary>
    /// The workspace and everything that belongs to a session go up and down
    /// TOGETHER, which is what <see cref="UiWindowSet.SetPrimaryOpen"/> is
    /// for. Flipping <c>Main.IsOpen</c> here directly opened the shell and
    /// nothing else: the skeleton overlay's flag is written in exactly one
    /// place, so a workspace opened from the command, the launcher entry or
    /// the chord came up with no bone dots, no gizmo and no world-adoption
    /// handles at all — the sidebar could mark a world class and nothing in
    /// the viewport could answer, because the window that draws the answers
    /// was never opened (user 2026-08-15).
    /// </summary>
    public void ToggleMainWindow()
        => _windows.SetPrimaryOpen(!_windows.Main.IsOpen);

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
        // Through the same seat as every other opener, for the same reason:
        // the library is workspace content, and a workspace raised without
        // its session windows is a shell with a dead viewport.
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

        _framework.Update -= OnFrameworkUpdate;
        _pluginInterface.UiBuilder.Draw -= DrawUI;
        _pluginInterface.UiBuilder.OpenMainUi -= ToggleMainWindow;
    }
}
