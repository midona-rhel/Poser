using System;
using System.Numerics;
using Dalamud.Game.ClientState.Objects;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Graphics.Render;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using Poser.Core;
using Poser.Entities;
using Poser.Files;
using Poser.Game.Bindings;
using Poser.Game.Posing;
using Poser.Services;

namespace Poser.Game.Preview;

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

    /// <summary>Framework ticks a stage may wait on the preview body's skeleton
    /// before the wait is STATED. About two seconds — long past the handful of
    /// ticks a healthy bind takes, so the line only ever appears when something
    /// is actually stuck.</summary>
    private const int SkeletonWaitTicks = 120;

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

    /// <summary>The identity of the actor whose address <see cref="Open"/>
    /// named, read off the IActor itself. Paired with the address so a proof
    /// can say "still the same actor", not merely "still someone".</summary>
    private EntityId? _requestedSourceId;

    /// <summary>Why the standing request could not be shown, or null. A refused
    /// import leaves the body exactly where the last successful stage left it,
    /// so without this the surface shows a perfectly good render and says
    /// nothing about the pose the user actually picked. Superseded by the next
    /// statement. NOT a readiness channel — see <see cref="_skeletonWaitTicks"/>
    /// — only a verdict about the file.</summary>
    private volatile string? _refusalText;

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

    /// <summary>The last source the copy refused, so a source that stays
    /// unresolvable is logged once rather than once per tick.</summary>
    private nint _refusedSource;

    /// <summary>
    /// The appearance source AS PROVEN on the framework thread: the address the
    /// draw thread asked for, the object-table slot found to be holding it, and
    /// that occupant's GameObjectId. Recorded ONCE, by searching the table for
    /// the address (<see cref="ProveSource"/>) rather than by dereferencing it,
    /// and thereafter only ever revalidated INDEX-FIRST
    /// (<see cref="ProvenSourceStillStands"/>).
    ///
    /// <para>The identity has to be stored, not re-derived: an index taken FROM
    /// the address under test proves only "this address is some live occupant",
    /// which a recycled address at a DIFFERENT slot satisfies. The stored slot
    /// plus the stored id is what makes the check say "still the same actor"
    /// (WorldActorDiscovery.cs:77-86 stores the index, :270-274 pairs it with
    /// the GameObjectId).</para>
    /// </summary>
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

        // The address AND THE IDENTITY are recorded here and proven in
        // CopyAppearance. Both are reads of the IActor the caller already
        // holds — no table access — because this runs on the draw thread every
        // frame while the object table is coherent only on the framework tick:
        // a resolve here would read the table off its own phase, and a null
        // from that unsynchronised read would veto the whole lifecycle below —
        // no _open, no auxiliary registration, no framework subscription — for
        // a preview that is perfectly alive. Naming a source is a draw-thread
        // statement; dereferencing one is not.
        //
        // The ID is what makes the address a claim about a PARTICULAR actor.
        // Without it a recycled address is refused once and then simply proven
        // again as whoever now occupies it, and the preview quietly wears a
        // stranger's appearance — the same hole, one tick later.
        lock (_gate)
        {
            _requestedSource = appearanceSource.Address;
            _requestedSourceId = appearanceSource.Id;
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
        _refusalText = null;
        lock (_gate)
        {
            _requestedSource = nint.Zero;
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
        nint source;
        EntityId? sourceId;
        lock (_gate)
        {
            source = _requestedSource;
            sourceId = _requestedSourceId;
        }
        if (source == nint.Zero || sourceId is not { } named)
            return;

        // PROVE ONCE, then REVALIDATE EVERY TICK — both on this thread, where
        // the object table is coherent. The request only NAMES an address and
        // was stated ticks ago; a source that despawned since would leave it
        // pointing at freed or recycled memory.
        //
        // Refusal is for THIS TICK ONLY and never touches the request: the
        // standing request belongs to the draw thread, which restates it every
        // frame from Open(). Clearing it here would start a refuse/re-arm loop
        // that cannot terminate, and a refusal landing before the first
        // successful copy would leave the CharaView with empty ModelData — no
        // body ever spawns at slot 441, TryApplyPendingPose is never reached,
        // and every stated pose is dropped in silence.
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

    /// <summary>
    /// Proves that <paramref name="source"/> is still the actor
    /// <paramref name="named"/>, and records where it lives. TWO independent
    /// accounts have to agree: the SCENE must still know that address as that
    /// actor, and the object TABLE must have a slot holding it.
    ///
    /// <para>The table search is by ADDRESS (<c>GetObjectAddress</c> reads each
    /// slot's own pointer), so the suspect pointer is never dereferenced to
    /// find out where it lives — asking an address which slot it occupies is
    /// asking the thing under test to vouch for itself.</para>
    /// </summary>
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

    /// <summary>A source that cannot be proven is WAITED on, never spent: it is
    /// routinely a body still coming up. Logged once per source; the tick count
    /// is what <see cref="OnFrameworkUpdate"/> turns into a spoken wait past the
    /// same bound the skeleton wait uses, rather than staring back in
    /// silence.</summary>
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

    /// <summary>
    /// INDEX FIRST, always: read the slot that was recorded and ask whether it
    /// still holds the recorded address AND the recorded actor. Deriving the
    /// index from the address instead would prove only that the address is
    /// SOME live occupant — a despawned source whose memory has been recycled
    /// into a different actor at a different slot passes that test, which is no
    /// test at all.
    /// </summary>
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
        // The auxiliary actor and its STABLE BINDING, on the same bounded-
        // silence terms as the skeleton wait below rather than a bare return:
        // the binding is published by the scene refresh, and a refresh that
        // cannot see the preview body arrive (it owns no scene descriptor by
        // design) used to drop the candidate carrying it, so this gate held
        // shut forever and every pose was dropped without a word behind a
        // perfectly good render. The publication is fixed at its source
        // (StableBindingRegistry.AuxiliaryBindingsChanged); this makes the WAIT
        // audible, so the next variant of it cannot hide.
        if (actor == null || _bindings.GetActorId(actor) == null)
        {
            if (++_skeletonWaitTicks > SkeletonWaitTicks)
                _statusText = "Waiting for the preview body…";
            return;
        }
        // …and its SKELETON, on the same terms. The auxiliary actor and its
        // stable binding both exist several ticks before the CharaView body is
        // skeleton-bound, so every first statement against a fresh body races
        // it. An import dispatched inside that window plans nothing and comes
        // back as the ordinary "nothing applies" refusal — indistinguishable
        // from a file whose bones genuinely miss — and the stage is SPENT
        // below, so the pose is dropped for good while the skeleton lands
        // milliseconds later. Waiting is the only correct reading: readiness is
        // not a verdict about the file.
        //
        // Bounded silence, never a refusal. The wait normally ends within a
        // handful of ticks; past the bound something is genuinely stuck, and a
        // preview that sits there saying nothing is the same failure in a new
        // costume. The user is told it is WAITING — the standing render, if
        // any, keeps showing meanwhile.
        if (!_poses.HasPosableSkeleton(actor))
        {
            if (++_skeletonWaitTicks > SkeletonWaitTicks)
                _statusText = "Waiting for the preview body…";
            return;
        }
        _skeletonWaitTicks = 0;

        var result = request.Pose is { } pose
            ? _poses.ImportPose(actor, pose, request.Options, "Preview pose")
            : _poses.ImportPose(actor, request.Path!, request.Options);
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
