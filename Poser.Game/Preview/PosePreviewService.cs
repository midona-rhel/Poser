using System;
using System.Numerics;
using System.Linq;
using Dalamud.Game.ClientState.Objects;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Graphics.Render;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using Poser.Game.Bindings;
using Poser.Game.Posing;

using Poser.Application.Posing;
using Poser.Domain.Identity;
using Poser.Domain.Actors;
using Poser.Documents.Files;
using Poser.Application.Lifecycle;
using Poser.Game.Entities;
using Poser.Game.Services;

namespace Poser.Game.Preview;

public sealed unsafe class PosePreviewService : IDisposable, IPosePreview, IPosePreviewRuntime
{
    /// <summary>The slot the CharaView spawns its hidden body into. Outside
    /// the GPose scan range (201-439) on purpose: the preview must never
    /// appear in any actor list the user sees.</summary>
    public const ushort PreviewObjectIndex = 441;

    /// <summary>CharaView slot 1 — the one whose render target is
    /// <c>CharaViewTextures[1]</c>.</summary>
    private const uint CharaViewIndex = 1;

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

    /// <summary>Wait this many framework ticks before showing a readiness message.</summary>
    private const int SkeletonWaitTicks = 120;

    private readonly IFramework _framework;
    private readonly IObjectTable _objectTable;
    private readonly IActorManager _actors;
    private readonly StableBindingRegistry _bindings;
    private readonly IPoseImportCommands _poses;
    private readonly BonePosingService _posing;
    private readonly IGPoseService _gpose;
    private readonly IPluginLog _log;

    // Draw-thread requests, framework-thread consumption.
    private readonly object _gate = new();

    /// <summary>The exact actor generation requested by the UI. Only the
    /// framework thread resolves this to a native appearance source.</summary>
    private ActorId? _requestedSourceId;

    /// <summary>Why the standing request could not be shown, or null. A refused
    /// import leaves the body exactly where the last successful stage left it,
    /// so without this the surface shows a perfectly good render and says
    /// nothing about the pose the user actually picked. Superseded by the next
    /// statement. NOT a readiness channel — see <see cref="_skeletonWaitTicks"/>
    /// — only a verdict about the file.</summary>
    private volatile string? _refusalText;

    /// <summary>The standing request, in the order it must land: the first
    /// stage alone for a single-stage request, both for a
    /// <see cref="ShowSequence"/>. The SERIAL is what the framework
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

    /// <summary>The last source the copy refused, so a source that stays
    /// unresolvable is logged once rather than once per tick.</summary>
    private nint _refusedSource;

    /// <summary>Source address, table slot and object id captured on the framework
    /// thread. Later reads check the saved slot before using the source.</summary>
    private nint _provenSource;
    private ushort _provenIndex = ushort.MaxValue;
    private ulong _provenObjectId;

    /// <summary>Consecutive ticks the standing stage has waited on the preview
    /// body's skeleton, and on the appearance source proving itself. Both waits
    /// are correct but neither may be endless AND silent.</summary>
    private int _skeletonWaitTicks;
    private int _proveWaitTicks;

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
        IPoseImportCommands poses,
        BonePosingService posing,
        IGPoseService gpose,
        IPluginLog log)
    {
        _framework = framework;
        _objectTable = objectTable;
        _actors = actors;
        _bindings = bindings;
        _poses = poses;
        _posing = posing;
        _gpose = gpose;
        _log = log;
    }

    /// <summary>The CharaView is initialized and rendering a body.</summary>
    public bool IsActive => _rendering;

    /// <summary>Null while the preview renders; otherwise a short reason to
    /// show in place of the image.</summary>
    public string? StatusText => _statusText;

    /// <summary>
    /// Null while the standing request is showing; otherwise the typed reason
    /// the pose was refused, to state OVER the render. Distinct from
    /// <see cref="StatusText"/> because a refusal happens with the body
    /// standing and the texture live: there is no empty well to put it in, and
    /// without it the refusal is invisible.
    /// </summary>
    public string? RefusalText => _refusalText;

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
    public void Open(ActorId appearanceSource)
    {
        if (_disposed)
            return;
        if (!_gpose.IsGPosing)
        {
            _statusText = "Enter GPose to preview.";
            return;
        }

        // Draw only names the actor. Resolve its current native binding on the framework tick.
        lock (_gate)
            _requestedSourceId = appearanceSource;

        if (_open)
            return;

        _open = true;
        _statusText = "Preparing preview…";
        _actors.RegisterAuxiliary(PreviewObjectIndex, ActorKind.Preview);
        _framework.Update += OnFrameworkUpdate;
        RunOnFramework(InitializeCharaView);
    }

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

    /// <summary>Replaces the request unless both stage values compare equal.</summary>
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
            // Inside the gate, WITH the bump: the verdict named the pose the
            // serial just replaced, so a framework tick must never be able to
            // observe the new serial still carrying the old reason.
            _refusalText = null;
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

    /// <summary>Pans by moving the preview body vertically, because the inspect
    /// camera does not provide the required pan. Positive input moves the view
    /// down the body. Zoom reduces or increases the movement scale.</summary>
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
    /// Native release is dispatched to the framework thread and is not awaited.</summary>
    public void Close()
    {
        _open = false;
        _rendering = false;
        _statusText = null;
        _refusalText = null;
        lock (_gate)
        {
            _requestedSourceId = null;
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
        _refusedSource = nint.Zero;
        _skeletonWaitTicks = 0;
        _proveWaitTicks = 0;
        ForgetProvenSource();
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
        _refusedSource = nint.Zero;
        _skeletonWaitTicks = 0;
        _proveWaitTicks = 0;
        ForgetProvenSource();
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
        ActorId? sourceId;
        lock (_gate)
            sourceId = _requestedSourceId;
        if (sourceId is not { } id
            || _bindings.Resolve(id) is not { Success: true, Value: { } sourceActor })
            return;
        var source = sourceActor.Address;
        var named = sourceActor.Id;

        // Preserve the request while its source is unavailable. Check the table
        // slot before copying; retry a refused source on later ticks.
        if (source != _provenSource && !ProveSource(source, named))
            return;
        _proveWaitTicks = 0;
        if (!ProvenSourceStillStands())
        {
            // The proof no longer holds. _copiedSource goes with it: leaving it
            // set would let the short-circuit below FOSSILIZE a copy taken from
            // an actor that has since left, with no path back.
            _log.Debug(
                "PosePreviewService: appearance source left its slot; the copy "
                + "is dropped and the source must prove itself again");
            ForgetProvenSource();
            return;
        }
        if (source == _copiedSource)
            return;

        _refusedSource = nint.Zero;
        agent->CharaView.ModelData.CopyFromCharacter((Character*)source);
        _copiedSource = source;
        // A new body carries none of the previous pose, and stands wherever the
        // game stages it — the pan base is re-read against it.
        ForgetAppliedPose();
        ClearPanBase();
    }

    /// <summary>Requires the actor list to associate this address with the named
    /// actor, then records the matching object-table slot and object id.</summary>
    private bool ProveSource(nint source, EntityId named)
    {
        // Identity first, and off the SCENE rather than the table — a second,
        // independently maintained account of who lives at this address. It is
        // also the cheap half: a source that has genuinely gone is refused here
        // without walking ~599 slots every tick.
        if (!SceneStillNames(source, named))
            return Unproven(
                source, "the scene no longer names that actor at that address");

        for (var index = 0; index < _objectTable.Length; index++)
        {
            if (_objectTable.GetObjectAddress(index) != source)
                continue;
            if (_objectTable[index] is not { } occupant)
                break;
            _provenSource = source;
            _provenIndex = (ushort)index;
            _provenObjectId = occupant.GameObjectId;
            return true;
        }

        return Unproven(source, "no object-table slot holds it");
    }

    /// <summary>Whether the scene still knows this exact address as the actor
    /// that was NAMED. A recycled address fails: the scene has a different
    /// actor there, or none.</summary>
    private bool SceneStillNames(nint source, EntityId named)
    {
        var actors = _actors.Actors;
        for (var i = 0; i < actors.Count; i++)
            if (actors[i].Address == source)
                return actors[i].Id == named;
        return false;
    }

    /// <summary>Counts source-readiness waits and logs each refused address once.</summary>
    private bool Unproven(nint source, string why)
    {
        if (_refusedSource != source)
        {
            _refusedSource = source;
            _log.Debug(
                $"PosePreviewService: appearance source unproven — {why}; "
                + "the copy waits");
        }
        _proveWaitTicks++;
        return false;
    }

    /// <summary>Checks the saved slot, address and object id. These observations
    /// cannot distinguish reuse with all three values unchanged.</summary>
    private bool ProvenSourceStillStands() =>
        _provenIndex != ushort.MaxValue
        && _objectTable[_provenIndex] is { } occupant
        && occupant.Address == _provenSource
        && occupant.GameObjectId == _provenObjectId;

    /// <summary>Drops the proof and the copy taken under it. The next tick
    /// proves the standing request again from scratch.</summary>
    private void ForgetProvenSource()
    {
        _provenSource = nint.Zero;
        _provenIndex = ushort.MaxValue;
        _provenObjectId = 0;
        _copiedSource = nint.Zero;
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
        // The frame's own status is written first and the WAITS speak over it:
        // CopyAppearance ran before this line and TryApplyPendingPose runs
        // after it, so a wait recorded either side has to be applied here or
        // it would be overwritten by the very next tick's "Preparing preview…".
        if (_proveWaitTicks > SkeletonWaitTicks)
            _statusText = "Preview source not found — waiting…";

        if (previewAddress != nint.Zero)
            TryApplyPendingPose(previewAddress);
    }

    /// <summary>Dispatches each stage once after actor, binding and skeleton
    /// readiness. Waits while the import engine is busy. A synchronous refusal
    /// consumes the stage; terminal import receipts are not observed here.</summary>
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
        // The sequence is one stage or two (rebase then
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
        // Wait for the auxiliary actor and its published binding.
        if (actor == null || _bindings.GetActorId(actor) is not { } previewId)
        {
            if (++_skeletonWaitTicks > SkeletonWaitTicks)
                _statusText = "Waiting for the preview body…";
            return;
        }
        // Wait for the skeleton before spending this stage's import attempt.
        if (!_poses.HasPosableSkeleton(previewId))
        {
            if (++_skeletonWaitTicks > SkeletonWaitTicks)
                _statusText = "Waiting for the preview body…";
            return;
        }
        _skeletonWaitTicks = 0;

        // Rebase without stale constraints, then snapshot the source IK for
        // the file stage. The copied targets are preview-model values only.
        ActorId? sourceId;
        lock (_gate) sourceId = _requestedSourceId;
        var ikSource = sourceId is { } id
            && _bindings.Resolve(id) is { Success: true, Value: { } sourceActor }
            ? sourceActor : null;
        _posing.CopyPreviewIk(_appliedStage == 0 && second != null ? null : ikSource, actor);

        var result = request.Pose is { } pose
            ? _poses.ImportPose(previewId, pose, request.Options, "Preview pose")
            : _poses.ImportPose(previewId, request.Path!, request.Options);
        if (!result.Success)
            _log.Debug(
                $"Pose preview could not show '{request.Key}': {result.Detail}");
        // Only a FILE stage's verdict is the user's to read. The rebase
        // baseline is this service's own machinery — the doc above already
        // says a baseline that cannot be stood in still lets the file show —
        // so its refusal stays a log line and never dresses the render in a
        // reason about a pose the user never picked.
        if (request.Path is not null)
            _refusalText = result.Success
                ? null
                : result.Detail ?? "This pose could not be shown.";
        _appliedStage++;
    }

    /// <summary>Writes the captured base position plus pan, avoiding accumulated
    /// drift. A new draw-object address causes the base to be captured again.</summary>
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
            // Scheduling failed. The native release may remain unfinished.
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
