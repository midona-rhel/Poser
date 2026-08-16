using Dalamud.Plugin.Services;
using Poser.Application.Scene;
using Poser.Application.Transforms;
using Poser.Core;
using Poser.Domain.Scene;
using Poser.Game.Bindings;
using Poser.Services;

namespace Poser.Game.Scene;

/// <summary>
/// Owns native discovery refresh and clean GPose-session teardown. Skeleton
/// discovery belongs HERE, not to any inspector section: an actor whose draw
/// object or Havok skeleton is not ready at first discovery is retried on
/// the framework thread at a bounded backoff cadence while it remains
/// present. Refreshes are coalesced through a structural signature — an
/// attempt that finds no change publishes nothing, increments no scene
/// revision, and cancels no active transform gesture.
/// </summary>
public sealed class CleanSceneLifecycle : IDisposable
{
    private static readonly TimeSpan InitialRetryInterval = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan MaxRetryInterval = TimeSpan.FromSeconds(5);

    private readonly StableBindingRegistry _bindings;
    private readonly SceneSession _scene;
    private readonly TransformGestureService _gestures;
    private readonly TransformHistory _history;
    private readonly Poser.Application.Animation.AnimationSession _animation;
    private readonly Poser.Application.Presentation.ActorPresentationSession _presentation;
    private readonly Poser.Application.Appearance.ActorModelIdSession _modelId;
    private readonly Poser.Application.Integration.ActorIntegrationSession _integration;
    private readonly Poser.Game.Animation.AnimationRuntimePort _animationPort;
    private readonly Poser.Game.Animation.FacialPoseCapture _facialCapture;
    private readonly IEventBus _events;
    private readonly IFramework _framework;

    private static readonly TimeSpan SlotPollInterval = TimeSpan.FromSeconds(1);

    private readonly object _disposeGate = new();
    private bool _disposeRestoreAbandoned;

    private SceneSnapshot? _lastSignature;
    private bool _refreshing;
    private bool _retryPending;
    private TimeSpan _retryInterval = TimeSpan.FromMilliseconds(500);
    private DateTime _nextRetryUtc = DateTime.MinValue;
    private DateTime _nextSlotPollUtc = DateTime.MinValue;

    public CleanSceneLifecycle(
        StableBindingRegistry bindings,
        SceneSession scene,
        TransformGestureService gestures,
        TransformHistory history,
        Poser.Application.Animation.AnimationSession animation,
        Poser.Application.Presentation.ActorPresentationSession presentation,
        Poser.Application.Appearance.ActorModelIdSession modelId,
        Poser.Application.Integration.ActorIntegrationSession integration,
        Poser.Game.Animation.AnimationRuntimePort animationPort,
        Poser.Game.Animation.FacialPoseCapture facialCapture,
        IEventBus events,
        IFramework framework)
    {
        _bindings = bindings;
        _scene = scene;
        _gestures = gestures;
        _history = history;
        _animation = animation;
        _presentation = presentation;
        _modelId = modelId;
        _integration = integration;
        _animationPort = animationPort;
        _facialCapture = facialCapture;
        _events = events;
        _framework = framework;
        _events.Subscribe<ActorListChangedEvent>(OnActorListChanged);
        _events.Subscribe<LightListChangedEvent>(OnLightListChanged);
        _events.Subscribe<CameraListChangedEvent>(OnCameraListChanged);
        _events.Subscribe<PropListChangedEvent>(OnPropListChanged);
        _events.Subscribe<OverlayNodeListChangedEvent>(OnOverlayListChanged);
        _events.Subscribe<WorldObjectListChangedEvent>(OnWorldObjectListChanged);
        _events.Subscribe<SkeletonChangedEvent>(OnSkeletonChanged);
        _events.Subscribe<GPoseStateChangedEvent>(OnGPoseChanged);
        // Discovery, retries, and refreshes all run on the framework thread:
        // the registry refresh reads native skeleton data and shared
        // bone-name state, while events publish from the framework thread —
        // a concurrent ctor-thread refresh corrupted shared collections.
        _framework.Update += OnFrameworkUpdate;
        _ = framework.RunOnFrameworkThread(Refresh);
    }

    public void Dispose()
    {
        // Unhooking the pump stops any pending missing-skeleton retries.
        _framework.Update -= OnFrameworkUpdate;
        _events.Unsubscribe<ActorListChangedEvent>(OnActorListChanged);
        _events.Unsubscribe<LightListChangedEvent>(OnLightListChanged);
        _events.Unsubscribe<CameraListChangedEvent>(OnCameraListChanged);
        _events.Unsubscribe<PropListChangedEvent>(OnPropListChanged);
        _events.Unsubscribe<OverlayNodeListChangedEvent>(OnOverlayListChanged);
        _events.Unsubscribe<WorldObjectListChangedEvent>(OnWorldObjectListChanged);
        _events.Unsubscribe<SkeletonChangedEvent>(OnSkeletonChanged);
        _events.Unsubscribe<GPoseStateChangedEvent>(OnGPoseChanged);

        // Plugin unload while still in GPose is the same last moment as a
        // GPose exit: the overridden actors are about to become
        // unreachable, so every animation override is put back NOW. A
        // face bake pending at unload can never complete (its pump is
        // gone), so its command guard is released first rather than left
        // to block the restoration. Disposal must not throw.
        try
        {
            if (_framework.IsInFrameworkUpdateThread)
            {
                // Dalamud disposes plugins on the framework thread; run
                // inline — no waiting, no queue.
                ResetOwnedState("Scene lifecycle disposed.");
            }
            else
            {
                // Off-thread disposal: bounded wait, with a gate that
                // makes timeout and execution mutually exclusive. If the
                // pump is dead the wait expires and the flag abandons the
                // queued callback; if the callback is mid-restore it holds
                // the gate, so disposal blocks until it finishes rather
                // than returning under it. Either way the callback can
                // never run against disposed services.
                var task = _framework.RunOnFrameworkThread(() =>
                {
                    lock (_disposeGate)
                    {
                        if (_disposeRestoreAbandoned)
                            return;
                        ResetOwnedState("Scene lifecycle disposed.");
                    }
                });
                if (!task.Wait(TimeSpan.FromSeconds(2)))
                {
                    lock (_disposeGate)
                    {
                        _disposeRestoreAbandoned = true;
                    }
                }
            }
        }
        catch
        {
            // An unreachable framework thread at shutdown means the game
            // is tearing down anyway; there is nothing left to restore
            // into.
        }
    }

    private void Refresh()
    {
        // The registry refresh itself creates missing skeletons, and
        // SkeletonService publishes SkeletonChangedEvent synchronously while
        // doing so — suppress the nested re-entry it would trigger.
        if (_refreshing)
            return;
        _refreshing = true;
        try
        {
            RefreshCore();
        }
        finally
        {
            _refreshing = false;
        }
    }

    private void RefreshCore()
    {
        var staged = _bindings.RefreshCandidate();
        var candidate = staged.Snapshot;
        var admitted = false;
        try
        {
            // The refresh is the only place that resolves a whole skeleton's bone
            // names at once, so it owns the flush of whatever those lookups found
            // untranslated. One line per refresh instead of one per bone: a modded
            // 400-bone character used to pay hundreds of synchronous log writes on
            // this exact tick. No-op when nothing new was seen.
            Poser.Core.BoneInfo.BoneInfoService.FlushUntranslatedLog();
            // One structural signature coalesces every refresh source (events,
            // retries, session transitions): identical scenes publish nothing —
            // no snapshot churn, no revision increment, no gesture cancellation.
            //
            // The scene signature is not the WHOLE candidate, though: auxiliary
            // bodies (the CharaView pose preview) are bound so the import
            // pipeline can reach them and are deliberately absent from the
            // snapshot, so one appearing or being replaced moves nothing this
            // signature can see. Coalescing on it alone therefore ABORTS the
            // candidate that carries the preview's own bindings, and every pose
            // stated against the preview is dropped in silence — see
            // StableBindingRegistry.AuxiliaryBindingsChanged. Both halves have
            // to be unchanged for a refresh to publish nothing.
            var signature = CanonicalSignature(candidate);
            _retryPending = candidate.Actors.Any(
                actor => actor.CharacterSkeleton == null);
            if (!_retryPending)
                _retryInterval = InitialRetryInterval;
            if (_lastSignature?.ContentEquals(signature) == true
                && !_bindings.AuxiliaryBindingsChanged(staged))
                return;

            var result = _scene.TryRefresh(CreateAdmissionCandidate(
                candidate,
                _scene.Snapshot));
            if (!result.Accepted)
                return;

            // SceneSession owns application admission; native maps become visible
            // only after that same admission accepts the candidate. This includes
            // NoChange: exact ids/generations are still checked by the registry
            // against the admitted structural snapshot before map publication.
            _bindings.CommitCandidate(staged, _scene.Snapshot);
            admitted = true;

            // A rejected candidate is deliberately retried: recording its
            // signature would coalesce away the correction opportunity.
            _lastSignature = signature;
            _retryInterval = InitialRetryInterval;
            if (!result.StateChanged)
                return;

            // Selective reconciliation against the refreshed exact-generation
            // scene: a gesture whose every target is still current survives and
            // accepts the new revision (unrelated actors/slots may come and go
            // mid-drag); any stale target cancels it once through the rebuilt
            // bindings with no history entry. History patches follow the same
            // rule per patch.
            _gestures.ReconcileScene(_scene.Contains);
            _history.Reconcile(_scene.Contains);
            // Animation follows the same exact-generation rule: a replaced
            // actor's old entry is released without touching the new body.
            // The port's detour-facing address index is rebuilt from the
            // surviving stable ids in the same step, so a redrawn actor can
            // never inherit the previous body's speed enforcement.
            _animation.Reconcile(_scene.Snapshot);
            _presentation.Reconcile(_scene.Snapshot);
            _modelId.Reconcile(_scene.Snapshot);
            _integration.Reconcile(_scene.Snapshot);
            _animationPort.SyncEnforcementIndex();
        }
        finally
        {
            if (!admitted)
                _bindings.AbortCandidate(staged);
        }
    }

    /// <summary>
    /// Bounded retry pump for actors whose skeletons were not ready at
    /// discovery: retries at a backoff cadence (0.5 s doubling to 5 s) while
    /// such an actor remains present. Runs only on the framework tick.
    /// </summary>
    private void OnFrameworkUpdate(IFramework framework)
    {
        var now = DateTime.UtcNow;

        // Auxiliary slot changes (sheathe/unsheathe, equipment or prop
        // replacement, ornament spawn/despawn) fire none of our events, so
        // slot presence is polled at a steady cadence. The structural
        // signature makes an unchanged scene free: no snapshot, no
        // revision, no gesture cancellation.
        if (!_retryPending)
        {
            if (now < _nextSlotPollUtc)
                return;
            _nextSlotPollUtc = now + SlotPollInterval;
            Refresh();
            return;
        }

        if (now < _nextRetryUtc)
            return;
        _nextRetryUtc = now + _retryInterval;
        var doubled = _retryInterval + _retryInterval;
        _retryInterval = doubled > MaxRetryInterval ? MaxRetryInterval : doubled;
        Refresh();
    }

    /// <summary>
    /// Creates the one revision-neutral structural fingerprint. ContentEquals
    /// is the normative full-scene policy, including exact ids, bone topology,
    /// relationships, environment, gaze, and every camera field.
    /// </summary>
    internal static SceneSnapshot CanonicalSignature(SceneSnapshot candidate) =>
        candidate with { Revision = 0 };

    /// <summary>
    /// Serializes producer content against the committed Application scene.
    /// Exact replays retain its revision; changed content requests the next
    /// revision, saturating at ulong.MaxValue where SceneSession permits an
    /// equal-revision content update. SceneSession remains the only committed
    /// revision owner.
    /// </summary>
    internal static SceneSnapshot CreateAdmissionCandidate(
        SceneSnapshot candidate,
        SceneSnapshot committed)
    {
        var signature = CanonicalSignature(candidate);
        var committedSignature = CanonicalSignature(committed);
        var revision = signature.ContentEquals(committedSignature)
            ? committed.Revision
            : committed.Revision == ulong.MaxValue
                ? ulong.MaxValue
                : committed.Revision + 1;
        return candidate with { Revision = revision };
    }

    private void OnActorListChanged(ActorListChangedEvent _) =>
        Refresh();

    private void OnLightListChanged(LightListChangedEvent _) =>
        Refresh();

    private void OnPropListChanged(PropListChangedEvent _) =>
        Refresh();

    private void OnOverlayListChanged(OverlayNodeListChangedEvent _) =>
        Refresh();

    /// <summary>Borrowing a map object and releasing it both move the scene,
    /// and this event was published from the first day with nothing listening:
    /// an adopted object appeared only if some unrelated list happened to
    /// change and kick a refresh.</summary>
    private void OnWorldObjectListChanged(WorldObjectListChangedEvent _) =>
        Refresh();

    private void OnCameraListChanged(CameraListChangedEvent _) =>
        Refresh();

    private void OnSkeletonChanged(SkeletonChangedEvent _) =>
        Refresh();

    private void OnGPoseChanged(GPoseStateChangedEvent evt)
    {
        if (!evt.IsGPosing)
        {
            if (_gestures.ActiveGesture is { } gesture)
                _gestures.Cancel(gesture);
            _history.Clear();
            // Leaving GPose is the last chance to write into the actors
            // Poser overrode, so everything owned is put back here rather
            // than dropped when they disappear. "Last chance" is not
            // "guaranteed": the edge is observed after IsGPosing has
            // already flipped, so the clone may ALREADY be destroyed and
            // the exact generation unresolvable by the time this runs.
            // Owners that hold state the object does not own must carry
            // their own fallback — see the MCDF teardown's by-name
            // Glamourer release in runtime-appearance.md.
            ResetOwnedState("GPose exited.");
        }
        Refresh();
    }

    private void ResetOwnedState(string reason) =>
        ResetOwnedStateForLifecycle(
            reason,
            detail => { _facialCapture.CancelPending(detail); },
            () => { _animation.ResetAll(); },
            () => { _presentation.ResetAll(); },
            () => { _modelId.ResetAll(); },
            () => { _integration.ResetAll(); });

    /// <summary>One teardown order for GPose exit and plugin disposal.</summary>
    internal static void ResetOwnedStateForLifecycle(
        string reason,
        Action<string> cancelFacialCapture,
        Action resetAnimation,
        Action resetPresentation,
        Action resetModelId,
        Action resetIntegration)
    {
        cancelFacialCapture(reason);
        resetAnimation();
        resetPresentation();
        resetModelId();
        resetIntegration();
    }
}
