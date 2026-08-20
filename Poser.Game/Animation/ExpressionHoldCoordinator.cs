using System;
using System.Collections.Generic;
using Dalamud.Plugin.Services;
using Poser.Application.Animation;
using Poser.Application.Lifecycle;
using Poser.Application.Operations;
using Poser.Application.Scene;
using Poser.Domain.Animation;
using Poser.Domain.Identity;
using Poser.Domain.Scene;
using Poser.Entities;
using Poser.Game.Bindings;
using Poser.Services;
using LegacyTransform = Poser.Transform;

namespace Poser.Game.Animation;

/// <summary>Holds a facial timeline after its bound face output settles.</summary>
public sealed class ExpressionHoldCoordinator : IDisposable
{
    private const int StableTicks = 2;
    private const int SettleTimeoutTicks = 20;
    private const float StableEpsilon = 1e-5f;

    private sealed class PendingHold
    {
        public required ActorId Actor;
        public required SkeletonId Skeleton;
        public required SessionGeneration Session;
        public required ushort Timeline;
        public required List<BoneId> Bones;
        public required List<LegacyTransform> Baseline;
        public List<LegacyTransform> LastReading = new();
        public int StableRuns;
        public int SettleTicks;
        public bool Changed;
        public bool ReplayedWhilePaused;
    }

    private readonly IFramework _framework;
    private readonly StableBindingRegistry _bindings;
    private readonly SceneSession _scene;
    private readonly AnimationSession _animation;
    private readonly IBonePosingService _posing;
    private readonly ISessionGenerationSource _sessionGeneration;
    private readonly IPluginLog _log;
    private PendingHold? _pending;
    private bool _disposed;

    public ExpressionHoldCoordinator(
        IFramework framework,
        StableBindingRegistry bindings,
        SceneSession scene,
        AnimationSession animation,
        IBonePosingService posing,
        ISessionGenerationSource sessionGeneration,
        IPluginLog log)
    {
        _framework = framework;
        _bindings = bindings;
        _scene = scene;
        _animation = animation;
        _posing = posing;
        _sessionGeneration = sessionGeneration;
        _log = log;
        _framework.Update += OnFrameworkUpdate;
    }

    public bool IsPendingFor(ActorId actor) => _pending?.Actor == actor;

    /// <summary>Cancels this actor's settle drive and restores its facial baseline.</summary>
    public AnimationResult Release(ActorId actor)
    {
        if (_pending is { Actor: var pendingActor } && pendingActor == actor)
        {
            _pending = null;
            _animation.AbandonExpressionHold(
                actor, discardRestorePoint: false);
        }
        return _animation.ReleaseExpression(actor);
    }

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

        var bones = new List<BoneId>();
        foreach (var bone in skeleton.Bones)
            if (IsFaceBone(bone.Id.CanonicalName))
                bones.Add(bone.Id);
        if (bones.Count == 0)
            return AnimationResult.Fail("The actor has no bound face bones.");
        if (!TryReadFace(bones, out var baseline, out var problem))
            return AnimationResult.Fail(
                problem ?? "The actor has no bound face bones.");

        var begun = _animation.BeginExpressionHold(actor, timeline);
        if (!begun.Success)
            return begun;
        _pending = new PendingHold
        {
            Actor = actor,
            Skeleton = skeleton.Id,
            Session = session,
            Timeline = timeline,
            Bones = bones,
            Baseline = baseline,
        };
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

        RequestRawRefresh(pending);
        if (!TryReadFace(pending.Bones, out var reading, out var problem))
        {
            CancelStale(pending,
                $"Expression preview cancelled: {problem}");
            return;
        }

        pending.Changed |= !Settled(reading, pending.Baseline);
        pending.StableRuns = pending.Changed &&
            Settled(reading, pending.LastReading)
            ? pending.StableRuns + 1
            : 0;
        pending.LastReading = reading;
        pending.SettleTicks++;

        // A paused actor can accept the first Facial write without evaluating
        // it. One validated replay reproduces the next-click path internally.
        if (!pending.Changed && !pending.ReplayedWhilePaused &&
            _animation.IsPaused(pending.Actor))
        {
            pending.ReplayedWhilePaused = true;
            var replayed = _animation.BeginExpressionHold(
                pending.Actor, pending.Timeline);
            if (!replayed.Success)
            {
                _pending = null;
                _animation.AbandonExpressionHold(
                    pending.Actor, discardRestorePoint: false);
                _log.Warning(
                    $"Expression preview retry failed actor={pending.Actor} " +
                    $"timeline={pending.Timeline}: {replayed.Detail}");
            }
            else
            {
                _log.Debug(
                    $"Expression preview replayed while paused actor={pending.Actor} " +
                    $"timeline={pending.Timeline}.");
            }
            return;
        }

        bool timedOut = pending.SettleTicks >= SettleTimeoutTicks;
        if (pending.StableRuns < StableTicks && !timedOut)
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
                $"timeline={pending.Timeline} ticks={pending.SettleTicks}" +
                (timedOut ? " timeout=true." : "."));
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
        foreach (var bone in pending.Bones)
            if (_bindings.Resolve(bone) is not { Success: true })
                return "Expression preview cancelled because a face binding changed.";
        return null;
    }

    private void CancelStale(PendingHold pending, string detail)
    {
        _pending = null;
        _animation.AbandonExpressionHold(pending.Actor);
        _log.Warning(detail);
    }

    private static bool IsFaceBone(string name) =>
        name.StartsWith("j_f_", StringComparison.Ordinal) ||
        name.Equals("j_kao", StringComparison.Ordinal) ||
        name.StartsWith("j_ago", StringComparison.Ordinal);

    private bool TryReadFace(
        IReadOnlyList<BoneId> bones,
        out List<LegacyTransform> reading,
        out string? problem)
    {
        reading = new List<LegacyTransform>(bones.Count);
        problem = null;
        foreach (var bone in bones)
        {
            if (_bindings.Resolve(bone) is not { Success: true, Value: { } live })
            {
                problem = $"face bone {bone.CanonicalName} is no longer bound";
                return false;
            }
            reading.Add(live.LastRawTransform);
        }
        return true;
    }

    private static bool Settled(
        IReadOnlyList<LegacyTransform> current,
        IReadOnlyList<LegacyTransform> previous)
    {
        if (current.Count != previous.Count)
            return false;
        for (var index = 0; index < current.Count; index++)
        {
            var a = current[index];
            var b = previous[index];
            if (Math.Abs(a.Position.X - b.Position.X) > StableEpsilon ||
                Math.Abs(a.Position.Y - b.Position.Y) > StableEpsilon ||
                Math.Abs(a.Position.Z - b.Position.Z) > StableEpsilon ||
                Math.Abs(a.Rotation.X - b.Rotation.X) > StableEpsilon ||
                Math.Abs(a.Rotation.Y - b.Rotation.Y) > StableEpsilon ||
                Math.Abs(a.Rotation.Z - b.Rotation.Z) > StableEpsilon ||
                Math.Abs(a.Rotation.W - b.Rotation.W) > StableEpsilon ||
                Math.Abs(a.Scale.X - b.Scale.X) > StableEpsilon ||
                Math.Abs(a.Scale.Y - b.Scale.Y) > StableEpsilon ||
                Math.Abs(a.Scale.Z - b.Scale.Z) > StableEpsilon)
                return false;
        }
        return true;
    }

    private void RequestRawRefresh(PendingHold pending)
    {
        foreach (var bone in pending.Bones)
            if (_bindings.Resolve(bone) is
                { Success: true, Value: { Skeleton: { } skeleton } })
            {
                _posing.RequestRawTransformRefresh(skeleton);
                return;
            }
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
