using Dalamud.Plugin.Services;
using Poser.Application.Scene;
using Poser.Application.Transforms;
using Poser.Core;
using Poser.Game.Bindings;
using Poser.Services;

namespace Poser.Game.Scene;

/// <summary>Owns native discovery refresh and clean GPose-session teardown.</summary>
public sealed class CleanSceneLifecycle : IDisposable
{
    private readonly StableBindingRegistry _bindings;
    private readonly SceneSession _scene;
    private readonly TransformGestureService _gestures;
    private readonly TransformHistory _history;
    private readonly IEventBus _events;

    public CleanSceneLifecycle(
        StableBindingRegistry bindings,
        SceneSession scene,
        TransformGestureService gestures,
        TransformHistory history,
        IEventBus events,
        IFramework framework)
    {
        _bindings = bindings;
        _scene = scene;
        _gestures = gestures;
        _history = history;
        _events = events;
        _events.Subscribe<ActorListChangedEvent>(OnActorListChanged);
        _events.Subscribe<SkeletonChangedEvent>(OnSkeletonChanged);
        _events.Subscribe<GPoseStateChangedEvent>(OnGPoseChanged);
        // The plugin constructor runs on a loader task thread, while every
        // subscribed event publishes from the framework thread. The registry
        // refresh reads native skeleton data and shared bone-name state, so
        // the initial discovery must run on the framework thread too — a
        // concurrent ctor-thread refresh corrupted shared collections and
        // could fail the whole plugin load.
        _ = framework.RunOnFrameworkThread(Refresh);
    }

    public void Dispose()
    {
        _events.Unsubscribe<ActorListChangedEvent>(OnActorListChanged);
        _events.Unsubscribe<SkeletonChangedEvent>(OnSkeletonChanged);
        _events.Unsubscribe<GPoseStateChangedEvent>(OnGPoseChanged);
    }

    private void Refresh()
    {
        // Restore through the still-current bindings before replacing the
        // registry generation map.
        if (_gestures.ActiveGesture is { } gesture)
            _gestures.Cancel(gesture);
        _scene.Refresh(_bindings.Refresh());
    }

    private void OnActorListChanged(ActorListChangedEvent _) =>
        Refresh();

    private void OnSkeletonChanged(SkeletonChangedEvent _) =>
        Refresh();

    private void OnGPoseChanged(GPoseStateChangedEvent evt)
    {
        if (!evt.IsGPosing)
        {
            if (_gestures.ActiveGesture is { } gesture)
                _gestures.Cancel(gesture);
            _history.Clear();
        }
        Refresh();
    }
}
