using Poser.Application.Animation;
using Poser.Application.Presentation;
using Poser.Domain.Identity;

namespace Poser.Application.Scene;

public interface IScenePlaybackControl
{
    bool? ReadPlaying(SelectionId id);
    void SetPlaying(SelectionId id, bool playing);
    bool? ReadNight(SelectionId id);
    void SetNight(SelectionId id, bool night);
}

public sealed class ScenePlaybackControl(
    IAnimationPlayback animation, ISceneObjectControl objectControl) : IScenePlaybackControl
{
    private readonly IAnimationPlayback _animation = animation;
    private readonly ISceneObjectControl _objectControl = objectControl;
    public bool? ReadPlaying(SelectionId id)
    {
        switch (id)
        {
            case { Kind: SceneEntityKind.Actor, Actor: { } actorId }:
                return _animation.AnyPlaying(actorId);
            case { Kind: SceneEntityKind.WorldObject, WorldObject: { } objectId }:
                if (_objectControl.Read(objectId) is not { } handle)
                    return null;
                if (handle.IsVfx)
                    return !handle.VfxPaused;
                return handle.Spawned ? null : !handle.AnimationPaused;
            default:
                return null;
        }
    }

    public void SetPlaying(SelectionId id, bool playing)
    {
        switch (id)
        {
            case { Kind: SceneEntityKind.Actor, Actor: { } actorId }:
                if (playing)
                    _animation.Resume(actorId);
                else
                    _animation.Pause(actorId);
                break;
            case { Kind: SceneEntityKind.WorldObject, WorldObject: { } objectId }:
                if (_objectControl.Read(objectId) is not { } handle)
                    return;
                if (handle.IsVfx)
                    _objectControl.SetVfxPaused(objectId, !playing);
                else if (!handle.Spawned)
                    _objectControl.SetAnimationPaused(objectId, !playing);
                break;
        }
    }

    /// <summary>Scenery's night state; null for everything else.</summary>
    public bool? ReadNight(SelectionId id) =>
        id is { Kind: SceneEntityKind.WorldObject, WorldObject: { } objectId }

        && _objectControl.Read(objectId) is { IsVfx: false } handle

            ? handle.NightState
            : null;

    public void SetNight(SelectionId id, bool night)
    {
        if (id is { Kind: SceneEntityKind.WorldObject, WorldObject: { } objectId }
            && _objectControl.Read(objectId) is { IsVfx: false })
            _objectControl.SetNightState(objectId, night);
    }

}
