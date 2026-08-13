using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Plugin.Services;
using Poser.Application.Animation;
using Poser.Application.Scene;
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
    private readonly SceneSession _scene;
    private readonly AnimationSession _animation;
    private readonly TransformCommandService _transforms;
    private readonly TransformGestureService _gestures;
    private readonly IPluginLog _log;

    /// <summary>Ticks to let the face settle after the preview stops.
    /// Ktisis proc's its own sync twice for the same reason.</summary>
    private const int SettleTicks = 2;

    private sealed class PendingBake
    {
        public required ActorId Actor;
        public required SkeletonId Skeleton;
        /// <summary>The EXACT speed ownership before the bake paused the
        /// actor: an owned override value (0 = already paused, 0.5 = a
        /// custom slow-motion), or null when the game owned its own speed.
        /// Restored verbatim — collapsing this to a pause/resume pair
        /// destroyed custom speeds.</summary>
        public required float? PriorSpeed;
        public required List<(BoneId Bone, LegacyTransform Captured)> Captures;
        public int TicksRemaining = SettleTicks;
    }

    private PendingBake? _pending;

    public FacialPoseCapture(
        IFramework framework,
        StableBindingRegistry bindings,
        SceneSession scene,
        AnimationSession animation,
        TransformCommandService transforms,
        TransformGestureService gestures,
        IPluginLog log)
    {
        _framework = framework;
        _bindings = bindings;
        _scene = scene;
        _animation = animation;
        _transforms = transforms;
        _gestures = gestures;
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
        // A live transform gesture owns the pose right now; baking under
        // it would interleave two writers on the same bones.
        if (_gestures.ActiveGesture != null)
            return GestureResult.Fail("Finish the current transform gesture first.");

        // Only the Character skeleton carries face bones; auxiliary slots
        // must not be swept in.
        if (descriptor.CharacterSkeleton is not { } skeleton)
            return GestureResult.Fail("This actor has no character skeleton.");

        var captures = new List<(BoneId, LegacyTransform)>();
        foreach (var bone in skeleton.Bones)
        {
            if (!IsFaceBone(bone.Id.CanonicalName))
                continue;
            if (_bindings.Resolve(bone.Id) is not { Success: true, Value: { } live })
                continue;
            // LastRawTransform is the pre-reparent absolute a pose file
            // stores; LastTransform diverges for face partials.
            captures.Add((bone.Id, live.LastRawTransform));
        }

        if (captures.Count == 0)
            return GestureResult.Fail("This actor has no face bones to capture.");

        // Pause for the capture, remembering the exact prior ownership so
        // the end of the bake can put back a custom speed, a pause, or
        // no override at all — whichever was true.
        float? priorSpeed = _animation.OverridesFor(actor).OverallSpeed;
        if (priorSpeed is not 0f)
        {
            var paused = _animation.Pause(actor);
            if (!paused.Success)
                return GestureResult.Fail(paused.Detail ?? "Could not pause the actor.");
        }

        // Stop ONLY the preview: the session's expression release (Brio's
        // unpin / Straight face / idle sequence), which touches nothing
        // but the face.
        var stopped = _animation.ReleaseExpression(actor);
        if (!stopped.Success)
        {
            RestoreSpeed(actor, priorSpeed);
            return GestureResult.Fail(stopped.Detail ?? "Could not stop the facial preview.");
        }

        // Suspend AFTER our own setup calls, so the guard blocks the user
        // and not this operation.
        _animation.SuspendCommands();
        _pending = new PendingBake
        {
            Actor = actor,
            Skeleton = skeleton.Id,
            PriorSpeed = priorSpeed,
            Captures = captures,
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
    /// Phase two: re-validate, then apply through the ONE atomic transform
    /// authority. SetAbsoluteMany captures every target before writing,
    /// rolls the whole face back on any failure, refuses to run under a
    /// live gesture, and records the single undoable history patch — the
    /// per-bone linked-aware path double-applied linked bones and is not
    /// used here.
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

            var writes = new List<(TransformTargetId, Poser.Domain.Transforms.PoseTransform)>(
                pending.Captures.Count);
            foreach (var (boneId, captured) in pending.Captures)
                writes.Add((
                    TransformTargetId.ForBone(boneId),
                    new Poser.Domain.Transforms.PoseTransform(
                        captured.Position, captured.Rotation, captured.Scale)));

            // rawBaseline: the application basis is each bone's CURRENT
            // LastRawTransform — the settled, preview-free face — exactly
            // as a pose file loads. The default captured baseline is
            // LastTransform, which diverges on face partials.
            var applied = _transforms.SetAbsoluteMany(
                writes, "Apply facial animation to pose", rawBaseline: true);
            if (!applied.Success)
                _log.Warning($"Face capture abandoned: {applied.Detail}");
        }
        finally
        {
            // Release the guard before our own teardown call, then give
            // the actor back its EXACT prior speed ownership.
            _animation.ResumeCommands();
            RestoreSpeed(pending.Actor, pending.PriorSpeed);
        }
    }

    /// <summary>Puts back the speed state recorded at Begin: an owned
    /// override is re-written verbatim (including 0 — an actor that was
    /// already paused stays paused); no override hands the speed back to
    /// the game.</summary>
    private void RestoreSpeed(ActorId actor, float? priorSpeed)
    {
        var restored = priorSpeed is { } speed
            ? _animation.SetSpeed(actor, speed)
            : _animation.ClearSpeed(actor);
        if (!restored.Success)
            _log.Warning(
                $"Face bake could not restore playback speed: {restored.Detail}");
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

        var snapshot = _scene.Snapshot;
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
