using System.Globalization;
using System.Text;
using Poser.Application.Animation;
using Poser.Application.Scene;
using Poser.Domain.Identity;

namespace Poser.Application.Transforms;

/// <summary>
/// The actor's current key from what the session already knows: the exact
/// actor and skeleton generations in the scene snapshot, the timeline and
/// loop choices the animation session holds plus the base timeline the
/// body is playing, and the disruption epoch.
/// </summary>
public sealed class ActorStateKeySource : IActorStateKeySource
{
    private readonly SceneSession _scene;
    private readonly AnimationSession _animation;
    private readonly ActorDisruptionEpochs _epochs;

    public ActorStateKeySource(
        SceneSession scene,
        AnimationSession animation,
        ActorDisruptionEpochs epochs)
    {
        _scene = scene;
        _animation = animation;
        _epochs = epochs;
    }

    public ActorStateKey? Current(Guid lineage)
    {
        if (_scene.Snapshot.FindActor(lineage) is not { } actor)
            return null;
        var slots = new List<SkeletonId>(actor.Skeletons.Count);
        foreach (var skeleton in actor.Skeletons)
            slots.Add(skeleton.Id);
        return new ActorStateKey(
            lineage,
            actor.Id,
            slots,
            AnimationSignature(actor.Id),
            _epochs.Read(lineage));
    }

    private string AnimationSignature(ActorId actor)
    {
        var overrides = _animation.OverridesFor(actor);
        var text = new StringBuilder();
        ushort? playing = null;
        try
        {
            playing = _animation.Read(actor)?.BaseTimeline;
        }
        catch
        {
            // A body that cannot be read keys on the session's choices alone.
        }
        text.Append(playing?.ToString(CultureInfo.InvariantCulture) ?? "-");
        text.Append('|');
        text.Append(overrides.BaseTimeline?.ToString(CultureInfo.InvariantCulture) ?? "-");
        foreach (var (slot, timeline) in overrides.SelectedSlots.OrderBy(pair => pair.Key))
            text.Append('|').Append(slot).Append('=').Append(timeline);
        foreach (var slot in overrides.LoopWantedSlots.OrderBy(slot => slot))
            text.Append("|loop:").Append(slot);
        return text.ToString();
    }
}
