using System;
using Dalamud.Plugin.Services;
using Poser.Application.Animation;
using Poser.Application.Lifecycle;
using Poser.Application.Operations;
using Poser.Application.Scene;
using Poser.Domain.Identity;
using Poser.Domain.Scene;
using Poser.Game.Bindings;

namespace Poser.Game.Animation;

/// <summary>Completes a held-expression reset after two validated game frames.</summary>
public sealed class ExpressionReleaseCoordinator : IDisposable
{
    private const int RestoreDelayTicks = 2;

    private sealed record PendingRelease(
        ActorId Actor,
        SkeletonId Skeleton,
        SessionGeneration Session,
        int Ticks);

    private readonly IFramework _framework;
    private readonly StableBindingRegistry _bindings;
    private readonly SceneSession _scene;
    private readonly AnimationSession _animation;
    private readonly ISessionGenerationSource _sessionGeneration;
    private readonly IPluginLog _log;
    private PendingRelease? _pending;
    private bool _disposed;

    public ExpressionReleaseCoordinator(
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

    public event Action<ActorId, AnimationResult>? Completed;

    public bool IsPendingFor(ActorId actor) => _pending?.Actor == actor;

    /// <summary>Starts Straight Face and reserves the exact delayed restore.</summary>
    public AnimationResult Begin(ActorId actor)
    {
        if (_disposed)
            return AnimationResult.Fail("Expression reset is unavailable.");
        if (!_framework.IsInFrameworkUpdateThread)
            return AnimationResult.Fail(
                "Expression reset must start on the framework thread.");
        if (_pending != null)
            return AnimationResult.Fail("An expression reset is already pending.");
        if (_sessionGeneration.ActiveSessionGeneration is not { } session)
            return AnimationResult.Fail(
                "Expression reset requires an active application session.");
        if (FindActor(actor)?.CharacterSkeleton is not { } skeleton)
            return AnimationResult.Fail(
                "The actor has no current character skeleton.");
        if (_bindings.Resolve(actor) is not { Success: true })
            return AnimationResult.Fail("The actor binding is stale.");

        var begun = _animation.BeginExpressionRelease(actor);
        if (!begun.Success)
            return begun;
        _pending = new PendingRelease(
            actor, skeleton.Id, session, RestoreDelayTicks);
        return AnimationResult.Ok();
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        if (_disposed || _pending is not { } pending)
            return;
        if (Validate(pending) is { } stale)
        {
            Finish(pending, AnimationResult.Fail(stale));
            return;
        }
        pending = pending with { Ticks = pending.Ticks - 1 };
        _pending = pending;
        if (pending.Ticks > 0)
            return;

        Finish(pending, _animation.CompleteExpressionRelease(pending.Actor));
    }

    private string? Validate(PendingRelease pending)
    {
        if (_sessionGeneration.ActiveSessionGeneration is not { } active ||
            active != pending.Session)
            return "Expression reset cancelled because its session changed.";
        if (FindActor(pending.Actor)?.CharacterSkeleton is not { } skeleton ||
            skeleton.Id != pending.Skeleton)
            return "Expression reset cancelled because the skeleton changed.";
        if (_bindings.Resolve(pending.Actor) is not { Success: true })
            return "Expression reset cancelled because the actor binding changed.";
        if (!_animation.IsSupported(pending.Actor))
            return "Expression reset cancelled because animation became unavailable.";
        return null;
    }

    private void Finish(PendingRelease pending, AnimationResult result)
    {
        _pending = null;
        if (!result.Success)
            _log.Warning(result.Detail ?? "Expression reset failed.");
        Completed?.Invoke(pending.Actor, result);
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
        _pending = null;
        _framework.Update -= OnFrameworkUpdate;
    }
}
