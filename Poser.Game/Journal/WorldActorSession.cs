using System;
using Poser.Application.Transforms;
using Poser.Application.Presentation;
using Poser.Domain.Actors;
using Poser.Domain.Identity;
using Poser.Domain.Presentation;
using Poser.Entities;
using Poser.Game.Scene;
using Poser.Services;

namespace Poser.Game.Journal;

/// <summary>World adoption history retains an observation, never an address-based command.</summary>
public sealed class WorldActorSession
{
    private readonly WorldActorDiscovery _discovery;
    private readonly TransformHistory _history;
    private readonly Func<IActor, bool> _release;
    private readonly IActorLifecycle? _state;
    private readonly ActorPresentationSession? _presentation;
    private readonly Func<IActor, ActorId?>? _actorId;

    public WorldActorSession(WorldActorDiscovery discovery, TransformHistory history, IActorSpawnService spawns,
        SceneLifecycleHistory lifecycle, ActorPresentationSession presentation, IEntityBindings bindings)
        : this(discovery, history, spawns.RemoveActorFromScene, lifecycle.ActorStatePort, presentation, bindings.GetActorId) { }

    internal WorldActorSession(WorldActorDiscovery discovery, TransformHistory history, Func<IActor, bool> release,
        IActorLifecycle? state = null, ActorPresentationSession? presentation = null, Func<IActor, ActorId?>? actorId = null)
    {
        _discovery = discovery;
        _history = history;
        _release = release;
        _state = state;
        _presentation = presentation;
        _actorId = actorId;
    }

    public WorldActorImportResult Adopt(WorldActorCandidateId id, out IActor? actor)
    {
        actor = null;
        if (!_discovery.TryRetainCandidate(id, out var observation))
            return WorldActorImportResult.Stale("That world actor is from an older listing.");
        var claim = new Claim(this, observation);
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
        var claim = new Claim(this, observation, actor);
        if (!claim.Release())
            return false;
        _history.Append(new JournalStep("Release actor", () => claim.Acquire().Success, claim.Release));
        return true;
    }

    private sealed class Claim(
        WorldActorSession owner, WorldActorObservation observation,
        IActor? actor = null)
    {
        public IActor? Actor { get; private set; } = actor;
        private bool _captured;
        private ActorState? _savedState;
        private string? _savedName;
        private PresentationOverrides? _savedPresentation;

        public WorldActorImportResult Acquire()
        {
            var result = owner._discovery.AcquireObservation(observation, out var actor);
            if (result.Success)
            {
                Actor = actor;
                if (actor is not null && _captured)
                {
                    bool Current() => ReferenceEquals(Actor, actor)
                        && owner._discovery.CanRestoreObservation(observation, actor);
                    if (_savedState is { } state)
                        owner._state?.Restore(actor, state, Current);
                    owner._state?.WhenPosable(actor, _ =>
                    {
                        if (!Current()) return;
                        if (_savedName is { } name) owner._state.SetName(actor, name);
                        if (owner._actorId?.Invoke(actor) is { } id && owner._presentation is { } presentation)
                        {
                            var restored = presentation.RestoreOverrides(id, _savedPresentation);
                            if (!restored.Success) owner._state.Note(restored.Detail ?? "Actor presentation restore failed.");
                        }
                    });
                }
            }
            return result;
        }

        public bool Release()
        {
            if (Actor is null)
                return true;
            if (!owner._discovery.ReleaseObservation(observation, Actor, ReleaseCurrent))
                return false;
            Actor = null;
            return true;
        }

        private bool ReleaseCurrent(IActor live)
        {
            var id = owner._actorId?.Invoke(live);
            var presentation = id is { } bound ? owner._presentation?.OverridesFor(bound) : null;
            // The entry keeps its authored snapshot, not a freshly reacquired
            // body's temporary animation while an undo restore is pending.
            if (!_captured)
            {
                _savedState = owner._state?.Read(live);
                _savedName = owner._state?.GetName(live);
                _savedPresentation = presentation;
                _captured = true;
            }
            if (id is { } current && owner._presentation?.ResetActor(current).Success == false)
                return false;
            if (owner._release(live))
                return true;
            if (id is { } stillBound)
                owner._presentation?.RestoreOverrides(stillBound, presentation);
            return false;
        }
    }
}
