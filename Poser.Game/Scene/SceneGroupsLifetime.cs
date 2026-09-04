using System;
using Poser.Application.Scene;
using Poser.Application.Transforms;
using Poser.Core;
using Poser.Services;

namespace Poser.Game.Scene;

/// <summary>Groups are scene state: they end with the GPose session and
/// with the plugin, like every entity they hold. Left alone they outlived
/// their members — pruned only when the sidebar next rebuilt, never on
/// unload (2026-09-02). The application layer cannot see the event bus,
/// so this owner clears them from the game layer.</summary>
public sealed class SceneGroupsLifetime : IDisposable
{
    private readonly IEventBus _events;
    private readonly SceneGroups _groups;
    private readonly GroupTransformState _groupTransforms;

    public SceneGroupsLifetime(
        IEventBus events,
        SceneGroups groups,
        GroupTransformState groupTransforms)
    {
        _events = events;
        _groups = groups;
        _groupTransforms = groupTransforms;
        _events.Subscribe<GPoseStateChangedEvent>(OnGPoseStateChanged);
    }

    private void OnGPoseStateChanged(GPoseStateChangedEvent e)
    {
        if (!e.IsGPosing)
        {
            _groups.Clear();
            _groupTransforms.Clear();
        }
    }

    public void Dispose()
    {
        _events.Unsubscribe<GPoseStateChangedEvent>(OnGPoseStateChanged);
        _groups.Clear();
        _groupTransforms.Clear();
    }
}
