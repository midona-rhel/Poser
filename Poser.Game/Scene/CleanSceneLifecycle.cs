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
    private readonly IEventBus _events;
    private readonly IFramework _framework;

    private static readonly TimeSpan SlotPollInterval = TimeSpan.FromSeconds(1);

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
        IEventBus events,
        IFramework framework)
    {
        _bindings = bindings;
        _scene = scene;
        _gestures = gestures;
        _history = history;
        _events = events;
        _framework = framework;
        _events.Subscribe<ActorListChangedEvent>(OnActorListChanged);
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
        _events.Unsubscribe<SkeletonChangedEvent>(OnSkeletonChanged);
        _events.Unsubscribe<GPoseStateChangedEvent>(OnGPoseChanged);
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
        return builder.ToString();
    }

    private void OnActorListChanged(ActorListChangedEvent _) =>
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
        }
        Refresh();
    }
}
