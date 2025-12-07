using System;
using System.Collections.Generic;
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
    private readonly Dictionary<nint, Skeleton> _skeletons = new();

    public SkeletonService(IPluginLog log, IGPoseService gPoseService, IEventBus eventBus)
    {
        _log = log;
        _gPoseService = gPoseService;
        _eventBus = eventBus;

        _eventBus.Subscribe<GPoseStateChangedEvent>(OnGPoseStateChanged);
    }

    public ISkeleton? GetSkeleton(IActor actor)
    {
        if (actor.Address == nint.Zero)
            return null;

        if (_skeletons.TryGetValue(actor.Address, out var skeleton))
            return skeleton;

        // Create new skeleton
        try
        {
            skeleton = new Skeleton(actor);
            if (skeleton.IsValid)
            {
                _skeletons[actor.Address] = skeleton;

                // Attach skeleton as child of actor
                if (actor is ActorBase actorBase)
                {
                    actorBase.AttachChild(skeleton);
                }

                _log.Debug($"Created skeleton for {actor.Name} with {skeleton.Bones.Count} bones");
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
        if (_skeletons.TryGetValue(actor.Address, out var skeleton))
        {
            skeleton.Refresh();
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

    public void Dispose()
    {
        _eventBus.Unsubscribe<GPoseStateChangedEvent>(OnGPoseStateChanged);
        ClearAll();
    }
}
