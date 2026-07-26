using System.Collections.Generic;
using System.Linq;
using Poser.Application.Scene;
using Poser.Domain.Animation;
using Poser.Domain.Identity;

namespace Poser.Application.Animation;

/// <summary>
/// Scene-wide animation commands.
///
/// Each action captures the actor-id set ONCE, from the snapshot at the
/// moment the command begins, and works only that list. An actor that
/// appears while the command runs is not swept up in it, and an actor
/// that leaves fails its own entry rather than aborting the rest — so the
/// result reports exactly which actors were affected and which were not.
/// </summary>
public sealed class AnimationSceneActions
{
    private readonly SceneSession _scene;
    private readonly AnimationSession _animation;

    public AnimationSceneActions(SceneSession scene, AnimationSession animation)
    {
        _scene = scene;
        _animation = animation;
    }

    /// <summary>
    /// Skipped actors are reported separately from failures and are NOT
    /// counted as attempted: an actor that cannot be animated at all was
    /// never a target, and folding it into the attempt count makes a
    /// complete run look partial.
    /// </summary>
    public readonly record struct SceneActionReport(
        int Attempted,
        int Succeeded,
        IReadOnlyList<string> Failures,
        IReadOnlyList<string> Skipped)
    {
        public bool Success => Failures.Count == 0;

        public string Summary(string verb)
        {
            var text = Failures.Count == 0
                ? $"{verb} {Succeeded} actor{(Succeeded == 1 ? "" : "s")}."
                : $"{verb} {Succeeded} of {Attempted}: {string.Join("; ", Failures)}";
            if (Skipped.Count > 0)
                text += $" Skipped {Skipped.Count} without animation control " +
                    $"({string.Join(", ", Skipped)}).";
            return text;
        }
    }

    /// <summary>The actor set this command owns, frozen before any work.</summary>
    private IReadOnlyList<ActorDescriptorId> Capture()
    {
        var snapshot = _scene.Snapshot;
        return snapshot.Actors
            .Select(actor => new ActorDescriptorId(actor.Id, actor.Name))
            .ToList();
    }

    public readonly record struct ActorDescriptorId(ActorId Id, string Name);

    private SceneActionReport Run(
        IReadOnlyList<ActorDescriptorId> targets,
        System.Func<ActorId, AnimationResult> action)
    {
        var failures = new List<string>();
        var skipped = new List<string>();
        int attempted = 0;
        int succeeded = 0;
        foreach (var target in targets)
        {
            if (!_animation.IsSupported(target.Id))
            {
                skipped.Add(target.Name);
                continue;
            }
            attempted++;
            var result = action(target.Id);
            if (result.Success)
                succeeded++;
            else
                failures.Add($"{target.Name}: {result.Detail ?? "failed"}");
        }
        return new SceneActionReport(attempted, succeeded, failures, skipped);
    }

    public SceneActionReport FreezeAll() =>
        Run(Capture(), id => _animation.Pause(id));

    public SceneActionReport ResumeAll() =>
        Run(Capture(), id => _animation.Resume(id));

    /// <summary>
    /// Restarts whatever each actor is currently playing, from its own
    /// base slot — an actor with no animation is skipped rather than
    /// being given one.
    /// </summary>
    public SceneActionReport ReplayAll() =>
        Run(Capture(), id =>
        {
            if (_animation.Read(id) is not { } reading)
                return AnimationResult.Fail("unreadable");
            ushort timeline = reading.BaseTimeline != 0
                ? reading.BaseTimeline
                : reading.TimelineFor(AnimationSlot.Base);
            return timeline == 0
                ? AnimationResult.Ok()
                : _animation.Blend(id, timeline);
        });

    /// <summary>Restores every Poser-owned override on every captured
    /// actor, leaving the scene as Poser found it.</summary>
    public SceneActionReport StopAll() =>
        Run(Capture(), id => _animation.ResetActor(id));
}
