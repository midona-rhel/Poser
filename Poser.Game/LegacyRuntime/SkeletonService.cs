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

    /// <summary>
    /// One cache entry per (actor id, slot), guarded on NATIVE facts only:
    /// validity and the slot's current CharacterBase. Which wrapper OBJECT
    /// asks is irrelevant — a caller holding a different wrapper for the
    /// same logical actor gets the same skeleton, with the entry re-pointed
    /// at the live wrapper. A guard failure rebuilds the SAME instance in
    /// place instead of releasing and recreating it. Together these make
    /// the historical refresh storm structurally impossible: no call path
    /// can release an entry another caller's identical request just built,
    /// and no rebuild mints a new instance for an unchanged native skeleton
    /// (issue #78).
    /// </summary>
    public unsafe ISkeleton? GetSkeleton(IActor actor, PoseSlot slot)
    {
        if (actor.Address == nint.Zero || slot == PoseSlot.Unknown)
            return null;

        var currentBase = (nint)SlotCharacterBases.Resolve(actor.Address, slot);
        var key = (actor.Id, slot);
        if (_skeletons.TryGetValue(key, out var skeleton))
        {
            // Same id, newer wrapper object: same actor. Follow the caller's
            // wrapper so the entry never pins a stale one.
            if (!ReferenceEquals(skeleton.Actor, actor))
                RebindActor(key, skeleton, actor);

            if (skeleton.IsValid &&
                currentBase != nint.Zero &&
                skeleton.CharacterBaseAddress == currentBase)
            {
                return skeleton;
            }

            if (currentBase == nint.Zero)
            {
                // The slot is genuinely gone; release only this entry.
                _log.Debug(
                    $"Skeleton released for {actor.Name} {slot}: the slot has no character base");
                ReleaseSkeleton(key, skeleton);
                _eventBus.Publish(new SkeletonChangedEvent(actor, null));
                return null;
            }

            // The native skeleton changed (redraw, equipment replacement):
            // rebuild THIS instance in place. The instance, its id, and its
            // cache entry all survive; only the native view and the build
            // revision move.
            _log.Debug(
                $"Skeleton rebuilt in place for {actor.Name} {slot}: " +
                (!skeleton.IsValid
                    ? "the skeleton went invalid"
                    : $"the character base moved " +
                      $"({skeleton.CharacterBaseAddress:X} to {currentBase:X})"));
            skeleton.Refresh();
            if (skeleton.IsValid)
            {
                _eventBus.Publish(new SkeletonChangedEvent(actor, skeleton));
                return skeleton;
            }

            ReleaseSkeleton(key, skeleton);
            _eventBus.Publish(new SkeletonChangedEvent(actor, null));
        }

        if (currentBase == nint.Zero)
            return null;

        try
        {
            skeleton = new Skeleton(
                actor,
                slot,
                owner => (nint)SlotCharacterBases.Resolve(owner.Address, slot));
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

    /// <summary>Re-points a cached skeleton at the live wrapper for its
    /// unchanged actor id, moving the Character slot's entity attachment
    /// with it.</summary>
    private static void RebindActor(
        (EntityId Actor, PoseSlot Slot) key, Skeleton skeleton, IActor actor)
    {
        if (skeleton.Actor is ActorBase previous)
            previous.DetachChild(skeleton);
        skeleton.RebindActor(actor);
        if (key.Slot == PoseSlot.Character && actor is ActorBase next)
            next.AttachChild(skeleton);
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
            if (!key.Actor.Equals(actor.Id))
                continue;
            if (!ReferenceEquals(skeleton.Actor, actor))
                RebindActor(key, skeleton, actor);
            skeleton.Refresh();
            _eventBus.Publish(new SkeletonChangedEvent(actor, skeleton.IsValid ? skeleton : null));
        }
    }

    /// <summary>
    /// The ONE release path for every cached skeleton — replacement, actor
    /// removal, ClearAll, and disposal. Detaches the entity from its
    /// ActorBase BEFORE disposal so <c>IActor.Skeleton</c> (first attached
    /// skeleton child) can never return a disposed instance; after a
    /// replacement it returns only the newly attached Character skeleton.
    /// </summary>
    private void ReleaseSkeleton((EntityId Actor, PoseSlot Slot) key, Skeleton skeleton)
    {
        if (skeleton.Actor is ActorBase actorBase)
            actorBase.DetachChild(skeleton);
        skeleton.Dispose();
        _skeletons.Remove(key);
    }

    public void ClearAll()
    {
        foreach (var (key, skeleton) in _skeletons.ToArray())
            ReleaseSkeleton(key, skeleton);
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
            // A live actor at the same id and native address keeps its
            // entry, wrapper object or not; the entry follows the live
            // wrapper. An actor gone or moved releases the slot.
            if (liveActors.TryGetValue(key.Actor, out var actor) &&
                skeleton.Actor.Address == actor.Address)
            {
                if (!ReferenceEquals(skeleton.Actor, actor))
                    RebindActor(key, skeleton, actor);
                continue;
            }

            ReleaseSkeleton(key, skeleton);
        }
    }

    public void Dispose()
    {
        _eventBus.Unsubscribe<GPoseStateChangedEvent>(OnGPoseStateChanged);
        _eventBus.Unsubscribe<ActorListChangedEvent>(OnActorListChanged);
        ClearAll();
    }
}
