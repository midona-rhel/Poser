using Dalamud.Plugin.Services;
using Poser.Application.Animation;
using Poser.Application.Lifecycle;
using Poser.Application.Scene;
using Poser.Domain.Animation;
using Poser.Domain.Identity;
using Poser.Domain.Operations;
using Poser.Domain.Scene;
using Poser.Entities;
using Poser.Services;

namespace Poser.Game.Animation;

/// <summary>Owns the native lifetime of held-expression retries and bake admission.</summary>
public sealed class ExpressionPreview : IExpressionPreview, IDisposable
{
    private readonly IFramework _framework;
    private readonly IEntityBindings _bindings;
    private readonly ISessionGenerationSource _sessions;
    private readonly SceneSession _scene;
    private readonly AnimationSession _animation;
    private readonly IFacialPoseCapture _capture;
    private readonly TimeProvider _clock;
    private readonly Dictionary<ActorId, TimelineEntry> _choices = new();
    private PendingRetry? _pending;
    private bool _disposed;

    private sealed record PendingRetry(
        ActorId Actor, TimelineEntry Entry, SessionGeneration Session,
        IActor Binding, long StartedAt);

    public ExpressionPreview(
        IFramework framework, IEntityBindings bindings,
        ISessionGenerationSource sessions, SceneSession scene,
        AnimationSession animation, IFacialPoseCapture capture,
        TimeProvider? clock = null)
    {
        _framework = framework;
        _bindings = bindings;
        _sessions = sessions;
        _scene = scene;
        _animation = animation;
        _capture = capture;
        _clock = clock ?? TimeProvider.System;
        _framework.Update += OnUpdate;
        _scene.SceneChanged += OnSceneChanged;
    }

    public event Action<string>? Failed;
    public bool IsPending(ActorId actor) => _pending?.Actor == actor;
    public bool IsBaking => _capture.IsPending;

    private IActor? Resolve(ActorId actor) =>
        !_disposed && _framework.IsInFrameworkUpdateThread &&
        _sessions.ActiveSessionGeneration != null &&
        _scene.Snapshot.FindActor(actor) != null &&
        _bindings.Resolve(actor) is { Success: true, Value: { } binding }
            ? binding : null;

    private static AnimationResult Unavailable() =>
        AnimationResult.Fail("The actor is no longer available for expression editing.");

    public AnimationResult Choose(ActorId actor, TimelineEntry entry)
    {
        if (Resolve(actor) == null)
            return Unavailable();
        CancelRetry(actor);
        var result = _animation.ChooseSlot(actor, AnimationSlot.Facial, (ushort)entry.TimelineId);
        if (result.Success)
            _choices[actor] = entry;
        return result;
    }

    public AnimationResult Preview(ActorId actor, ushort timeline)
    {
        if (Resolve(actor) is not { } binding)
            return Unavailable();
        var result = _animation.HoldExpression(actor, timeline);
        if (!result.Success)
            return result;
        if (_choices.TryGetValue(actor, out var entry) &&
            entry.TimelineId == timeline &&
            _sessions.ActiveSessionGeneration is { } session)
            _pending = new(actor, entry, session, binding, _clock.GetTimestamp());
        return result;
    }

    public AnimationResult Reset(ActorId actor)
    {
        if (Resolve(actor) == null)
            return Unavailable();
        CancelRetry(actor);
        var result = _animation.ReleaseExpression(actor);
        if (result.Success)
            _choices.Remove(actor);
        return result;
    }

    public AnimationResult Bake(ActorId actor, ushort timeline)
    {
        if (Resolve(actor) == null || _scene.Snapshot.FindActor(actor) is not { } descriptor)
            return Unavailable();
        if (IsPending(actor) || IsBaking)
            return AnimationResult.Fail("An expression preview or bake is already pending.");
        var held = _animation.HoldExpression(actor, timeline);
        if (!held.Success)
            return held;
        var captured = _capture.Begin(actor, descriptor);
        return captured.Success ? AnimationResult.Ok() : AnimationResult.Fail(captured.Detail!);
    }

    public void CancelRetry(ActorId actor)
    {
        if (_pending?.Actor == actor)
            _pending = null;
    }

    private void OnSceneChanged(SceneSnapshot snapshot)
    {
        foreach (var actor in _choices.Keys.ToArray())
            if (snapshot.FindActor(actor) == null)
                _choices.Remove(actor);
    }

    private void OnUpdate(IFramework framework)
    {
        if (_disposed || !_framework.IsInFrameworkUpdateThread || _pending is not { } pending)
            return;
        if (_scene.Snapshot.FindActor(pending.Actor) == null)
        {
            _pending = null;
            var released = _animation.ReleaseExpression(pending.Actor);
            if (!released.Success)
                Failed?.Invoke($"Expression reset: {released.Detail}");
            _choices.Remove(pending.Actor);
            return;
        }
        if (_sessions.ActiveSessionGeneration != pending.Session ||
            !ReferenceEquals(Resolve(pending.Actor), pending.Binding) ||
            _animation.SelectedFor(pending.Actor, AnimationSlot.Facial) != pending.Entry.TimelineId ||
            _animation.HeldExpressionFor(pending.Actor) != pending.Entry.TimelineId ||
            !_choices.TryGetValue(pending.Actor, out var choice) || choice != pending.Entry)
        {
            _pending = null;
            return;
        }
        if (_clock.GetElapsedTime(pending.StartedAt).TotalMilliseconds < 500)
            return;

        // The paused client sometimes needs a second evaluation edge. Replay
        // once, only against the same native body; drawing a pane never drives it.
        _pending = null;
        var replayed = _animation.HoldExpression(pending.Actor, (ushort)pending.Entry.TimelineId);
        if (!replayed.Success)
            Failed?.Invoke($"Expression retry: {replayed.Detail}");
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _pending = null;
        _choices.Clear();
        _framework.Update -= OnUpdate;
        _scene.SceneChanged -= OnSceneChanged;
    }
}
