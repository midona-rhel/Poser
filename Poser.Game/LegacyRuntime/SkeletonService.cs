using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Plugin.Services;
using Poser.Core;
using Poser.Entities;
using Poser.Services;

namespace Poser.Game;

/// <summary>
/// Service for managing actor skeletons.
/// </summary>
public class SkeletonService : ISkeletonService
{
    private readonly IPluginLog _log;
    private readonly IGPoseService _gPoseService;
    private readonly IEventBus _eventBus;
    private readonly Dictionary<EntityId, Skeleton> _skeletons = new();

    public SkeletonService(IPluginLog log, IGPoseService gPoseService, IEventBus eventBus)
    {
        _log = log;
        _gPoseService = gPoseService;
        _eventBus = eventBus;

        _eventBus.Subscribe<GPoseStateChangedEvent>(OnGPoseStateChanged);
        _eventBus.Subscribe<ActorListChangedEvent>(OnActorListChanged);
    }

    public ISkeleton? GetSkeleton(IActor actor)
    {
        if (actor.Address == nint.Zero)
            return null;

        if (_skeletons.TryGetValue(actor.Id, out var skeleton))
        {
            if (ReferenceEquals(skeleton.Actor, actor) &&
                skeleton.Actor.Address == actor.Address &&
                skeleton.IsValid)
            {
                return skeleton;
            }

            skeleton.Dispose();
            _skeletons.Remove(actor.Id);
        }

        // Create new skeleton
        try
        {
            skeleton = new Skeleton(actor);
            if (skeleton.IsValid)
            {
                _skeletons[actor.Id] = skeleton;

                // Attach skeleton as child of actor
                if (actor is ActorBase actorBase)
                {
                    actorBase.AttachChild(skeleton);
                }

                _log.Debug($"Created skeleton for {actor.Name} with {skeleton.Bones.Count} bones");
                _eventBus.Publish(new SkeletonChangedEvent(actor, skeleton));
                return skeleton;
            }
        }
        catch (Exception ex)
        {
            _log.Warning($"Failed to create skeleton for {actor.Name}: {ex.Message}");
        }

        return null;
    }

    public void RefreshSkeleton(IActor actor)
    {
        if (_skeletons.TryGetValue(actor.Id, out var skeleton) &&
            ReferenceEquals(skeleton.Actor, actor))
        {
            skeleton.Refresh();
            _eventBus.Publish(new SkeletonChangedEvent(actor, skeleton.IsValid ? skeleton : null));
        }
    }

    public void ClearAll()
    {
        foreach (var skeleton in _skeletons.Values)
        {
            if (skeleton is IDisposable disposable)
                disposable.Dispose();
        }
        _skeletons.Clear();
    }

    private void OnGPoseStateChanged(GPoseStateChangedEvent e)
    {
        if (!e.IsGPosing)
        {
            ClearAll();
        }
    }

    private void OnActorListChanged(ActorListChangedEvent e)
    {
        var liveActors = e.Actors.ToDictionary(actor => actor.Id);
        foreach (var (id, skeleton) in _skeletons.ToArray())
        {
            if (liveActors.TryGetValue(id, out var actor) &&
                ReferenceEquals(skeleton.Actor, actor) &&
                skeleton.Actor.Address == actor.Address)
            {
                continue;
            }

            skeleton.Dispose();
            _skeletons.Remove(id);
        }
    }

    public void Dispose()
    {
        _eventBus.Unsubscribe<GPoseStateChangedEvent>(OnGPoseStateChanged);
        _eventBus.Unsubscribe<ActorListChangedEvent>(OnActorListChanged);
        ClearAll();
    }
}
