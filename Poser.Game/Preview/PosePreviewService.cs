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

    /// <summary>How far the body may be offset from its staged position, in
    /// native units either way. A whole body is about 1.8 tall, so this is
    /// already past both ends of it — the ceiling exists so a held button
    /// cannot walk the body out of the render entirely.</summary>
    private const float MaxViewPan = 2.0f;

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
    private PoseImportOptions? _appliedOptions;
    private float _viewPanY;
    private Vector3? _panBasePosition;
    private nint _panBaseObject;

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
    /// applied; the latest call wins. The OPTIONS INSTANCE is part of the
    /// request: restating the same path with the same instance is free, while
    /// a new instance re-imports — that is how an import-option change reaches
    /// a preview whose path never moved.
    /// </summary>
    public void ShowPose(string path, PoseImportOptions options)
    {
        lock (_gate)
        {
            _requestedPath = path;
            _requestedOptions = options;
        }
    }

    /// <summary>
    /// Ktisis' preview arrows, in degrees per call — both axes of the member,
    /// each a DELTA (Ktisis' 50/click, and HaselTweaks' PortraitHelper drags
    /// the banner editor's CharaView through the same call the same way).
    /// </summary>
    public void Rotate(float yawDelta, float pitchDelta = 0f) =>
        RunOnFramework(() =>
        {
            if (!_initialized)
                return;
            var agent = AgentInspect.Instance();
            if (agent != null)
                agent->CharaView.SetCameraYawAndPitch(yawDelta, pitchDelta);
        });

    /// <summary>
    /// Dolly — a DELTA, like every other camera call here. HaselTweaks'
    /// PortraitHelper drives the banner editor's CharaView with
    /// <c>SetCameraDistance(100 * dDistance)</c> against a native distance that
    /// spans about 0.5 to 2.0, so a visible click is worth whole units, not
    /// fractions. The sign is still the caller's assumption: closer is taken to
    /// be the smaller distance, hence negative.
    /// </summary>
    public void Zoom(float distanceDelta) => RunOnFramework(() =>
    {
        if (!_initialized)
            return;
        var agent = AgentInspect.Instance();
        if (agent != null)
            agent->CharaView.SetCameraDistance(distanceDelta);
    });

    /// <summary>
    /// Framing — a DELTA in NATIVE world units, positive carrying the view DOWN
    /// the body.
    ///
    /// The camera does not move: <c>CharaView.SetCameraXAndY</c> is a
    /// <c>[VirtualFunction]</c> (slot 6) and the INSPECT view's vtable leaves it
    /// a no-op — tested in game at 20 and at 75 with identical results, while
    /// slots 4 and 5 (distance, yaw/pitch) on the same object work. That matches
    /// the game itself: try-on and inspect rotate and zoom but never pan; only
    /// CharaViewPortrait pans. So the preview BODY moves instead, opposite the
    /// requested view travel — camera fixed, body offset, frame effectively
    /// panned. This is Poser's own mechanism; no reference implements it.
    /// </summary>
    public void Pan(float viewDelta) => RunOnFramework(() =>
    {
        if (!_initialized)
            return;
        _viewPanY = Math.Clamp(
            _viewPanY + viewDelta,
            -MaxViewPan,
            MaxViewPan);
        ApplyViewPan();
    });

    public void ResetCamera() => RunOnFramework(() =>
    {
        if (!_initialized)
            return;
        _viewPanY = 0f;
        ApplyViewPan();
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
        _appliedOptions = null;
        ClearPanBase();
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
        _appliedOptions = null;
        _counter = 1;
        // The body goes back where the game put it, and the next preview opens
        // centred — a released CharaView loses its yaw and zoom the same way.
        ClearPanBase();
        _viewPanY = 0f;
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
        // A new body carries none of the previous pose, and stands wherever the
        // game stages it — the pan base is re-read against it.
        _appliedPath = null;
        _appliedOptions = null;
        ClearPanBase();
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
        ApplyViewPan();

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
    ///
    /// A request is already applied only when BOTH the path and the options
    /// INSTANCE are the ones that landed: the binder restates the cached
    /// instance every frame (so that costs nothing), and hands over a fresh
    /// instance exactly when the import options changed under it.
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
        if (string.Equals(path, _appliedPath, StringComparison.Ordinal)
            && ReferenceEquals(options, _appliedOptions))
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
        _appliedOptions = options;
    }

    /// <summary>
    /// Stands the preview body at its staged position plus the pan offset —
    /// ABSOLUTE, never live-plus-delta: a write that reads back its own output
    /// accumulates drift every frame, the same trap the orbit rotation fell
    /// into. Re-asserted every tick after Update/Render, which is also the
    /// answer to the CharaView restaging the body: whatever it staged this
    /// frame is either the captured base or a fresh one.
    ///
    /// The body travels OPPOSITE the view — carrying the view DOWN the body
    /// means lifting the body in front of a camera that cannot move.
    ///
    /// The write path is PosingService.ApplyTransformToActor's, verbatim:
    /// the game object's draw object, <c>Object.Position</c>.
    /// </summary>
    private void ApplyViewPan()
    {
        var drawObject = ResolvePreviewDrawObject();
        if (drawObject == null)
        {
            _panBasePosition = null;
            _panBaseObject = nint.Zero;
            return;
        }

        if (_panBasePosition == null || _panBaseObject != (nint)drawObject)
        {
            _panBasePosition = drawObject->Object.Position;
            _panBaseObject = (nint)drawObject;
        }

        var basePosition = _panBasePosition.Value;
        drawObject->Object.Position = new Vector3(
            basePosition.X,
            basePosition.Y + _viewPanY,
            basePosition.Z);
    }

    /// <summary>
    /// Drops the captured base so the next tick reads whatever the game staged.
    /// If the body captured against is still the one standing there its
    /// position goes back first: re-capturing a position this service had
    /// already offset would fold the offset in twice.
    /// </summary>
    private void ClearPanBase()
    {
        if (_panBasePosition is { } basePosition && _panBaseObject != nint.Zero)
        {
            var drawObject = ResolvePreviewDrawObject();
            if (drawObject != null && (nint)drawObject == _panBaseObject)
                drawObject->Object.Position = basePosition;
        }
        _panBasePosition = null;
        _panBaseObject = nint.Zero;
    }

    private FFXIVClientStructs.FFXIV.Client.Graphics.Scene.DrawObject*
        ResolvePreviewDrawObject()
    {
        var address = _objectTable[PreviewObjectIndex]?.Address ?? nint.Zero;
        return address == nint.Zero
            ? null
            : ((Character*)address)->GameObject.DrawObject;
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
