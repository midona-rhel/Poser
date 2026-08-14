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
/// ONE pose the preview body should stand in: a file on disk, or a pose held
/// in memory (the rebase baseline, which no path names).
/// </summary>
/// <param name="Key">What the request is DEDUPED on in place of a path — the
/// path itself for a file, a caller-chosen stand-in for an in-memory pose.
/// Restating the same key with the same options INSTANCE is free.</param>
public readonly record struct PosePreviewRequest(
    string Key, string? Path, PoseFile? Pose, PoseImportOptions Options)
{
    public static PosePreviewRequest File(
        string path, PoseImportOptions options) =>
        new(path, path, null, options);

    public static PosePreviewRequest Memory(
        PoseFile pose, string key, PoseImportOptions options) =>
        new(key, null, pose, options);
}

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

    /// <summary>Zoom-in accumulated on <see cref="Zoom"/> that HALVES the pan
    /// speed — zoomed in, the frame shows less body, so the same pan travel
    /// reads that much stronger (user-reported: great at default zoom, too
    /// strong zoomed in). In the argument units of SetCameraDistance: every
    /// this-many units closer halves pan; the factor is clamped so extreme
    /// dollies never freeze or launch the pan.</summary>
    private const float ZoomPanHalving = 10f;
    private const float MinPanScale = 0.15f;
    private const float MaxPanScale = 2.0f;

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

    /// <summary>The object-table index the requested source occupied when the
    /// request was made. An address alone is a claim; the copy re-proves it by
    /// checking this slot still holds that exact address (WorldActorDiscovery's
    /// standard), because ticks pass between Open and the copy.</summary>
    private ushort _requestedSourceIndex = ushort.MaxValue;

    /// <summary>The standing request, in the order it must land: the first
    /// stage alone for a plain <see cref="ShowPose(string, PoseImportOptions)"/>,
    /// both for a <see cref="ShowSequence"/>. The SERIAL is what the framework
    /// side watches — a new statement supersedes whatever the sequence had
    /// reached, wholesale.</summary>
    private PosePreviewRequest? _requestedFirst;
    private PosePreviewRequest? _requestedSecond;
    private long _requestSerial;

    private volatile bool _open;
    private volatile bool _rendering;
    private volatile string? _statusText;
    private volatile bool _disposed;

    // Framework thread only.
    private bool _initialized;
    private nint _copiedSource;
    private uint _counter = 1;

    /// <summary>The serial the body currently stands for, and how many of its
    /// stages have been dispatched. -1 is "this body stands for nothing" — a
    /// fresh CharaView, a re-copied appearance — which re-runs the whole
    /// sequence rather than only its tail.</summary>
    private long _appliedSerial = -1;
    private int _appliedStage;
    private float _viewPanY;
    private float _zoomAccum;
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

        var sourceReference = _objectTable.CreateObjectReference(appearanceSource.Address);
        if (sourceReference is null)
        {
            _statusText = "Select an actor to preview.";
            return;
        }

        lock (_gate)
        {
            _requestedSource = appearanceSource.Address;
            _requestedSourceIndex = sourceReference.ObjectIndex;
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
    public void ShowPose(string path, PoseImportOptions options) =>
        Request(PosePreviewRequest.File(path, options), null);

    /// <summary>The same statement for a pose held in memory — the rebase
    /// baseline, which is a capture and not a file. <paramref name="key"/>
    /// stands in for the path in the dedupe.</summary>
    public void ShowPose(PoseFile pose, string key, PoseImportOptions options) =>
        Request(PosePreviewRequest.Memory(pose, key, options), null);

    /// <summary>
    /// TWO poses in order, which is how a preview shows what an import will
    /// actually do: the body is first stood in the target's own pose
    /// (<paramref name="first"/>) and the file then lands on top of it
    /// (<paramref name="second"/>) with the user's real options — a layering
    /// import layers over the same stance the confirm will.
    ///
    /// <para>The pair is ONE request: a later statement replaces both stages
    /// wherever the sequence had got to, and the sequence itself survives every
    /// retry the pending path makes (the body has no binding yet, an import is
    /// already in flight) because the stage counter, not the caller, is what
    /// advances it.</para>
    /// </summary>
    public void ShowSequence(PosePreviewRequest first, PosePreviewRequest second) =>
        Request(first, second);

    /// <summary>
    /// The one door every statement goes through. A restatement of what already
    /// stands is FREE — the binder restates every frame while the service warms
    /// up — and identity is the same rule as before the sequence existed: the
    /// key, and the options INSTANCE.
    /// </summary>
    private void Request(PosePreviewRequest first, PosePreviewRequest? second)
    {
        lock (_gate)
        {
            if (_requestedFirst is { } standing
                && standing == first
                && Nullable.Equals(_requestedSecond, second))
                return;
            _requestedFirst = first;
            _requestedSecond = second;
            _requestSerial++;
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
        if (agent == null)
            return;
        agent->CharaView.SetCameraDistance(distanceDelta);
        // Remembered so the pan can slow down as the frame tightens.
        _zoomAccum += distanceDelta;
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
        // Zoomed in, the frame shows less body: scale the travel down so a
        // drag reads the same at every zoom. Zoom-in deltas are negative, so
        // the power is < 1 when close and > 1 when backed off.
        float scale = Math.Clamp(
            MathF.Pow(2f, _zoomAccum / ZoomPanHalving),
            MinPanScale,
            MaxPanScale);
        _viewPanY = Math.Clamp(
            _viewPanY + viewDelta * scale,
            -MaxViewPan,
            MaxViewPan);
        ApplyViewPan();
    });

    public void ResetCamera() => RunOnFramework(() =>
    {
        if (!_initialized)
            return;
        _viewPanY = 0f;
        _zoomAccum = 0f;
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
            _requestedSourceIndex = ushort.MaxValue;
            _requestedFirst = null;
            _requestedSecond = null;
            _requestSerial++;
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
        ForgetAppliedPose();
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
        ForgetAppliedPose();
        _counter = 1;
        // The body goes back where the game put it, and the next preview opens
        // centred — a released CharaView loses its yaw and zoom the same way.
        ClearPanBase();
        _viewPanY = 0f;
        _zoomAccum = 0f;
        var agent = AgentInspect.Instance();
        if (agent != null)
            agent->CharaView.Release();
    }

    private void CopyAppearance(AgentInspect* agent)
    {
        nint source;
        ushort sourceIndex;
        lock (_gate)
        {
            source = _requestedSource;
            sourceIndex = _requestedSourceIndex;
        }
        if (source == nint.Zero || source == _copiedSource)
            return;

        // Deref-time revalidation: the request was made ticks ago, and a source
        // that despawned since would leave this address pointing at freed or
        // recycled memory. The slot must still hold the exact address.
        if (sourceIndex == ushort.MaxValue
            || _objectTable[sourceIndex] is not { } occupant
            || occupant.Address != source)
        {
            lock (_gate)
            {
                if (_requestedSource == source)
                {
                    _requestedSource = nint.Zero;
                    _requestedSourceIndex = ushort.MaxValue;
                }
            }
            _log.Debug("PosePreviewService: appearance source no longer occupies its slot; copy refused");
            return;
        }

        agent->CharaView.ModelData.CopyFromCharacter((Character*)source);
        _copiedSource = source;
        // A new body carries none of the previous pose, and stands wherever the
        // game stages it — the pan base is re-read against it.
        ForgetAppliedPose();
        ClearPanBase();
    }

    /// <summary>The body stands for nothing: the standing request runs again
    /// from its FIRST stage.</summary>
    private void ForgetAppliedPose()
    {
        _appliedSerial = -1;
        _appliedStage = 0;
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
    /// The pending sequence lands as soon as the preview body has an auxiliary
    /// actor AND a stable binding — the import engine resolves every target
    /// through the binding registry, whose refresh runs on its own cadence.
    /// Until then the standing request is retried next tick, from whichever
    /// stage it had reached.
    ///
    /// <para>ONE STAGE PER ARM: the engine takes a single import at a time, so
    /// the second stage waits on the first through <see cref="CleanPoseFacade.
    /// IsImportBusy"/> rather than failing against it. A stage that is REFUSED
    /// (an unreadable file, nothing in scope) is spent all the same — retrying
    /// it would re-read the file every tick forever — and the sequence moves
    /// on, so a baseline that cannot be stood in still lets the file show.</para>
    ///
    /// <para>The serial is the supersession: a statement made mid-sequence
    /// replaces both stages, and the counter restarts at the first.</para>
    /// </summary>
    private void TryApplyPendingPose(nint previewAddress)
    {
        PosePreviewRequest? first;
        PosePreviewRequest? second;
        long serial;
        lock (_gate)
        {
            first = _requestedFirst;
            second = _requestedSecond;
            serial = _requestSerial;
        }
        if (first is null)
            return;
        if (serial != _appliedSerial)
        {
            _appliedSerial = serial;
            _appliedStage = 0;
        }
        // The sequence is one stage (a plain ShowPose) or two (rebase then
        // file). Once every stage has been dispatched the body stands for the
        // whole request and NOTHING more is armed — the bug this guards was a
        // second stage re-arming every idle tick, which held the shared import
        // pipeline busy forever and jittered the body between the two stages.
        int stageCount = second is null ? 1 : 2;
        if (_appliedStage >= stageCount)
            return;
        if ((_appliedStage == 0 ? first : second) is not { } request)
            return;
        // The engine arms one import at a time; a stage refused for that alone
        // would be spent below, so the wait happens before it is attempted.
        if (_poses.IsImportBusy)
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

        var result = request.Pose is { } pose
            ? _poses.ImportPose(actor, pose, request.Options, "Preview pose")
            : _poses.ImportPose(actor, request.Path!, request.Options);
        if (!result.Success)
            _log.Debug(
                $"Pose preview could not show '{request.Key}': {result.Detail}");
        _appliedStage++;
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
