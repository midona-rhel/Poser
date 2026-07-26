using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Plugin.Services;
using Poser.Application.Animation;
using Poser.Application.Transforms;
using Poser.Domain.Animation;
using Poser.Domain.Identity;
using Poser.Domain.Scene;
using Poser.Entities;
using Poser.Game.Bindings;
using Poser.Services;
using LegacyTransform = Poser.Transform;

namespace Poser.Game.Animation;

/// <summary>
/// Keeps a previewed facial animation after the preview stops.
///
/// This cannot be done in one frame. Poser applies its pose layers as
/// deltas on top of whatever the animation is currently producing, so
/// while a facial timeline plays there is no observable "what this face
/// would be without it" — reading and writing the same value on the same
/// tick yields an identity delta and changes nothing. The delta only
/// exists once the preview has stopped and the face has settled back.
///
/// So the bake is two phases:
///   1. capture each Character face bone's LastRawTransform while the
///      preview is visible — the same basis PoseFileService saves;
///   2. stop ONLY the facial slot, let the baseline settle for two
///      framework ticks, then apply each captured value against the
///      bone's now-current LastRawTransform, exactly as loading a pose
///      file does.
///
/// Ktisis achieves the same result by calling the original
/// hkaPose::syncModelSpace on the face partial, which works only because
/// its posing freezes model space by neutering that hook. Poser has no
/// such hook and deliberately does not add one.
///
/// Expression and gaze are named layers present in BOTH phases, so their
/// contribution appears on both sides of the delta and cancels; they are
/// never cleared and never double-applied. Manual edits to other bones
/// are untouched because only face bones are written.
/// </summary>
public sealed class FacialPoseCapture : IDisposable
{
    private readonly IFramework _framework;
    private readonly StableBindingRegistry _bindings;
    private readonly IBonePosingService _bonePosing;
    private readonly AnimationSession _animation;
    private readonly ITransformRuntimePort _runtime;
    private readonly TransformHistory _history;
    private readonly IPluginLog _log;

    /// <summary>Ticks to let the face settle after the preview stops.
    /// Ktisis proc's its own sync twice for the same reason.</summary>
    private const int SettleTicks = 2;

    private sealed class PendingBake
    {
        public required ActorId Actor;
        public required SkeletonId Skeleton;
        public required bool WasPaused;
        public required List<(BoneId Bone, LegacyTransform Captured)> Captures;
        public required List<TransformTargetState> Before;
        public int TicksRemaining = SettleTicks;
    }

    private PendingBake? _pending;

    public FacialPoseCapture(
        IFramework framework,
        StableBindingRegistry bindings,
        IBonePosingService bonePosing,
        AnimationSession animation,
        ITransformRuntimePort runtime,
        TransformHistory history,
        IPluginLog log)
    {
        _framework = framework;
        _bindings = bindings;
        _bonePosing = bonePosing;
        _animation = animation;
        _runtime = runtime;
        _history = history;
        _log = log;
        _framework.Update += OnFrameworkUpdate;
    }

    /// <summary>True between the two phases. While pending, the session
    /// refuses animation commands and the surface disables the control,
    /// so nothing can change the face under the capture.</summary>
    public bool IsPending => _pending != null;

    /// <summary>Face bones use the game's own naming, the same rule the
    /// Face pose region uses, so bake and Reset Face cover the same set.</summary>
    private static bool IsFaceBone(string name) =>
        name.StartsWith("j_f_", StringComparison.Ordinal) ||
        name.Equals("j_kao", StringComparison.Ordinal) ||
        name.StartsWith("j_ago", StringComparison.Ordinal);

    /// <summary>
    /// Phase one: pause, capture the visible face, and stop the preview.
    /// </summary>
    public GestureResult Begin(ActorId actor, ActorDescriptor descriptor)
    {
        if (_pending != null)
            return GestureResult.Fail("A face capture is already in progress.");
        if (!_framework.IsInFrameworkUpdateThread)
            return GestureResult.Fail("Face capture must start on the framework thread.");

        // Only the Character skeleton carries face bones; auxiliary slots
        // must not be swept in.
        if (descriptor.CharacterSkeleton is not { } skeleton)
            return GestureResult.Fail("This actor has no character skeleton.");

        var captures = new List<(BoneId, LegacyTransform)>();
        var before = new List<TransformTargetState>();
        foreach (var bone in skeleton.Bones)
        {
            if (!IsFaceBone(bone.Id.CanonicalName))
                continue;
            if (_bindings.Resolve(bone.Id) is not { Success: true, Value: { } live })
                continue;

            var captured = _runtime.Capture(TransformTargetId.ForBone(bone.Id));
            if (!captured.Success || captured.State == null)
                continue;
            // LastRawTransform is the pre-reparent absolute a pose file
            // stores; LastTransform diverges for face partials.
            captures.Add((bone.Id, live.LastRawTransform));
            before.Add(captured.State);
        }

        if (captures.Count == 0)
            return GestureResult.Fail("This actor has no face bones to capture.");

        bool wasPaused = _animation.IsPaused(actor);
        if (!wasPaused)
        {
            var paused = _animation.Pause(actor);
            if (!paused.Success)
                return GestureResult.Fail(paused.Detail ?? "Could not pause the actor.");
        }

        // Stop ONLY the preview: the facial slot goes back to the timeline
        // captured before Poser first replaced it, leaving base and upper
        // body playing.
        var stopped = _animation.RestoreSlotTimeline(actor, AnimationSlot.Facial);
        if (!stopped.Success)
        {
            if (!wasPaused)
                _animation.Resume(actor);
            return GestureResult.Fail(stopped.Detail ?? "Could not stop the facial preview.");
        }

        // Suspend AFTER our own setup calls, so the guard blocks the user
        // and not this operation.
        _animation.SuspendCommands();
        _pending = new PendingBake
        {
            Actor = actor,
            Skeleton = skeleton.Id,
            WasPaused = wasPaused,
            Captures = captures,
            Before = before,
        };
        return GestureResult.Ok();
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        if (_pending is not { } pending)
            return;
        if (--pending.TicksRemaining > 0)
            return;
        _pending = null;
        Complete(pending);
    }

    /// <summary>
    /// Phase two: re-validate, apply, and record one history entry.
    /// </summary>
    private void Complete(PendingBake pending)
    {
        try
        {
            if (Revalidate(pending) is { } problem)
            {
                _log.Warning($"Face capture abandoned: {problem}");
                return;
            }

            // Apply exactly as loading a pose file does: the captured
            // absolute against the bone's CURRENT raw baseline, which is
            // now the settled, preview-free face.
            var applied = new List<BoneId>();
            foreach (var (boneId, captured) in pending.Captures)
            {
                if (_bindings.Resolve(boneId) is not { Success: true, Value: { } live })
                    continue;
                _bonePosing.ApplyTransform(live, captured, live.LastRawTransform);
                applied.Add(boneId);
            }
            if (applied.Count == 0)
                return;

            var after = new List<TransformTargetState>(pending.Before.Count);
            foreach (var state in pending.Before)
            {
                var captured = _runtime.Capture(state.Target);
                if (!captured.Success || captured.State == null)
                {
                    // Put every touched bone back and record nothing: a
                    // half-recorded patch would not undo cleanly.
                    foreach (var original in pending.Before)
                        _runtime.Restore(original);
                    _log.Warning("Face capture rolled back: a bone could not be re-read.");
                    return;
                }
                after.Add(captured.State);
            }

            // One patch for the whole face, so undo removes the bake in a
            // single step rather than bone by bone.
            _history.Append(new TransformPatch(
                "Apply facial animation to pose", pending.Before, after));
        }
        finally
        {
            // Release the guard before our own teardown call.
            _animation.ResumeCommands();
            // The actor was paused for the capture; give it back the
            // playback state it had.
            if (!pending.WasPaused)
                _animation.Resume(pending.Actor);
        }
    }

    /// <summary>
    /// Anything that could make the captured values belong to a different
    /// body: the actor generation, the Character skeleton generation, or
    /// any individual bone binding. Returns a reason, or null when the
    /// capture is still valid.
    /// </summary>
    private string? Revalidate(PendingBake pending)
    {
        if (_bindings.Resolve(pending.Actor) is not { Success: true })
            return "the actor is no longer available";

        var snapshot = _bindings.CurrentSnapshot;
        ActorDescriptor? descriptor = null;
        foreach (var candidate in snapshot.Actors)
            if (candidate.Id.Equals(pending.Actor))
                descriptor = candidate;
        if (descriptor?.CharacterSkeleton is not { } skeleton)
            return "the character skeleton is gone";
        if (!skeleton.Id.Equals(pending.Skeleton))
            return "the character skeleton was replaced";

        foreach (var (boneId, _) in pending.Captures)
            if (_bindings.Resolve(boneId) is not { Success: true })
                return $"bone {boneId.CanonicalName} was rebound";
        return null;
    }

    public void Dispose()
    {
        _framework.Update -= OnFrameworkUpdate;
        GC.SuppressFinalize(this);
    }
}
