using System;
using System.Numerics;
using Dalamud.Game.ClientState.Objects;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Graphics.Render;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using Poser.Entities;
using Poser.Files;
using Poser.Game.Bindings;
using Poser.Game.Posing;
using Poser.Services;

namespace Poser.Game.Preview;

/// <summary>
/// The pose library's live preview: the game's own inspect CharaView (index 1)
/// renders a hidden body into <c>RenderTargetManager.CharaViewTextures[1]</c>,
/// and selected pose files are applied to that body through the ordinary
/// import pipeline. Ported from Ktisis 0.4 <c>PreviewNode</c> — the init /
/// per-tick Update+Render / Release sequence and the camera calls are its
/// exact semantics.
/// </summary>
public sealed unsafe class PosePreviewService : IDisposable
{
    /// <summary>The slot the CharaView spawns its hidden body into. Outside
    /// the GPose scan range (201-439) on purpose: the preview must never
    /// appear in any actor list the user sees.</summary>
    public const ushort PreviewObjectIndex = 441;

    /// <summary>CharaView slot 1 — the one whose render target is
    /// <c>CharaViewTextures[1]</c>.</summary>
    private const uint CharaViewIndex = 1;

    /// <summary>Ktisis' preview node size, used until the texture reports
    /// its own dimensions.</summary>
    private static readonly Vector2 FallbackSize = new(192f, 320f);

    private readonly IFramework _framework;
    private readonly IObjectTable _objectTable;
    private readonly IActorManager _actors;
    private readonly StableBindingRegistry _bindings;
    private readonly CleanPoseFacade _poses;
    private readonly IGPoseService _gpose;
    private readonly IPluginLog _log;

    // Draw-thread requests, framework-thread consumption.
    private readonly object _gate = new();
    private nint _requestedSource;
    private string? _requestedPath;
    private PoseImportOptions? _requestedOptions;

    private volatile bool _open;
    private volatile bool _rendering;
    private volatile string? _statusText;
    private volatile bool _disposed;

    // Framework thread only.
    private bool _initialized;
    private nint _copiedSource;
    private uint _counter = 1;
    private string? _appliedPath;

    public PosePreviewService(
        IFramework framework,
        IObjectTable objectTable,
        IActorManager actors,
        StableBindingRegistry bindings,
        CleanPoseFacade poses,
        IGPoseService gpose,
        IPluginLog log)
    {
        _framework = framework;
        _objectTable = objectTable;
        _actors = actors;
        _bindings = bindings;
        _poses = poses;
        _gpose = gpose;
        _log = log;
    }

    /// <summary>The CharaView is initialized and rendering a body.</summary>
    public bool IsActive => _rendering;

    /// <summary>Null while the preview renders; otherwise a short reason to
    /// show in place of the image.</summary>
    public string? StatusText => _statusText;

    /// <summary>
    /// The shader resource view of <c>CharaViewTextures[1]</c>, or 0 when the
    /// preview is closed or the target does not exist yet. Fetched fresh on
    /// every read: the texture is the game's, never cached and never ref-held.
    /// </summary>
    public nint TextureHandle
    {
        get
        {
            if (!_open)
                return 0;
            var texture = CharaViewTexture();
            return texture == null ? 0 : (nint)texture->D3D11ShaderResourceView;
        }
    }

    public Vector2 TextureSize
    {
        get
        {
            var texture = _open ? CharaViewTexture() : null;
            if (texture == null || texture->ActualWidth == 0 || texture->ActualHeight == 0)
                return FallbackSize;
            return new Vector2(texture->ActualWidth, texture->ActualHeight);
        }
    }

    private static FFXIVClientStructs.FFXIV.Client.Graphics.Kernel.Texture* CharaViewTexture()
    {
        var manager = RenderTargetManager.Instance();
        return manager == null ? null : manager->CharaViewTextures[(int)CharaViewIndex].Value;
    }

    /// <summary>
    /// Opens the preview and copies <paramref name="appearanceSource"/>'s
    /// appearance onto the preview body. Idempotent; calling it again with a
    /// different source re-copies on the next framework tick.
    /// </summary>
    public void Open(IActor appearanceSource)
    {
        if (_disposed)
            return;
        if (appearanceSource.Address == nint.Zero)
        {
            _statusText = "Select an actor to preview.";
            return;
        }
        if (!_gpose.IsGPosing)
        {
            _statusText = "Enter GPose to preview.";
            return;
        }

        lock (_gate)
        {
            _requestedSource = appearanceSource.Address;
        }

        if (_open)
            return;

        _open = true;
        _statusText = "Preparing preview…";
        _actors.RegisterAuxiliary(PreviewObjectIndex, ActorKind.Preview);
        _framework.Update += OnFrameworkUpdate;
        RunOnFramework(InitializeCharaView);
    }

    /// <summary>
    /// The pose to show. Remembered until the preview body is bound, then
    /// applied; the latest call wins.
    /// </summary>
    public void ShowPose(string path, PoseImportOptions options)
    {
        lock (_gate)
        {
            _requestedPath = path;
            _requestedOptions = options;
        }
    }

    /// <summary>Ktisis' preview arrows: yaw only, in degrees per click.</summary>
    public void Rotate(float yawDelta) => RunOnFramework(() =>
    {
        if (!_initialized)
            return;
        var agent = AgentInspect.Instance();
        if (agent != null)
            agent->CharaView.SetCameraYawAndPitch(yawDelta, 0f);
    });

    public void ResetCamera() => RunOnFramework(() =>
    {
        if (!_initialized)
            return;
        var agent = AgentInspect.Instance();
        if (agent != null)
            agent->CharaView.ResetPositions();
    });

    /// <summary>Releases the CharaView and the auxiliary registration.
    /// Idempotent and safe from any thread.</summary>
    public void Close()
    {
        _open = false;
        _rendering = false;
        _statusText = null;
        lock (_gate)
        {
            _requestedSource = nint.Zero;
            _requestedPath = null;
            _requestedOptions = null;
        }

        // Unsubscribe eagerly rather than inside the framework hop: a hop that
        // never runs (unload with a dead pump) must not leave the tick wired.
        _framework.Update -= OnFrameworkUpdate;
        _actors.UnregisterAuxiliary(PreviewObjectIndex);
        RunOnFramework(ReleaseCharaView);
    }

    private void InitializeCharaView()
    {
        if (!_open || _initialized)
            return;

        var agent = AgentInspect.Instance();
        if (agent == null)
        {
            _statusText = "The inspect view is unavailable.";
            return;
        }

        // Ktisis PreviewNode:130-131,144 — initialize slot 1 against the
        // inspect agent, copy the source appearance, then prime the view with
        // its own character before the first Render.
        agent->CharaView.Initialize(&agent->AgentInterface, CharaViewIndex, 0);
        _initialized = true;
        _counter = 1;
        _copiedSource = nint.Zero;
        _appliedPath = null;
        CopyAppearance(agent);
        agent->CharaView.Update(_counter, agent->CharaView.GetCharacter());
    }

    private void ReleaseCharaView()
    {
        if (!_initialized)
            return;
        _initialized = false;
        _copiedSource = nint.Zero;
        _appliedPath = null;
        _counter = 1;
        var agent = AgentInspect.Instance();
        if (agent != null)
            agent->CharaView.Release();
    }

    private void CopyAppearance(AgentInspect* agent)
    {
        nint source;
        lock (_gate)
        {
            source = _requestedSource;
        }
        if (source == nint.Zero || source == _copiedSource)
            return;

        agent->CharaView.ModelData.CopyFromCharacter((Character*)source);
        _copiedSource = source;
        // A new body carries none of the previous pose.
        _appliedPath = null;
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        if (!_open)
            return;
        if (!_gpose.IsGPosing)
        {
            Close();
            return;
        }

        var agent = AgentInspect.Instance();
        if (agent == null)
        {
            _rendering = false;
            _statusText = "The inspect view is unavailable.";
            return;
        }

        if (!_initialized)
        {
            InitializeCharaView();
            if (!_initialized)
                return;
        }

        CopyAppearance(agent);

        var previewObject = _objectTable[PreviewObjectIndex];
        var previewAddress = previewObject?.Address ?? nint.Zero;
        // Ktisis PreviewNode:159-160 drives Update with the slot-441 body.
        // Before it exists the view's own character stands in, exactly as the
        // setup call does.
        var character = previewAddress != nint.Zero
            ? (Character*)previewAddress
            : agent->CharaView.GetCharacter();
        agent->CharaView.Update(_counter, character);
        agent->CharaView.Render(_counter++);

        _rendering = previewAddress != nint.Zero;
        _statusText = _rendering ? null : "Preparing preview…";

        if (previewAddress != nint.Zero)
            TryApplyPendingPose(previewAddress);
    }

    /// <summary>
    /// The pending pose lands as soon as the preview body has an auxiliary
    /// actor AND a stable binding — the import engine resolves every target
    /// through the binding registry, whose refresh runs on its own cadence.
    /// Until then, and after any failure, the LATEST requested path is retried
    /// next tick.
    /// </summary>
    private void TryApplyPendingPose(nint previewAddress)
    {
        string? path;
        PoseImportOptions? options;
        lock (_gate)
        {
            path = _requestedPath;
            options = _requestedOptions;
        }
        if (path == null || options == null)
            return;
        if (string.Equals(path, _appliedPath, StringComparison.Ordinal))
            return;

        IActor? actor = null;
        var auxiliary = _actors.AuxiliaryActors;
        for (var i = 0; i < auxiliary.Count; i++)
        {
            if (auxiliary[i].Address == previewAddress)
            {
                actor = auxiliary[i];
                break;
            }
        }
        if (actor == null || _bindings.GetActorId(actor) == null)
            return;

        var result = _poses.ImportPose(actor, path, options);
        if (!result.Success)
            return;
        _appliedPath = path;
    }

    private void RunOnFramework(Action action)
    {
        if (_framework.IsInFrameworkUpdateThread)
        {
            action();
            return;
        }
        try
        {
            _ = _framework.RunOnFrameworkThread(action);
        }
        catch (Exception ex)
        {
            // An unreachable pump at shutdown: the CharaView dies with the
            // process either way.
            _log.Debug($"Pose preview could not reach the framework thread: {ex.Message}");
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        Close();
    }
}
