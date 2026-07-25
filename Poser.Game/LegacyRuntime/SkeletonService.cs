using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Plugin.Services;
using Poser.Core;
using Poser.Domain.Identity;
using Poser.Entities;
using Poser.Services;

namespace Poser.Game;

/// <summary>
/// Slot-aware skeleton discovery and caching. Skeletons cache per
/// (actor, slot); an entry is reused only while its actor binding AND its
/// slot's native CharacterBase are unchanged, so sheathing, redraws, and
/// equipment replacement release exactly that slot.
/// </summary>
public class SkeletonService : ISkeletonService
{
    private readonly IPluginLog _log;
    private readonly IGPoseService _gPoseService;
    private readonly IEventBus _eventBus;
    private readonly Dictionary<(EntityId Actor, PoseSlot Slot), Skeleton> _skeletons = new();

    public SkeletonService(IPluginLog log, IGPoseService gPoseService, IEventBus eventBus)
    {
        _log = log;
        _gPoseService = gPoseService;
        _eventBus = eventBus;

        _eventBus.Subscribe<GPoseStateChangedEvent>(OnGPoseStateChanged);
        _eventBus.Subscribe<ActorListChangedEvent>(OnActorListChanged);
    }

    public ISkeleton? GetSkeleton(IActor actor) =>
        GetSkeleton(actor, PoseSlot.Character);

    public unsafe ISkeleton? GetSkeleton(IActor actor, PoseSlot slot)
    {
        if (actor.Address == nint.Zero || slot == PoseSlot.Unknown)
            return null;

        var currentBase = (nint)SlotCharacterBases.Resolve(actor.Address, slot);
        var key = (actor.Id, slot);
        if (_skeletons.TryGetValue(key, out var skeleton))
        {
            if (ReferenceEquals(skeleton.Actor, actor) &&
                skeleton.Actor.Address == actor.Address &&
                skeleton.IsValid &&
                currentBase != nint.Zero &&
                skeleton.CharacterBaseAddress == currentBase)
            {
                return skeleton;
            }

            // The slot vanished or was replaced: release only this entry.
            skeleton.Dispose();
            _skeletons.Remove(key);
            _eventBus.Publish(new SkeletonChangedEvent(actor, null));
        }

        if (currentBase == nint.Zero)
            return null;

        try
        {
            skeleton = new Skeleton(
                actor,
                slot,
                () => (nint)SlotCharacterBases.Resolve(actor.Address, slot));
            if (skeleton.IsValid)
            {
                _skeletons[key] = skeleton;

                if (slot == PoseSlot.Character && actor is ActorBase actorBase)
                    actorBase.AttachChild(skeleton);

                _log.Debug($"Created {slot} skeleton for {actor.Name} with {skeleton.Bones.Count} bones");
                _eventBus.Publish(new SkeletonChangedEvent(actor, skeleton));
                return skeleton;
            }
        }
        catch (Exception ex)
        {
            _log.Warning($"Failed to create {slot} skeleton for {actor.Name}: {ex.Message}");
        }

        return null;
    }

    public IReadOnlyList<ISkeleton> GetSkeletons(IActor actor)
    {
        var result = new List<ISkeleton>();
        foreach (var slot in SlotCharacterBases.SupportedSlots)
        {
            if (GetSkeleton(actor, slot) is { } skeleton)
                result.Add(skeleton);
        }
        return result;
    }

    public void RefreshSkeleton(IActor actor)
    {
        foreach (var (key, skeleton) in _skeletons.ToArray())
        {
            if (!key.Actor.Equals(actor.Id) || !ReferenceEquals(skeleton.Actor, actor))
                continue;
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
        foreach (var (key, skeleton) in _skeletons.ToArray())
        {
            if (liveActors.TryGetValue(key.Actor, out var actor) &&
                ReferenceEquals(skeleton.Actor, actor) &&
                skeleton.Actor.Address == actor.Address)
            {
                continue;
            }

            skeleton.Dispose();
            _skeletons.Remove(key);
        }
    }

    public void Dispose()
    {
        _eventBus.Unsubscribe<GPoseStateChangedEvent>(OnGPoseStateChanged);
        _eventBus.Unsubscribe<ActorListChangedEvent>(OnActorListChanged);
        ClearAll();
    }
}
