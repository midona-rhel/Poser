using System;
using Dalamud.Plugin.Services;
using Poser.Application.Animation;
using Poser.Application.Lifecycle;
using Poser.Application.Operations;
using Poser.Application.Scene;
using Poser.Domain.Animation;
using Poser.Domain.Identity;
using Poser.Domain.Scene;
using Poser.Game.Bindings;

namespace Poser.Game.Animation;

/// <summary>Lets a facial timeline evaluate for two validated game frames.</summary>
public sealed class ExpressionHoldCoordinator : IDisposable
{
    private const int SettleTicks = 2;

    private sealed record PendingHold(
        ActorId Actor,
        SkeletonId Skeleton,
        SessionGeneration Session,
        ushort Timeline,
        int Ticks);

    private readonly IFramework _framework;
    private readonly StableBindingRegistry _bindings;
    private readonly SceneSession _scene;
    private readonly AnimationSession _animation;
    private readonly ISessionGenerationSource _sessionGeneration;
    private readonly IPluginLog _log;
    private PendingHold? _pending;
    private bool _disposed;

    public ExpressionHoldCoordinator(
        IFramework framework,
        StableBindingRegistry bindings,
        SceneSession scene,
        AnimationSession animation,
        ISessionGenerationSource sessionGeneration,
        IPluginLog log)
    {
        _framework = framework;
        _bindings = bindings;
        _scene = scene;
        _animation = animation;
        _sessionGeneration = sessionGeneration;
        _log = log;
        _framework.Update += OnFrameworkUpdate;
    }

    public bool IsPendingFor(ActorId actor) => _pending?.Actor == actor;

    /// <summary>Starts the selected expression and reserves its validated hold.</summary>
    public AnimationResult Begin(ActorId actor, ushort timeline)
    {
        if (_disposed)
            return AnimationResult.Fail("Expression preview is unavailable.");
        if (!_framework.IsInFrameworkUpdateThread)
            return AnimationResult.Fail(
                "Expression preview must start on the framework thread.");
        if (_pending != null)
            return AnimationResult.Fail("An expression preview is already settling.");
        if (_sessionGeneration.ActiveSessionGeneration is not { } session)
            return AnimationResult.Fail(
                "Expression preview requires an active application session.");
        if (FindActor(actor)?.CharacterSkeleton is not { } skeleton)
            return AnimationResult.Fail(
                "The actor has no current character skeleton.");
        if (_bindings.Resolve(actor) is not { Success: true })
            return AnimationResult.Fail("The actor binding is stale.");

        var begun = _animation.BeginExpressionHold(actor, timeline);
        if (!begun.Success)
            return begun;
        _pending = new PendingHold(
            actor, skeleton.Id, session, timeline, SettleTicks);
        _log.Debug(
            $"Expression preview started actor={actor} timeline={timeline}.");
        return AnimationResult.Ok();
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        if (_disposed || _pending is not { } pending)
            return;
        if (Validate(pending) is { } stale)
        {
            _pending = null;
            _animation.AbandonExpressionHold(pending.Actor);
            _log.Warning(stale);
            return;
        }

        pending = pending with { Ticks = pending.Ticks - 1 };
        _pending = pending;
        if (pending.Ticks > 0)
            return;

        _pending = null;
        var completed = _animation.CompleteExpressionHold(
            pending.Actor, pending.Timeline);
        if (!completed.Success)
        {
            _animation.AbandonExpressionHold(
                pending.Actor, discardRestorePoint: false);
            _log.Warning(
                completed.Detail ?? "Expression preview could not be held.");
        }
        else
            _log.Debug(
                $"Expression preview held actor={pending.Actor} " +
                $"timeline={pending.Timeline}.");
    }

    private string? Validate(PendingHold pending)
    {
        if (_sessionGeneration.ActiveSessionGeneration is not { } active ||
            active != pending.Session)
            return "Expression preview cancelled because its session changed.";
        if (FindActor(pending.Actor)?.CharacterSkeleton is not { } skeleton ||
            skeleton.Id != pending.Skeleton)
            return "Expression preview cancelled because its skeleton changed.";
        if (_bindings.Resolve(pending.Actor) is not { Success: true })
            return "Expression preview cancelled because its actor binding changed.";
        if (!_animation.IsSupported(pending.Actor))
            return "Expression preview cancelled because animation became unavailable.";
        if (_animation.SelectedFor(pending.Actor, AnimationSlot.Facial) !=
            pending.Timeline)
            return "Expression preview cancelled because its selection changed.";
        return null;
    }

    private ActorDescriptor? FindActor(ActorId actor)
    {
        foreach (var candidate in _scene.Snapshot.Actors)
            if (candidate.Id == actor)
                return candidate;
        return null;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        if (_pending is { } pending)
            _animation.AbandonExpressionHold(pending.Actor);
        _pending = null;
        _framework.Update -= OnFrameworkUpdate;
    }
}
