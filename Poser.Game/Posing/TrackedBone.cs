using Poser.Application.Posing;
using Poser.Application.Scene;
using Poser.Application.Transforms;
using Poser.Domain;
using Poser.Domain.Identity;
using Poser.Domain.Posing;
using Poser.Domain.Posing.BoneInfo;

namespace Poser.Game.Posing;

/// <summary>
/// A bone an undo step was recorded on, found again when the step runs. After a redraw the
/// bone is on the actor's new skeleton, as undo finds it for bone edits. After the actor was
/// removed and brought back by undo, the bone is on the actor that took its place, which
/// <see cref="IActorReplacements"/> finds and which can sit in a different game slot.
/// </summary>
internal sealed class TrackedBone(BoneId recorded, Func<ActorId?> actor, SceneSession scene)
{
    /// <summary>Call this while the bone's actor is in the scene, when the step is recorded.</summary>
    public static TrackedBone Track(BoneId bone, IActorReplacements replacements, SceneSession scene) =>
        new(bone, replacements.Track(bone.Skeleton.Actor), scene);

    public BoneId Recorded => recorded;

    /// <summary>The bone on its actor's current skeleton. Null when the actor or the bone
    /// is gone, or while the actor is being redrawn.</summary>
    public TransformTargetId? Current =>
        actor() is { } now ? scene.CurrentTransformTarget(TransformTargetId.ForBone(recorded.OnActor(now))) : null;

    /// <summary>Whether the bone's actor is being redrawn.</summary>
    public bool Pending =>
        actor() is { } now && scene.IsTransformTargetPending(TransformTargetId.ForBone(recorded.OnActor(now)));

    /// <summary>Runs <paramref name="write"/> on the bone as it is now. Fails when the bone is
    /// no longer in the scene, and waits while its actor is being redrawn.</summary>
    public Outcome Run(Func<TransformTargetId, Outcome> write) =>
        Current is { } current ? write(current)
        : Pending ? Outcome.Busy(UndoJournal.TargetsPending)
        : Outcome.Fail($"{BoneInfoService.GetDisplayName(recorded.CanonicalName)} is no longer in the scene.");

    /// <summary>A FABRIK or Rope handle that follows a bone keeps that bone's id in the chain's
    /// settings. Call this when an undo step that stores settings is recorded. The function it
    /// returns puts the followed bone as it is then into the settings the step writes. A bone
    /// that is not found then keeps its recorded id.</summary>
    public static Func<IkChainConfig, IkChainConfig> Handles(
        IActorReplacements replacements, SceneSession scene, params IkChainConfig?[] recorded)
    {
        var bones = recorded.Select(config => config?.Fabrik?.Handle.Bone).OfType<BoneId>().Distinct()
            .Select(bone => Track(bone, replacements, scene)).ToArray();
        return config => config.Fabrik is { Handle.Bone: { } bone } control
            && bones.FirstOrDefault(tracked => tracked.Recorded == bone) is { } tracked
            && tracked.Current?.Bone is { } now
                ? config with { Fabrik = control with { Handle = control.Handle with { Bone = now } } }
                : config;
    }
}
