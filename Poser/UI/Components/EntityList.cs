using System;
using Poser.Core;
using Poser.Entities;
using Poser.Services;

namespace Poser.UI.Components;

/// <summary>
/// Container component that holds typed entity sublists (ActorList, etc.).
/// Extensible for future entity types like cameras and lights.
/// </summary>
public class EntityList : IDisposable
{
    private readonly ActorList _actorList;
    // Future: private readonly CameraList _cameraList;
    // Future: private readonly LightList _lightList;

    public EntityList(
        IActorManager actorManager,
        IAnimationService animationService,
        EventBus eventBus)
    {
        _actorList = new ActorList(actorManager, animationService, eventBus);
    }

    public void Draw()
    {
        // Draw actor list
        _actorList.Draw();

        // Future: Draw camera list
        // _cameraList?.Draw();

        // Future: Draw light list
        // _lightList?.Draw();
    }

    public void Dispose()
    {
        _actorList.Dispose();
    }
}
