using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Plugin.Services;
using Poser.Application.Actors;
using Poser.Application.Animation;
using Poser.Application.Selection;
using Poser.Config;
using Poser.Domain.Identity;
using Poser.Entities;
using Poser.Game.Bindings;
using Poser.Services;

namespace Poser.UI;

/// <summary>Which world listing an adoption handle stands for.</summary>
public enum WorldAdoptionKind
{
    Actor,
    Light,
}

/// <summary>
/// One thing in the world that is NOT in the scene yet, as a pointer-free row
/// for the overlay: a world point to project, a name to say, and exactly one
/// of the two listing keys behind it. Valid only until the next refresh — the
/// services behind both keys refuse a key they no longer recognise, so a stale
/// row can only produce a typed refusal, never a wrong adoption.
/// </summary>
public readonly record struct WorldAdoptionCandidate(
    WorldAdoptionKind Kind,
    string Name,
    Vector3 Position,
    float DistanceFromPlayer,
    WorldActorCandidateId Actor = default,
    WorldLightCandidate Light = default);

/// <summary>
/// The overlay's adoption listing: everything the world holds that the scene
/// does not, refreshed on a cadence, plus the one call that takes a listed
/// thing into the scene.
///
/// <para>Adoption never writes to the world entity. An actor is CLONED — the
/// same typed import the spawn browser's World tab performs, which lands a
/// Poser-owned body in the GPose band where the character write gate admits
/// it; a reference-adopted overworld actor would keep its sub-201 index and
/// every posing write would refuse it. A light is CAPTURED — a Poser-owned
/// copy plus the copy-and-suppress contract that restores the original when
/// the copy is released, so removing it releases rather than destroys
/// (Ktisis LightEntity.ResetWorldLight; Brio RemoveWroldLight). Both listings
/// already exclude what the scene holds: overworld actors are outside the
/// GPose band by construction, and a captured light is filtered out of the
/// candidates by its original's handle.</para>
///
/// <para>Both listings and both adoptions are framework-thread work; the
/// services behind them refuse off-thread rather than racing the game, which
/// is why this is pumped from the overlay's own draw (the game's main thread)
/// and never from a worker.</para>
/// </summary>
public sealed class WorldAdoptionSource
{
    /// <summary>How long a listing is reused before it is re-read. The world
    /// is frozen in GPose, so a slower cadence than the frame is not a
    /// staleness problem; a per-frame re-read of two whole object
    /// enumerations would be.</summary>
    private const long RefreshIntervalMs = 500;

    /// <summary>Ktisis' own default adoption range (OverlayConfig
    /// WorldCameraRange, 30y): a zone holds far more lights and actors than
    /// the screen can carry handles for, and the far ones are noise around
    /// whatever the camera is actually looking at.</summary>
    public const float RangeYalms = 30f;

    // The concrete discovery service, for the same reason the spawn browser
    // takes it: the import overload that hands the clone wrapper back — the
    // thing a pending-select needs — is not on the read port.
    private readonly Game.WorldActorDiscovery _worldActors;
    private readonly ILightingService _lighting;
    private readonly StableBindingRegistry _bindings;
    private readonly SelectionSession _selection;
    private readonly AnimationSession _animation;
    private readonly ConfigurationService _configuration;
    private readonly IPluginLog _log;

    private readonly List<WorldAdoptionCandidate> _candidates = new();
    private long _nextRefreshMs;
    private IActor? _pendingSelectActor;
    private ILight? _pendingSelectLight;

    public WorldAdoptionSource(
        Game.WorldActorDiscovery worldActors,
        ILightingService lighting,
        StableBindingRegistry bindings,
        SelectionSession selection,
        AnimationSession animation,
        ConfigurationService configuration,
        IPluginLog log)
    {
        _worldActors = worldActors;
        _lighting = lighting;
        _bindings = bindings;
        _selection = selection;
        _animation = animation;
        _configuration = configuration;
        _log = log;
    }

    /// <summary>The workspace bar's World switch. Session state, not config —
    /// Ktisis' ShowWorldObjects is runtime-only for the same reason: it is a
    /// mode you are in while hunting for something to add, not a preference.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>The current listing, nearest first. Empty whenever the switch
    /// is off, so nothing is enumerated for a hidden layer.</summary>
    public IReadOnlyList<WorldAdoptionCandidate> Candidates => _candidates;

    /// <summary>Pumped once per overlay frame: re-lists on the cadence and
    /// finishes any adoption whose entity the scene has now bound.</summary>
    public void Tick()
    {
        ReconcilePending();
        if (!Enabled)
        {
            _candidates.Clear();
            return;
        }
        long now = Environment.TickCount64;
        if (now < _nextRefreshMs)
            return;
        _nextRefreshMs = now + RefreshIntervalMs;
        Refresh();
    }

    /// <summary>Takes a listed thing into the scene. The world entity itself
    /// is never written: an actor is cloned and a light is copied with its
    /// original suppressed until release.</summary>
    public void Adopt(in WorldAdoptionCandidate candidate)
    {
        switch (candidate.Kind)
        {
            case WorldAdoptionKind.Actor:
                AdoptActor(candidate.Actor);
                break;
            case WorldAdoptionKind.Light:
                AdoptLight(candidate.Light);
                break;
        }
        // The listing the click came from now names something the scene holds:
        // re-read at once rather than leaving its handle up for half a second.
        _nextRefreshMs = 0;
    }

    private void AdoptActor(WorldActorCandidateId id)
    {
        var result = _worldActors.CloneCandidate(id, out var clone);
        if (result.Success)
        {
            // The clone is bound by the scene's own rescan, so the wrapper the
            // typed import hands back is resolved on a later tick.
            _pendingSelectActor = clone;
            return;
        }
        _log.Information(
            $"[Overlay] world actor adoption refused: {result.Status} "
            + $"{result.Detail}");
    }

    private void AdoptLight(WorldLightCandidate candidate)
    {
        var captured = _lighting.CaptureWorldLight(candidate);
        if (captured == null)
        {
            _log.Information(
                "[Overlay] world light adoption refused: the light could not "
                + "be captured");
            return;
        }
        _pendingSelectLight = captured;
    }

    /// <summary>Selects what was just adopted, once the scene has bound it —
    /// the same two-step every spawn row uses, because the registry scan that
    /// mints the id runs after the call that created the entity.</summary>
    private void ReconcilePending()
    {
        if (_pendingSelectLight is { } light &&
            _bindings.GetLightId(light) is { } lightId)
        {
            _selection.Select(SelectionId.ForLight(lightId));
            _pendingSelectLight = null;
        }

        if (_pendingSelectActor is not { } actor)
            return;
        if (_bindings.GetActorId(actor) is not { } actorId)
            return;
        _selection.Select(SelectionId.ForActor(actorId));
        _pendingSelectActor = null;
        FreezeIfRequested(actorId);
    }

    /// <summary>Spawn-frozen applies to every actor Poser adds, this one
    /// included: the toggle is about actors arriving mid-idle, not about which
    /// surface added them.</summary>
    private void FreezeIfRequested(ActorId actor)
    {
        if (!_configuration.Config.SpawnFrozen)
            return;
        var result = _animation.Pause(actor);
        if (!result.Success)
            _log.Information(
                $"[Overlay] the adopted actor could not be frozen: "
                + $"{result.Detail}");
    }

    private void Refresh()
    {
        _candidates.Clear();
        foreach (var actor in _worldActors.RefreshCandidates())
        {
            if (actor.DistanceFromPlayer > RangeYalms)
                continue;
            _candidates.Add(new WorldAdoptionCandidate(
                WorldAdoptionKind.Actor,
                actor.Name,
                actor.Position,
                actor.DistanceFromPlayer,
                Actor: actor.Id));
        }

        foreach (var light in _lighting.GetWorldLightCandidates())
        {
            if (light.DistanceFromPlayer > RangeYalms)
                continue;
            _candidates.Add(new WorldAdoptionCandidate(
                WorldAdoptionKind.Light,
                "World light",
                light.Position,
                light.DistanceFromPlayer,
                Light: light));
        }
    }
}
