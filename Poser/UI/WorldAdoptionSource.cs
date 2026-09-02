using Poser.Domain.Actors;
using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Plugin.Services;
using Poser.Application.Actors;
using Poser.Application.Animation;
using Poser.Application.Selection;
using Poser.Application.Transforms;
using Poser.Config;
using Poser.Domain.Identity;
using Poser.Entities;
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

    /// <summary>A world effect the map plays — its own class (effects are
    /// separate from objects everywhere), adopted by reference exactly
    /// like a map object.</summary>
    Effect,
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
        WorldAdoptionKind.Effect,
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
    private readonly IWorldActorDiscovery _worldActors;
    private readonly ILightingService _lighting;
    private readonly IWorldObjectService _worldObjects;

    // The point every handle's range is measured from. Never the player's.
    private readonly ICameraService _camera;

    // Adopting a MAP object is a scene-lifecycle act with an exact inverse
    // (release-and-restore), so it goes through the seam that files one in the
    // same history the transforms use — unlike the actor clone and the light
    // capture beside it, for neither of which this seam can state an inverse.
    private readonly ISceneLifecycleHistory _lifecycle;
    private readonly IEntityBindings _bindings;
    private readonly SelectionSession _selection;
    private readonly AnimationSession _animation;
    private readonly ConfigurationService _configuration;
    private readonly IPluginLog _log;
    private readonly UserNotices _notices;

    private readonly List<WorldAdoptionCandidate> _candidates = new();
    private long _nextRefreshMs;
    private IActor? _pendingSelectActor;
    private ILight? _pendingSelectLight;
    private IWorldObject? _pendingSelectWorldObject;

    public WorldAdoptionSource(
        IWorldActorDiscovery worldActors,
        ILightingService lighting,
        IWorldObjectService worldObjects,
        ICameraService camera,
        ISceneLifecycleHistory lifecycle,
        IEntityBindings bindings,
        SelectionSession selection,
        AnimationSession animation,
        ConfigurationService configuration,
        IPluginLog log,
        UserNotices notices,
        TransformHistory history,
        IActorSpawnService spawns)
    {
        _history = history;
        _spawns = spawns;
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
        _notices = notices;
    }

    /// <summary>
    /// Says a refusal to the USER as well as to the log.
    ///
    /// <para>Every refusal below is the answer to a CLICK — the user aimed at a
    /// handle in the world and pressed it. A click that does nothing and
    /// explains nothing is indistinguishable from a click that missed, which
    /// is exactly how these read in game: the handle stayed, and the scene did
    /// not change. The log line is kept beside it because the log carries the
    /// status codes and the notice carries the sentence.</para>
    ///
    /// <para>Null-safe on purpose: the filter contract tests construct this
    /// source with no services at all to exercise the class-filter derivation,
    /// and a refusal path is not what they are pinning.</para>
    /// </summary>
    private void Refuse(string logLine, string spoken)
    {
        _log?.Information(logLine);
        _notices?.Refused(spoken);
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

    public bool ShowEffects { get; set; }

    /// <summary>
    /// Whether the layer draws at all — DERIVED from the class filters rather
    /// than held beside them. A master switch over exactly two filters is a
    /// third state that can disagree with both: it reads on while no class
    /// draws, or off while both classes read on, and the sidebar then has two
    /// answers to one question.
    /// </summary>
    public bool Enabled =>
        ShowActors || ShowLights || ShowWorldObjects || ShowEffects;

    /// <summary>One class's filter, as one call so no caller restates the
    /// mapping.</summary>
    public bool IsShown(WorldAdoptionKind kind) => kind switch
    {
        WorldAdoptionKind.Light => ShowLights,
        WorldAdoptionKind.WorldObject => ShowWorldObjects,
        WorldAdoptionKind.Effect => ShowEffects,
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
            case WorldAdoptionKind.Effect:
                ShowEffects = shown;
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
        ShowEffects = false;
        // Before the listing goes, because clearing the hover needs the thing
        // it is painted on to still be nameable.
        SetHovered(null);
        _candidates.Clear();
    }

    // ── the hover mark ───────────────────────────────────────────────────

    /// <summary>What the pointer is over, and the outline byte that object
    /// stood with before the mark was put on it. Held together because they
    /// are cleared together: the restore has to write back what THIS hover
    /// found, not a value stated anywhere else.</summary>
    private WorldAdoptionCandidate? _hoveredCandidate;
    private byte _hoveredOutline = WorldObjectOutline.None;
    private bool _hoveredActorPainted;

    /// <summary>
    /// Marks the world entity under the pointer, and unmarks whatever was
    /// marked before it.
    ///
    /// <para>Ktisis' <c>SetHovered</c>/<c>SetHoveredActor</c> pair
    /// (<c>Interface/Overlay/SceneDraw.cs:340-353</c>), including its
    /// same-target early return: the overlay calls this EVERY frame, and
    /// re-writing the mark each one would restore the mark's own value as the
    /// resting one. The overlay also calls it with null on every frame nothing
    /// is hovered (<c>:84-87</c>), which is what makes leaving clear it.</para>
    ///
    /// <para>WHICH CLASSES CARRY A MARK. Borrowed map objects wear the game's
    /// own outline; overworld actors wear the game's own highlight. Lights
    /// wear NEITHER, and that is not an omission — Ktisis marks neither
    /// (<c>SetHovered</c> is called only from its world-object pass,
    /// <c>:223</c>, and <c>SetHoveredActor</c> only from its actor pass,
    /// <c>:266</c>; its light pass sets a hover flag and no mark at all). A
    /// scene light has no mesh for an outline to trace.</para>
    ///
    /// <para>THE PAIRING IS THE CONTRACT: nothing is ever painted without this
    /// method holding what unpaints it, and every exit — a new hover, no
    /// hover, the adoption itself, and the session's end — runs through here.
    /// </para>
    /// </summary>
    public void SetHovered(WorldAdoptionCandidate? candidate)
    {
        if (Equals(candidate, _hoveredCandidate))
            return;

        if (_hoveredCandidate is { } previous)
        {
            switch (previous.Kind)
            {
                case WorldAdoptionKind.WorldObject:
                case WorldAdoptionKind.Effect:
                    _worldObjects?.WriteOutline(
                        previous.WorldObject, _hoveredOutline);
                    break;
                case WorldAdoptionKind.Actor when _hoveredActorPainted:
                    _worldActors?.SetHighlight(previous.Actor, false);
                    break;
            }
        }

        _hoveredCandidate = null;
        _hoveredOutline = WorldObjectOutline.None;
        _hoveredActorPainted = false;

        if (candidate is not { } next)
            return;

        switch (next.Kind)
        {
            case WorldAdoptionKind.WorldObject:
            case WorldAdoptionKind.Effect:
                if (_worldObjects == null ||
                    !_worldObjects.TryReadOutline(
                        next.WorldObject, out _hoveredOutline))
                    return;
                _worldObjects.WriteOutline(
                    next.WorldObject, WorldObjectOutline.Hover);
                break;
            case WorldAdoptionKind.Actor:
                _hoveredActorPainted =
                    _worldActors?.SetHighlight(next.Actor, true) == true;
                if (!_hoveredActorPainted)
                    return;
                break;
            default:
                // A light takes no mark; remembering it as hovered anyway
                // would make the next frame's early return skip a class that
                // DOES take one.
                return;
        }

        _hoveredCandidate = next;
    }

    /// <summary>The current listing, nearest first. Empty whenever every class
    /// is off, so nothing is enumerated for a hidden layer.</summary>
    public IReadOnlyList<WorldAdoptionCandidate> Candidates => _candidates;

    /// <summary>Pumped once per overlay frame: re-lists on the cadence and
    /// finishes any adoption whose entity the scene has now bound.</summary>
    public void Tick()
    {
        // The FALLBACK pump: the anchor normally rides the camera's
        // render seam, and this draw-time seat only runs when that hook
        // is gone. It must run before the Enabled early-out: pausing
        // needs no adoption class shown.
        if (!_worldObjects.AnchorPumpedFromRender)
            _worldObjects.HoldPausedAnimations();
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
        // The mark comes off BEFORE the thing is taken: an adopted object
        // leaves the candidate listing, and a mark whose owner is no longer
        // listed has nothing left to unpaint it.
        SetHovered(null);
        switch (candidate.Kind)
        {
            case WorldAdoptionKind.Actor:
                AdoptActor(candidate.Actor);
                break;
            case WorldAdoptionKind.Light:
                AdoptLight(candidate.Light);
                break;
            case WorldAdoptionKind.WorldObject:
            case WorldAdoptionKind.Effect:
                AdoptWorldObject(candidate.WorldObject);
                break;
        }
        // The listing the click came from now names something the scene holds:
        // re-read at once rather than leaving its handle up for half a second.
        _nextRefreshMs = 0;
    }

    private readonly TransformHistory _history;
    private readonly IActorSpawnService _spawns;

    private void AdoptActor(WorldActorCandidateId id)
    {
        var result = _worldActors.CloneCandidate(id, out var clone);
        if (result.Success)
        {
            // The clone is bound by the scene's own rescan, so the wrapper the
            // typed import hands back is resolved on a later tick.
            _pendingSelectActor = clone;
            if (clone is { } adopted)
            {
                // The undo hands the body back; the redo adopts it again
                // from the listing, and refuses if the listing has moved on.
                var address = adopted.Address;
                _history.Append(new JournalStep(
                    "Add actor from the world",
                    () => _spawns.RemoveActorFromScene(adopted)
                        || _spawns.AdoptFromWorld(address) is null,
                    () => _spawns.AdoptFromWorld(address) is not null));
            }
            return;
        }
        Refuse(
            $"[Overlay] world actor adoption refused: {result.Status} "
            + $"{result.Detail}",
            string.IsNullOrWhiteSpace(result.Detail)
                ? "That actor could not be added to the scene."
                : $"That actor could not be added to the scene: {result.Detail}");
    }

    private void AdoptLight(WorldLightCandidate candidate)
    {
        var captured = _lighting.CaptureWorldLight(candidate);
        if (captured == null)
        {
            Refuse(
                "[Overlay] world light adoption refused: the light could not "
                + "be captured",
                "That light could not be taken into the scene. It is either "
                + "already borrowed, or it has gone since the handle was "
                + "drawn.");
            return;
        }
        _pendingSelectLight = captured;
    }

    /// <summary>
    /// Takes one BG object into the scene BY REFERENCE — the map's own object,
    /// not a copy of it. Nothing is written to it here: the claim records where
    /// it stood, and every way that claim can end writes that back (see
    /// IWorldObjectService's restore contract). The adoption is journaled, so an
    /// undo releases it and the map has its object back.
    /// </summary>
    private void AdoptWorldObject(nint address)
    {
        if (_lifecycle.AdoptWorldObject(address)
            is IWorldObject adopted)
        {
            _pendingSelectWorldObject = adopted;
            return;
        }
        Refuse(
            "[Overlay] world object adoption refused: the object could not "
            + "be taken",
            "That object could not be borrowed. The map no longer holds it "
            + "there.");
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
            // The actor DID arrive; only the freeze the setting asked for did
            // not. Said as its own sentence so it cannot read as the adoption
            // having failed.
            Refuse(
                $"[Overlay] the adopted actor could not be frozen: "
                + $"{result.Detail}",
                "The actor was added, but could not be frozen.");
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

        if (ShowEffects)
        {
            foreach (var effect in _worldObjects.GetEffectCandidates())
            {
                float range = HorizontalDistance(effect.Position, eye);
                if (range > RangeYalms)
                    continue;
                _candidates.Add(new WorldAdoptionCandidate(
                    WorldAdoptionKind.Effect,
                    effect.Name,
                    effect.Position,
                    range,
                    WorldObject: effect.Address));
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
