using Dalamud.Plugin.Services;
using Poser.Application.Scene;
using Poser.Application.Transforms;
using Poser.Domain;
using Poser.Domain.Scene;
using Poser.Game.Bindings;
using Poser.Application.Events;
using Poser.Game.Core;

namespace Poser.Game.Scene;

/// <summary>
/// Owns native discovery refresh and clean GPose-session teardown. Skeleton
/// discovery belongs HERE, not to any inspector section: an actor whose draw
/// object or Havok skeleton is not ready at first discovery is retried on
/// the framework thread at a bounded backoff cadence while it remains
/// present. Every notification only requests a refresh; the framework
/// update runs at most one per frame, never inside the publisher (which may be
/// a native hook). A refresh that finds no structural change publishes
/// nothing, increments no scene revision, and cancels no active transform
/// gesture.
/// </summary>
public sealed class CleanSceneLifecycle : IDisposable
{
    private static readonly TimeSpan InitialRetryInterval = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan MaxRetryInterval = TimeSpan.FromSeconds(5);

    private readonly StableBindingRegistry _bindings;
    private readonly SceneSession _scene;
    private readonly TransformGestureService _gestures;
    private readonly TransformHistory _history;
    private readonly GroupTransformCoordinator? _groupCoordinator;
    private readonly IGroupTransformSource? _groupSource;
    private readonly Poser.Application.Animation.AnimationSession _animation;
    private readonly Poser.Application.Presentation.ActorPresentationSession _presentation;
    private readonly Poser.Application.Appearance.ActorModelIdSession _modelId;
    private readonly Poser.Application.Integration.IntegrationReset _integration;
    private readonly Poser.Game.Animation.AnimationRuntimePort _animationPort;
    private readonly Poser.Game.Animation.FacialPoseCapture _facialCapture;
    private readonly GazeService _gaze;
    private readonly IEventBus _events;
    private readonly IFramework _framework;
    private readonly Application.Settings.ConfigurationService _configuration;
    private readonly SceneGroups _groups;
    private readonly GroupTransformState _groupTransforms;

    /// <summary>The user-facing notice for a teardown that left owned state
    /// behind. Null under tests; the log line is written either way.</summary>
    private readonly Poser.Application.Presentation.IUserNotices? _notices;

    private static readonly TimeSpan SlotPollInterval = TimeSpan.FromSeconds(1);

    private readonly object _disposeGate = new();
    private bool _disposeRestoreAbandoned;

    private SceneSnapshot? _lastSignature;
    /// <summary>The bone-free fingerprint of the scene the last refresh
    /// settled on; the idle poll rebuilds only when it moves. Null after a
    /// rejected candidate, so the next poll retries.</summary>
    private IReadOnlyList<object?>? _idleSignature;
    private readonly CoalescedRefresh _refreshQueue = new();
    private bool _retryPending;
    private TimeSpan _retryInterval = TimeSpan.FromMilliseconds(500);
    private DateTime _nextRetryUtc = DateTime.MinValue;
    private DateTime _nextSlotPollUtc = DateTime.MinValue;

    /// <summary>Breadcrumbs for binding publication. The interesting line is
    /// the one that says a refresh published NOTHING: bone bindings only ever
    /// change here, so an actor whose bones never resolve is an actor this
    /// pass coalesced away.</summary>
    private readonly IPluginLog? _log;

    public CleanSceneLifecycle(
        StableBindingRegistry bindings,
        SceneSession scene,
        TransformGestureService gestures,
        TransformHistory history,
        Poser.Application.Animation.AnimationSession animation,
        Poser.Application.Presentation.ActorPresentationSession presentation,
        Poser.Application.Appearance.ActorModelIdSession modelId,
        Poser.Application.Integration.IntegrationReset integration,
        Poser.Game.Animation.AnimationRuntimePort animationPort,
        Poser.Game.Animation.FacialPoseCapture facialCapture,
        GazeService gaze,
        IEventBus events,
        IFramework framework,
        Application.Settings.ConfigurationService configuration,
        SceneGroups groups,
        GroupTransformState groupTransforms,
        IPluginLog? log = null,
        Poser.Application.Presentation.IUserNotices? notices = null,
        GroupTransformCoordinator? groupCoordinator = null,
        IGroupTransformSource? groupSource = null)
    {
        _log = log;
        _notices = notices;
        _groups = groups;
        _groupTransforms = groupTransforms;
        _bindings = bindings;
        _scene = scene;
        _gestures = gestures;
        _history = history;
        _groupCoordinator = groupCoordinator;
        _groupSource = groupSource;
        _animation = animation;
        _presentation = presentation;
        _modelId = modelId;
        _integration = integration;
        _animationPort = animationPort;
        _facialCapture = facialCapture;
        _gaze = gaze;
        _events = events;
        _framework = framework;
        _configuration = configuration;
        _events.Subscribe<ActorListChangedEvent>(OnActorListChanged);
        _events.Subscribe<LightListChangedEvent>(OnLightListChanged);
        _events.Subscribe<CameraListChangedEvent>(OnCameraListChanged);
        _events.Subscribe<PropListChangedEvent>(OnPropListChanged);
        _events.Subscribe<OverlayNodeListChangedEvent>(OnOverlayListChanged);
        _events.Subscribe<WorldObjectListChangedEvent>(OnWorldObjectListChanged);
        _events.Subscribe<SkeletonChangedEvent>(OnSkeletonChanged);
        _events.Subscribe<GPoseStateChangedEvent>(OnGPoseChanged);
        _events.Subscribe<GPoseExitingEvent>(OnGPoseExiting);
        // Discovery, retries, and refreshes all run on the framework thread:
        // the registry refresh reads native skeleton data and shared
        // bone-name state, while events publish from the framework thread —
        // a concurrent ctor-thread refresh corrupted shared collections.
        _framework.Update += OnFrameworkUpdate;
        _refreshQueue.Request();
    }

    public void Dispose()
    {
        _refreshQueue.Stop();
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
        _events.Unsubscribe<GPoseExitingEvent>(OnGPoseExiting);

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

        // Groups are plain managed state, so they end with the plugin
        // whether or not the reset above ran: the bounded hop can abandon
        // it. Past the gate, a reset that did run has already finished.
        try
        {
            _groups.Clear();
            _groupTransforms.Clear();
        }
        catch
        {
            // Disposal must not throw.
        }
    }

    private void Refresh()
    {
        var refreshWatch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            RefreshCore();
        }
        finally
        {
            refreshWatch.Stop();
            if (refreshWatch.Elapsed.TotalMilliseconds > 2.0)
                _log?.Debug(
                    "Scene bindings: refresh took " +
                    $"{refreshWatch.Elapsed.TotalMilliseconds:0.0}ms");
        }
    }

    private void RequestRefresh() => _refreshQueue.Request();

    private void RefreshCore()
    {
        // Taken before the candidate: reading the skeletons here can rebuild
        // one, and the candidate below must already include that rebuild.
        var idle = _bindings.IdleSignature();
        _idleSignature = null;
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
            Poser.Domain.Posing.BoneInfo.BoneInfoService.FlushUntranslatedLog();
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
            {
                _idleSignature = idle;
                return;
            }

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
            _groupCoordinator?.BindingsPublished();
            admitted = true;
            _log?.Debug(
                "Scene bindings: published " +
                string.Join(", ", candidate.Actors.Select(actor =>
                    $"[{actor.Name} skeleton {(actor.CharacterSkeleton is null ? "none" : "bound")}]")));

            // A rejected candidate is deliberately retried: recording its
            // signature would coalesce away the correction opportunity.
            _lastSignature = signature;
            _idleSignature = idle;
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
            _history.Reconcile(
                _scene.Contains,
                target => _groupSource?.CurrentTarget(target) ?? _bindings.CurrentTarget(target));
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
            // Gaze is keyed by the same exact generations; a replaced body
            // must not keep the previous one's gaze.
            _gaze.Reconcile();
        }
        finally
        {
            if (!admitted)
                _bindings.AbortCandidate(staged);
        }
    }

    /// <summary>
    /// The one place a refresh runs: at most one per frame, for whatever
    /// requested it since the last. Also the bounded retry pump for actors
    /// whose skeletons were not ready at discovery (0.5 s doubling to 5 s
    /// while such an actor remains present) and the idle poll.
    /// </summary>
    private void OnFrameworkUpdate(IFramework framework)
    {
        var now = DateTime.UtcNow;
        if (_retryPending)
        {
            if (now >= _nextRetryUtc)
            {
                _nextRetryUtc = now + _retryInterval;
                var doubled = _retryInterval + _retryInterval;
                _retryInterval = doubled > MaxRetryInterval ? MaxRetryInterval : doubled;
                RequestRefresh();
            }
        }
        else if (now >= _nextSlotPollUtc)
        {
            // Auxiliary slot changes (sheathe/unsheathe, equipment or prop
            // replacement, ornament spawn/despawn) and some row fields fire
            // none of our events, so they are polled — through the bone-free
            // signature, so an unchanged scene costs no rebuild at all.
            _nextSlotPollUtc = now + SlotPollInterval;
            if (!StableBindingRegistry.SameIdleSignature(
                    _idleSignature, _bindings.IdleSignature()))
                RequestRefresh();
        }
        _refreshQueue.Drain(Refresh);
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
        RequestRefresh();

    private void OnLightListChanged(LightListChangedEvent _) =>
        RequestRefresh();

    private void OnPropListChanged(PropListChangedEvent _) =>
        RequestRefresh();

    private void OnOverlayListChanged(OverlayNodeListChangedEvent _) =>
        RequestRefresh();

    /// <summary>Borrowing a map object and releasing it both move the scene,
    /// and this event was published from the first day with nothing listening:
    /// an adopted object appeared only if some unrelated list happened to
    /// change and kick a refresh.</summary>
    private void OnWorldObjectListChanged(WorldObjectListChangedEvent _) =>
        RequestRefresh();

    private void OnCameraListChanged(CameraListChangedEvent _) =>
        RequestRefresh();

    private void OnSkeletonChanged(SkeletonChangedEvent _) =>
        RequestRefresh();

    /// <summary>
    /// Leaving GPose is the last chance to write into the actors Poser
    /// overrode, so everything owned is put back here rather than dropped
    /// when they disappear. GPoseService publishes this after the final
    /// capture and before the state change, so it runs while every spawn,
    /// clone and binding is still alive: the state-change subscribers that
    /// unbind actors and destroy spawns subscribed first (DI order) and
    /// would otherwise run before this restore. Normal exit and plugin
    /// unload share this edge. The game's own GPose actors can still be gone
    /// already (the edge is observed after IsGPosing flips), so the MCDF
    /// teardown keeps its by-name Glamourer release for them.
    /// </summary>
    private void OnGPoseExiting(GPoseExitingEvent _)
    {
        _refreshQueue.Cancel();
        if (_gestures.ActiveGesture is { } gesture)
            _gestures.Cancel(gesture);
        _history.Clear();
        // GPoseService captured the final save before this exit notification.
        _configuration.ResetSessionNames();
        ResetOwnedState("GPose exited.");
    }

    private void OnGPoseChanged(GPoseStateChangedEvent e)
    {
        // Lineages are per native object; a finished session's can never
        // match again, and keeping them grew the registry for good.
        if (!e.IsGPosing)
            _bindings.ResetLineages();
        RequestRefresh();
    }

    private void ResetOwnedState(string reason) =>
        ResetOwnedStateForLifecycle(
            reason,
            detail =>
            {
                // The receipt is the capture's last operation, not this
                // cancel's result; a cancel has nothing of its own to fail.
                _facialCapture.CancelPending(detail);
                return null;
            },
            () => Failure(_animation.ResetAll()),
            () => Failure(_presentation.ResetAll()),
            () => Failure(_modelId.ResetAll()),
            () => Failure(_integration.ResetAll().Outcome),
            () =>
            {
                // Groups are scene state: they end with the session and the
                // plugin, like every entity they hold.
                _groups.Clear();
                _groupTransforms.Clear();
                return null;
            },
            message => Report(reason, message));

    private static string? Failure(Outcome result) =>
        result.Success ? null : result.Detail ?? "failed";

    /// <summary>One named teardown step; returns its failure, or null.</summary>
    internal readonly record struct TeardownStep(string Name, Func<string?> Run);

    /// <summary>
    /// Runs every step in order and collects each failure under its name. A
    /// throwing step is a failure like any other and does not skip the
    /// steps after it; nothing is retried here.
    /// </summary>
    internal static IReadOnlyList<string> RunSteps(IReadOnlyList<TeardownStep> steps)
    {
        var failures = new List<string>();
        foreach (var step in steps)
        {
            string? failure;
            try
            {
                failure = step.Run();
            }
            catch (Exception ex)
            {
                failure = ex.Message;
            }
            if (failure != null)
                failures.Add($"{step.Name}: {failure}");
        }
        return failures;
    }

    private void Report(string reason, IReadOnlyList<string> failures)
    {
        if (failures.Count > 0)
            Report(reason, string.Join(" | ", failures));
    }

    private void Report(string reason, string message)
    {
        _log?.Error($"Scene teardown ({reason}) left owned state unrestored: {message}");
        try
        {
            _notices?.Failed($"Restoring the scene failed: {message}");
        }
        catch (Exception)
        {
            // The notice surface may already be gone at unload; the log has it.
        }
    }

    /// <summary>One teardown order for GPose exit and plugin disposal. Every
    /// step runs; their failures are reported once, together, in this order.
    /// </summary>
    internal static IReadOnlyList<string> ResetOwnedStateForLifecycle(
        string reason,
        Func<string, string?> cancelFacialCapture,
        Func<string?> resetAnimation,
        Func<string?> resetPresentation,
        Func<string?> resetModelId,
        Func<string?> resetIntegration,
        Func<string?> clearGroups,
        Action<string> report)
    {
        var failures = RunSteps(
        [
            new("Facial capture", () => cancelFacialCapture(reason)),
            new("Animation", resetAnimation),
            new("Presentation", resetPresentation),
            new("Model id", resetModelId),
            new("Appearance", resetIntegration),
            new("Groups", clearGroups),
        ]);
        if (failures.Count > 0)
            report(string.Join(" | ", failures));
        return failures;
    }
}
