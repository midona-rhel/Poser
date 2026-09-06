using System;
using Poser.Application.Transforms;
using Poser.Domain.Actors;
using Poser.Entities;
using Poser.Services;

namespace Poser.Game.Journal;

/// <summary>World adoption history retains an observation, never an address-based command.</summary>
public sealed class WorldActorSession
{
    private readonly WorldActorDiscovery _discovery;
    private readonly TransformHistory _history;
    private readonly Func<IActor, bool> _release;

    public WorldActorSession(WorldActorDiscovery discovery, TransformHistory history, IActorSpawnService spawns)
        : this(discovery, history, spawns.RemoveActorFromScene) { }

    internal WorldActorSession(WorldActorDiscovery discovery, TransformHistory history, Func<IActor, bool> release)
    {
        _discovery = discovery;
        _history = history;
        _release = release;
    }

    public WorldActorImportResult Adopt(WorldActorCandidateId id, out IActor? actor)
    {
        actor = null;
        if (!_discovery.TryRetainCandidate(id, out var observation))
            return WorldActorImportResult.Stale("That world actor is from an older listing.");
        var claim = new Claim(_discovery, observation, _release);
        var result = claim.Acquire();
        if (result.Success)
        {
            actor = claim.Actor;
            _history.Append(new JournalStep("Add actor from the world",
                claim.Release, () => claim.Acquire().Success));
        }
        return result;
    }

    public bool Release(IActor actor)
    {
        if (!_discovery.TryObserveAdopted(actor, out var observation))
            return false;
        var claim = new Claim(_discovery, observation, _release, actor);
        if (!claim.Release())
            return false;
        _history.Append(new JournalStep("Release actor", () => claim.Acquire().Success, claim.Release));
        return true;
    }

    private sealed class Claim(
        WorldActorDiscovery discovery, WorldActorObservation observation, Func<IActor, bool> release,
        IActor? actor = null)
    {
        public IActor? Actor { get; private set; } = actor;

        public WorldActorImportResult Acquire()
        {
            var result = discovery.AcquireObservation(observation, out var actor);
            if (result.Success)
                Actor = actor;
            return result;
        }

        public bool Release()
        {
            if (Actor is null)
                return true;
            if (!discovery.ReleaseObservation(observation, Actor, release))
                return false;
            Actor = null;
            return true;
        }
    }
}
