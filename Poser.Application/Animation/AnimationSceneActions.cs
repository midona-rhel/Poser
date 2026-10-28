using System.Collections.Generic;
using System.Linq;
using Poser.Application.Scene;
using Poser.Domain;
using Poser.Domain.Identity;

namespace Poser.Application.Animation;

/// <summary>
/// Runs animation commands on a snapshot of the scene's actors.
/// Actors without animation control are skipped; individual command failures are collected.
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
    /// Counts attempted and successful commands separately from actors skipped
    /// for lack of animation control. A skipped actor does not make Success false.
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

    /// <summary>
    /// Copies the actor IDs and names before the command begins.
    /// </summary>
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
        System.Func<ActorId, Outcome> action)
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

    // One click, one step for every actor it holds or plays.
    public SceneActionReport FreezeAll() => Grouped(() => Run(Capture(), id => _animation.Pause(id)));

    public SceneActionReport ResumeAll() => Grouped(() => Run(Capture(), id => _animation.Resume(id)));

    private SceneActionReport Grouped(Func<SceneActionReport> run)
    {
        SceneActionReport? report = null;
        _animation.Grouped(() => report = run());
        return report!.Value;
    }
}
