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

    /// <summary>A short ordinal for an object INSTANCE, for breadcrumbs.
    /// </summary>
    private static string Ord(object? instance) =>
        instance is null
            ? "none"
            : System.Runtime.CompilerServices.RuntimeHelpers
                .GetHashCode(instance).ToString("X8");

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
            //
            // BREADCRUMB, because this is a rebuild ENGINE: every release
            // publishes SkeletonChangedEvent, the binding pass refreshes on
            // that event, and the refresh calls back in here. If two callers
            // hold different IActor wrappers for the same (Id, slot) they
            // ping-pong forever — each one's call releases the other's
            // skeleton — and the whole plugin refreshes bindings every frame.
            // The line names WHICH guard failed, so a storm is one grep.
            _log.Debug(
                $"Skeleton rebuild for {actor.Name} {slot}: " +
                (!ReferenceEquals(skeleton.Actor, actor)
                    ? $"a different actor wrapper asked " +
                      $"(held {Ord(skeleton.Actor)}, asked {Ord(actor)})"
                    : skeleton.Actor.Address != actor.Address
                        ? "the wrapper address moved"
                        : !skeleton.IsValid
                            ? "the skeleton went invalid"
                            : currentBase == nint.Zero
                                ? "the slot has no character base"
                                : $"the character base moved " +
                                  $"({skeleton.CharacterBaseAddress:X} to {currentBase:X})"));
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
            if (liveActors.TryGetValue(key.Actor, out var actor) &&
                ReferenceEquals(skeleton.Actor, actor) &&
                skeleton.Actor.Address == actor.Address)
            {
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
