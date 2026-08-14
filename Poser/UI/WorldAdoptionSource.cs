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

    /// <summary>A BG/layout object the map placed — the class Ktisis is alone
    /// in offering, and the only one that is ADOPTED BY REFERENCE: the actor
    /// class clones and the light class copies, while this one takes the map's
    /// own object and gives it back on release.</summary>
    WorldObject,
}

/// <summary>The kinds this source can actually list, in the order the shell
/// states them. Declared beside the enum rather than read off it: the enum
/// names what a candidate CAN be, and a kind with no discovery behind it yet
/// would draw a footer glyph that cannot change anything. A class becomes a
/// footer glyph by landing here, and only once its discovery, filter and
/// counter are all in place.</summary>
public static class WorldAdoptionClasses
{
    public static readonly WorldAdoptionKind[] All =
    [
        WorldAdoptionKind.Actor,
        WorldAdoptionKind.Light,
        WorldAdoptionKind.WorldObject,
    ];
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
    float DistanceFromCamera,
    WorldActorCandidateId Actor = default,
    WorldLightCandidate Light = default,
    nint WorldObject = default);

/// <summary>
/// The overlay's adoption listing: everything the world holds that the scene
/// does not, refreshed on a cadence, plus the one call that takes a listed
/// thing into the scene.
///
/// <para>Adoption never writes to the world entity. An actor is CLONED — the
/// typed world import, which lands a Poser-owned body in the GPose band where
/// the character write gate admits
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

    /// <summary>
    /// Ktisis measures the adoption range FROM THE CAMERA, and horizontally —
    /// <c>Ktisis/Interface/Overlay/SceneDraw.cs:326-334</c> builds a Vector2 of
    /// the camera's X and Z and compares against the candidate's X and Z, and
    /// the setting behind it is named <c>WorldCameraRange</c>
    /// (<c>Data/Config/Sections/OverlayConfig.cs:32</c>). Both halves are
    /// deliberate: the handles are for what you are LOOKING AT, not for what
    /// you are standing near — in GPose the two are routinely a zone apart —
    /// and a ground-plane radius keeps a balcony two floors up from vanishing
    /// while its neighbour on your own floor stays.
    ///
    /// <para>The distance the discovery services hand over is from the PLAYER,
    /// which is a different question (the spawn browser's list asks it, and is
    /// right to). It is recomputed here, once, for all three classes: two
    /// classes culling from one point while the third culls from another is
    /// exactly the inconsistency one shared range is for.</para>
    /// </summary>
    private static float HorizontalDistance(Vector3 point, Vector3 camera)
    {
        float x = point.X - camera.X;
        float z = point.Z - camera.Z;
        return MathF.Sqrt((x * x) + (z * z));
    }

    // The concrete discovery service, for the same reason the spawn browser
    // takes it: the import overload that hands the clone wrapper back — the
    // thing a pending-select needs — is not on the read port.
    private readonly Game.WorldActorDiscovery _worldActors;
    private readonly ILightingService _lighting;
    private readonly Game.WorldObjects.WorldObjectService _worldObjects;

    // The point every handle's range is measured from. Never the player's.
    private readonly ICameraService _camera;

    // Adopting a MAP object is a scene-lifecycle act with an exact inverse
    // (release-and-restore), so it goes through the seam that files one in the
    // same history the transforms use — unlike the actor clone and the light
    // capture beside it, for neither of which this seam can state an inverse.
    private readonly Game.Scene.SceneLifecycleHistory _lifecycle;
    private readonly StableBindingRegistry _bindings;
    private readonly SelectionSession _selection;
    private readonly AnimationSession _animation;
    private readonly ConfigurationService _configuration;
    private readonly IPluginLog _log;

    private readonly List<WorldAdoptionCandidate> _candidates = new();
    private long _nextRefreshMs;
    private IActor? _pendingSelectActor;
    private ILight? _pendingSelectLight;
    private Game.WorldObjects.AdoptedWorldObject? _pendingSelectWorldObject;

    public WorldAdoptionSource(
        Game.WorldActorDiscovery worldActors,
        ILightingService lighting,
        Game.WorldObjects.WorldObjectService worldObjects,
        ICameraService camera,
        Game.Scene.SceneLifecycleHistory lifecycle,
        StableBindingRegistry bindings,
        SelectionSession selection,
        AnimationSession animation,
        ConfigurationService configuration,
        IPluginLog log)
    {
        _worldActors = worldActors;
        _lighting = lighting;
        _worldObjects = worldObjects;
        _camera = camera;
        _lifecycle = lifecycle;
        _bindings = bindings;
        _selection = selection;
        _animation = animation;
        _configuration = configuration;
        _log = log;
    }

    /// <summary>Whether the world's addable ACTORS draw handles. Session
    /// state, not config — Ktisis' ShowWorldObjects is runtime-only for the
    /// same reason: it is a mode you are in while hunting for something to
    /// add, not a preference.</summary>
    public bool ShowActors { get; set; }

    /// <summary>Whether the world's addable LIGHTS draw handles. A zone holds
    /// far more of one class than the other, so hunting for a light through a
    /// field of actor handles is what the class filters are for.</summary>
    public bool ShowLights { get; set; }

    /// <summary>Whether the map's own BG/layout OBJECTS draw handles. Its own
    /// filter beside the other two because a zone holds an order of magnitude
    /// more of them than of either, so hunting for a light through a field of
    /// furniture handles is exactly what the class filters are for.</summary>
    public bool ShowWorldObjects { get; set; }

    /// <summary>
    /// Whether the layer draws at all — DERIVED from the class filters rather
    /// than held beside them. A master switch over exactly two filters is a
    /// third state that can disagree with both: it reads on while no class
    /// draws, or off while both classes read on, and the sidebar then has two
    /// answers to one question.
    /// </summary>
    public bool Enabled => ShowActors || ShowLights || ShowWorldObjects;

    /// <summary>One class's filter, as one call so no caller restates the
    /// mapping.</summary>
    public bool IsShown(WorldAdoptionKind kind) => kind switch
    {
        WorldAdoptionKind.Light => ShowLights,
        WorldAdoptionKind.WorldObject => ShowWorldObjects,
        _ => ShowActors,
    };

    /// <summary>Sets one class's filter. Turning the last one off leaves the
    /// layer off, because the layer IS its classes.</summary>
    public void SetShown(WorldAdoptionKind kind, bool shown)
    {
        switch (kind)
        {
            case WorldAdoptionKind.Light:
                ShowLights = shown;
                break;
            case WorldAdoptionKind.WorldObject:
                ShowWorldObjects = shown;
                break;
            default:
                ShowActors = shown;
                break;
        }
        // The listing is stale the moment a filter moves: re-read on the next
        // tick rather than leaving the outgoing class's handles up for the
        // rest of the cadence.
        _nextRefreshMs = 0;
    }

    /// <summary>The session ended: the next one starts with the world
    /// unmarked, exactly as the armature toggle does.</summary>
    public void EndSession()
    {
        ShowActors = false;
        ShowLights = false;
        ShowWorldObjects = false;
        _candidates.Clear();
    }

    /// <summary>The current listing, nearest first. Empty whenever every class
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
            case WorldAdoptionKind.WorldObject:
                AdoptWorldObject(candidate.WorldObject);
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

    /// <summary>
    /// Takes one BG object into the scene BY REFERENCE — the map's own object,
    /// not a copy of it. Nothing is written to it here: the claim records where
    /// it stood, and every way that claim can end writes that back (see
    /// WorldObjectService's restore contract). The adoption is journaled, so an
    /// undo releases it and the map has its object back.
    /// </summary>
    private void AdoptWorldObject(nint address)
    {
        if (_lifecycle.AdoptWorldObject(address)
            is Game.WorldObjects.AdoptedWorldObject adopted)
        {
            _pendingSelectWorldObject = adopted;
            return;
        }
        _log.Information(
            "[Overlay] world object adoption refused: the object could not "
            + "be taken");
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

        if (_pendingSelectWorldObject is { } worldObject &&
            _bindings.GetWorldObjectId(worldObject) is { } worldObjectId)
        {
            _selection.Select(SelectionId.ForWorldObject(worldObjectId));
            _pendingSelectWorldObject = null;
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
        // A class that is off is not enumerated at all: the filter is not a
        // draw-time skip over a listing nobody asked for — the enumeration is
        // the expensive half.
        var eye = _camera.GetCameraPosition();
        if (ShowActors)
        {
            foreach (var actor in _worldActors.RefreshCandidates())
            {
                float range = HorizontalDistance(actor.Position, eye);
                if (range > RangeYalms)
                    continue;
                _candidates.Add(new WorldAdoptionCandidate(
                    WorldAdoptionKind.Actor,
                    actor.Name,
                    actor.Position,
                    range,
                    Actor: actor.Id));
            }
        }

        if (ShowLights)
        {
            foreach (var light in _lighting.GetWorldLightCandidates())
            {
                float range = HorizontalDistance(light.Position, eye);
                if (range > RangeYalms)
                    continue;
                _candidates.Add(new WorldAdoptionCandidate(
                    WorldAdoptionKind.Light,
                    "World light",
                    light.Position,
                    range,
                    Light: light));
            }
        }

        if (ShowWorldObjects)
        {
            foreach (var worldObject in _worldObjects.GetCandidates())
            {
                float range = HorizontalDistance(worldObject.Position, eye);
                if (range > RangeYalms)
                    continue;
                _candidates.Add(new WorldAdoptionCandidate(
                    WorldAdoptionKind.WorldObject,
                    worldObject.Name,
                    worldObject.Position,
                    range,
                    WorldObject: worldObject.Address));
            }
        }

        // Nearest first, across all three classes together. The services each
        // sort by their own distance-from-player, which is not the order these
        // were culled in — and the nearest handle is the one a click between
        // two overlapping dots should mean.
        _candidates.Sort(static (left, right) =>
            left.DistanceFromCamera.CompareTo(right.DistanceFromCamera));
    }
}
