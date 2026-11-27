using Dalamud.Plugin.Services;
using Poser.Application.Scene;
using Poser.Application.Transforms;
using Poser.Domain;
using Poser.Domain.Scene;
using Poser.Game.Bindings;
using Poser.Application.Events;
using Poser.Game.Appearance;
using Poser.Game.Core;

namespace Poser.Game.Scene;

/// <summary>
/// Keeps Poser's lists of actors, skeletons and objects matched to the game,
/// on the game's main thread, and puts back what Poser changed when GPose ends
/// or Poser unloads. An event only asks for a refresh; the refresh runs on the
/// next frame. A skeleton that isn't there yet is looked for again, less often
/// each time. A refresh that finds nothing new does not interrupt a drag.
/// </summary>
public sealed class SceneRefreshAndCleanup : IDisposable
{
    private static readonly TimeSpan InitialRetryInterval = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan MaxRetryInterval = TimeSpan.FromSeconds(5);

    private readonly StableBindingRegistry _bindings;
    private readonly SceneSession _scene;
    private readonly TransformGestureService _gestures;
    private readonly EditHistory _history;
    private readonly GroupTransformCoordinator? _groupCoordinator;
    private readonly Poser.Game.Animation.AnimationSession _animation;
    private readonly Poser.Application.Presentation.ActorPresentationSession _presentation;
    private readonly ActorModelIdSession _modelId;
    private readonly Poser.Application.Integration.IntegrationReset _integration;
    private readonly Poser.Game.Animation.AnimationSpeedRuntimePort _animationPort;
    private readonly Poser.Game.Animation.FacialPoseCapture _facialCapture;
    private readonly GazeService _gaze;
    private readonly Poser.Application.Appearance.IInvisibleSkinService _clothingOnly;
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
    /// <summary>A summary, without bones, of the scene the last refresh took.
    /// The once-a-second check only refreshes when it differs. Cleared when a
    /// new picture was refused, so the next check tries again.</summary>
    private IReadOnlyList<object?>? _idleSignature;
    private readonly CoalescedRefresh _refreshQueue = new();
    private bool _friendlyBoneNames;
    private bool _retryPending;
    private TimeSpan _retryInterval = TimeSpan.FromMilliseconds(500);
    private DateTime _nextRetryUtc = DateTime.MinValue;
    private DateTime _nextSlotPollUtc = DateTime.MinValue;

    /// <summary>Optional timing and binding-publication diagnostics.</summary>
    private readonly IPluginLog? _log;

    public SceneRefreshAndCleanup(
        StableBindingRegistry bindings,
        SceneSession scene,
        TransformGestureService gestures,
        EditHistory history,
        Poser.Game.Animation.AnimationSession animation,
        Poser.Application.Presentation.ActorPresentationSession presentation,
        ActorModelIdSession modelId,
        Poser.Application.Integration.IntegrationReset integration,
        Poser.Game.Animation.AnimationSpeedRuntimePort animationPort,
        Poser.Game.Animation.FacialPoseCapture facialCapture,
        GazeService gaze,
        Poser.Application.Appearance.IInvisibleSkinService clothingOnly,
        IEventBus events,
        IFramework framework,
        Application.Settings.ConfigurationService configuration,
        SceneGroups groups,
        GroupTransformState groupTransforms,
        IPluginLog? log = null,
        Poser.Application.Presentation.IUserNotices? notices = null,
        GroupTransformCoordinator? groupCoordinator = null)
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
        _animation = animation;
        _presentation = presentation;
        _modelId = modelId;
        _integration = integration;
        _animationPort = animationPort;
        _facialCapture = facialCapture;
        _gaze = gaze;
        _clothingOnly = clothingOnly;
        _events = events;
        _framework = framework;
        _configuration = configuration;
        _friendlyBoneNames = configuration.Config.Skeleton.ShowFriendlyBoneNames;
        _configuration.OnConfigurationChanged += OnConfigurationChanged;
        _events.Subscribe<ActorListChangedEvent>(OnActorListChanged);
        _events.Subscribe<LightListChangedEvent>(OnLightListChanged);
        _events.Subscribe<CameraListChangedEvent>(OnCameraListChanged);
        _events.Subscribe<PropListChangedEvent>(OnPropListChanged);
        _events.Subscribe<OverlayNodeListChangedEvent>(OnOverlayListChanged);
        _events.Subscribe<WorldObjectListChangedEvent>(OnWorldObjectListChanged);
        _events.Subscribe<SkeletonChangedEvent>(OnSkeletonChanged);
        _events.Subscribe<GPoseStateChangedEvent>(OnGPoseChanged);
        _events.Subscribe<GPoseExitingEvent>(OnGPoseExiting);
        // Every refresh runs on the main thread, in the frame update: it reads
        // the game's skeletons and shared bone names, and the events that ask
        // for it come from the main thread too. A refresh from another thread
        // once corrupted those shared lists.
        _framework.Update += OnFrameworkUpdate;
        _refreshQueue.Request();
    }

    public void Dispose()
    {
        _refreshQueue.Stop();
        _configuration.OnConfigurationChanged -= OnConfigurationChanged;
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
                ResetOwnedState("Poser is shutting down.");
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
                        ResetOwnedState("Poser is shutting down.");
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
            // The main thread can't be reached at shutdown when the game is
            // closing too; then there is nothing left to put back.
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
        // Read before building the new picture: reading the skeletons can make
        // the game rebuild one, and the picture below must include that.
        var idle = _bindings.IdleSignature();
        _idleSignature = null;
        var staged = _bindings.RefreshCandidate();
        var candidate = staged.Snapshot;
        var admitted = false;
        try
        {
            // Combine newly encountered untranslated bone names into one log write.
            Poser.Domain.Posing.BoneInfo.BoneInfoService.FlushUntranslatedLog();
            // Skip the refresh only when nothing changed, preview bodies
            // included. Pose-import previews are tracked but are not part of the
            // scene picture, so the picture alone would miss them.
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

            // SceneSession decides whether the new picture is taken; the lookup
            // from ids to game objects is updated only after it is. When nothing
            // changed, the lookup still checks each id and generation against
            // the picture before it updates.
            _bindings.CommitCandidate(staged, _scene.Snapshot);
            // Retire clothing-only requests against the newly published actor list.
            _clothingOnly.Reconcile(actor => _scene.Snapshot.FindActor(actor) != null);
            _groupCoordinator?.BindingsPublished();
            admitted = true;
            _log?.Debug(
                "Scene bindings: published " +
                string.Join(", ", candidate.Actors.Select(actor =>
                    $"[{actor.Name} skeleton {(actor.CharacterSkeleton is null ? "none" : "bound")}]")));

            // A refused picture is tried again on purpose. Remembering it here
            // would make the next refresh think nothing changed.
            _lastSignature = signature;
            _idleSignature = idle;
            _retryInterval = InitialRetryInterval;
            if (!result.StateChanged)
                return;

            // A drag whose targets are all still in the scene, in the same
            // generation, carries on: other actors may come and go during it.
            // A drag with a target that is gone or was remade is cancelled once,
            // with no undo step. Each undo step is checked the same way.
            _gestures.ReconcileScene(_scene.Contains);
            _history.Reconcile(
                _scene.Contains,
                _scene.CurrentTransformTarget,
                _scene.IsTransformTargetPending);
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
    /// The one place a refresh runs: at most one per frame, for whatever asked
    /// for it since the last. It also retries actors whose skeletons were not
    /// ready when they were found, every 0.5 s at first and doubling up to 5 s
    /// while such an actor is there, and runs the once-a-second check.
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
    /// The scene picture with its revision number left out, for telling whether
    /// anything changed. Two are equal when everything matches: ids, bones and
    /// how they connect, parents, environment, gaze and every camera
    /// value.
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

    /// <summary>Refreshes scene bindings after world objects are acquired or released.</summary>
    private void OnWorldObjectListChanged(WorldObjectListChangedEvent _) =>
        RequestRefresh();

    private void OnCameraListChanged(CameraListChangedEvent _) =>
        RequestRefresh();

    private void OnSkeletonChanged(SkeletonChangedEvent _) =>
        RequestRefresh();

    /// <summary>Bone descriptors retain their display strings in the
    /// committed scene snapshot. A saved toggle or reset therefore needs a
    /// descriptor refresh on the next framework frame; the one-second idle
    /// signature is only a fallback, not acceptable UI latency.</summary>
    private void OnConfigurationChanged()
    {
        bool friendly = _configuration.Config.Skeleton.ShowFriendlyBoneNames;
        if (_friendlyBoneNames == friendly)
            return;
        _friendlyBoneNames = friendly;
        RequestRefresh();
    }

    /// <summary>
    /// Restores owned state after the final save capture and before state-change
    /// subscribers unbind actors or destroy spawns. Native GPose actors may
    /// already be gone when this notification arrives, so appearance teardown
    /// also supports releasing provider ownership by name.
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
                // The capture's result describes its own last run, not this
                // cancel, so it is not reported as this cancel's failure.
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
            // The notice window may already be gone when Poser unloads; the
            // log still has the message.
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
