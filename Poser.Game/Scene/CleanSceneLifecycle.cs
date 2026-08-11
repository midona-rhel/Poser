using System.Text;
using Dalamud.Plugin.Services;
using Poser.Application.Scene;
using Poser.Application.Transforms;
using Poser.Core;
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
    private readonly Poser.Application.Integration.ActorIntegrationSession _integration;
    private readonly Poser.Game.Animation.AnimationRuntimePort _animationPort;
    private readonly IEventBus _events;
    private readonly IFramework _framework;

    private static readonly TimeSpan SlotPollInterval = TimeSpan.FromSeconds(1);

    private readonly object _disposeGate = new();
    private bool _disposeRestoreAbandoned;

    private string? _lastSignature;
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
        Poser.Application.Integration.ActorIntegrationSession integration,
        Poser.Game.Animation.AnimationRuntimePort animationPort,
        IEventBus events,
        IFramework framework)
    {
        _bindings = bindings;
        _scene = scene;
        _gestures = gestures;
        _history = history;
        _animation = animation;
        _presentation = presentation;
        _integration = integration;
        _animationPort = animationPort;
        _events = events;
        _framework = framework;
        _events.Subscribe<ActorListChangedEvent>(OnActorListChanged);
        _events.Subscribe<LightListChangedEvent>(OnLightListChanged);
        _events.Subscribe<CameraListChangedEvent>(OnCameraListChanged);
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
                _animation.ResumeCommands();
                _animation.ResetAll();
                _presentation.ResetAll();
                _integration.ResetAll();
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
                        _animation.ResumeCommands();
                        _animation.ResetAll();
                        _presentation.ResetAll();
                        _integration.ResetAll();
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
        var snapshot = _bindings.Refresh();
        // The refresh is the only place that resolves a whole skeleton's bone
        // names at once, so it owns the flush of whatever those lookups found
        // untranslated. One line per refresh instead of one per bone: a modded
        // 400-bone character used to pay hundreds of synchronous log writes on
        // this exact tick. No-op when nothing new was seen.
        Poser.Core.BoneInfo.BoneInfoService.FlushUntranslatedLog();
        // One structural signature coalesces every refresh source (events,
        // retries, session transitions): identical scenes publish nothing —
        // no snapshot churn, no revision increment, no gesture cancellation.
        var signature = Signature(snapshot);
        _retryPending = snapshot.Actors.Any(
            actor => actor.CharacterSkeleton == null);
        if (!_retryPending)
            _retryInterval = InitialRetryInterval;
        if (signature == _lastSignature)
            return;
        _lastSignature = signature;
        _retryInterval = InitialRetryInterval;

        _scene.Refresh(snapshot);
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
        _integration.Reconcile(_scene.Snapshot);
        _animationPort.SyncEnforcementIndex();
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

    private static string Signature(Poser.Domain.Scene.SceneSnapshot snapshot)
    {
        var builder = new StringBuilder();
        foreach (var actor in snapshot.Actors)
        {
            builder.Append(actor.Id.LogicalId);
            builder.Append(':');
            builder.Append(actor.Id.Generation);
            builder.Append(':');
            // Slot presence and each slot's structural identity participate
            // in the signature: an appearing/vanishing/replaced slot is a
            // structural change; identical scenes still publish nothing.
            foreach (var skeleton in actor.Skeletons)
            {
                builder.Append((int)skeleton.Id.Slot);
                builder.Append('=');
                builder.Append(skeleton.Id.Generation);
                builder.Append(':');
                builder.Append(skeleton.Bones.Count);
                builder.Append(',');
            }
            builder.Append('|');
        }
        // Lights participate structurally: without their name/kind/on state a
        // spawn, rename, or toggle would coalesce away and never publish.
        foreach (var light in snapshot.Lights)
        {
            builder.Append(light.Id.LogicalId);
            builder.Append(':');
            builder.Append(light.Id.Generation);
            builder.Append(':');
            builder.Append(light.Name);
            builder.Append(':');
            builder.Append((int)light.Kind);
            builder.Append(':');
            builder.Append(light.IsOn ? '1' : '0');
            builder.Append('|');
        }
        // Cameras participate for the same reason: a create, rename, or live
        // switch must publish a new revision.
        foreach (var camera in snapshot.Cameras)
        {
            builder.Append(camera.Id.LogicalId);
            builder.Append(':');
            builder.Append(camera.Id.Generation);
            builder.Append(':');
            builder.Append(camera.Name);
            builder.Append(':');
            builder.Append((int)camera.Kind);
            builder.Append(':');
            builder.Append(camera.IsLive ? '1' : '0');
            builder.Append('|');
        }
        return builder.ToString();
    }

    private void OnActorListChanged(ActorListChangedEvent _) =>
        Refresh();

    private void OnLightListChanged(LightListChangedEvent _) =>
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
            // Leaving GPose is the last moment the actors Poser overrode
            // are still resolvable, so every animation override is put
            // back here rather than dropped when they disappear.
            _animation.ResetAll();
            _presentation.ResetAll();
            _integration.ResetAll();
        }
        Refresh();
    }
}
