using System.Numerics;
using Dalamud.Plugin.Services;
using Poser.Application.World;
using Poser.Application.Animation;
using Poser.Application.Lifecycle;
using Poser.Config;
using Poser.Core;
using Poser.Domain.Identity;
using Poser.Domain.Operations;
using Poser.Domain.Scene;
using Poser.Entities;
using Poser.Game.Journal;
using Poser.Game.Lighting;
using Poser.Game.Scene;
using Poser.Game.WorldObjects;
using Poser.Services;

namespace Poser.Game.World;

public sealed class WorldService : IWorldService, IDisposable
{
    private readonly IFramework _framework;
    private readonly IGPoseService _gpose;
    private readonly WorldActorDiscovery _actors;
    private readonly WorldActorSession _actorClaims;
    private readonly WorldObjectService _objects;
    private readonly LightingService _lights;
    private readonly SceneLifecycleHistory _history;
    private readonly IEntityBindings _bindings;
    private readonly AnimationSession _animation;
    private readonly ConfigurationService _config;
    private readonly WorldCandidateBook _book = new();
    private readonly Dictionary<WorldKinds, long> _refreshed = new();
    private readonly WorldClaimBook _claims = new();
    private WorldCandidateEntry? _hovered;
    private WorldCandidateId? _hoveredId;
    private bool _disposed;
    private readonly ISessionGenerationSource _sessions;
    private SessionGeneration? _session;
    public WorldSnapshot Snapshot => _book.Snapshot;
    public event Action? Changed;

    // Scene files contain spawnable copies, never candidate/native addresses.
    // These runtime-only operations bypass per-entity history because the
    // scene workflow records the whole load as one step.
    internal IReadOnlyList<AdoptedWorldObject> Adopted => _objects.Adopted;
    internal int Count => _objects.Count;
    internal AdoptedWorldObject? Spawn(string path, Transform placement, bool visible, out string? detail) =>
        _objects.Spawn(path, placement, visible, out detail);
    internal bool Release(AdoptedWorldObject entity) => _objects.Release(entity);
    internal void ReleaseAll() => _objects.ReleaseAll();

    public Task<WorldRelease> ReleaseSceneObjects() => _framework.RunOnFrameworkThread(() =>
    {
        _history.ReleaseAllWorldObjects();
        return new WorldRelease(WorldCommandStatus.Applied);
    });

    public WorldService(IFramework framework, IGPoseService gpose, WorldActorDiscovery actors,
        WorldActorSession actorClaims, WorldObjectService objects, LightingService lights,
        SceneLifecycleHistory history, IEntityBindings bindings, AnimationSession animation, ConfigurationService config,
        ISessionGenerationSource sessions)
    {
        _framework = framework; _gpose = gpose; _actors = actors; _actorClaims = actorClaims;
        _objects = objects; _lights = lights; _history = history; _bindings = bindings;
        _animation = animation; _config = config;
        _sessions = sessions; _session = sessions.ActiveSessionGeneration;
        _framework.Update += Tick;
    }

    public Task<WorldSnapshot> Refresh(WorldKinds kinds, bool force = false) =>
        _framework.RunOnFrameworkThread(() => RefreshCore(kinds, force));

    private WorldSnapshot RefreshCore(WorldKinds kinds, bool force)
    {
        ReconcileSession();
        if (_disposed || !_gpose.IsGPosing) return Snapshot;
        WorldKinds due = WorldKinds.None;
        foreach (var kind in new[] { WorldKinds.Actor, WorldKinds.Light, WorldKinds.Object, WorldKinds.Effect })
            if ((kinds & kind) != 0 && (force || !_refreshed.TryGetValue(kind, out long at) || System.Environment.TickCount64 - at >= 500))
            {
                due |= kind;
                _refreshed[kind] = System.Environment.TickCount64;
            }
        if (due == WorldKinds.None) return Snapshot;
        var entries = new List<WorldCandidateEntry>();
        if ((due & WorldKinds.Actor) != 0)
            foreach (var row in _actors.RefreshCandidates())
            {
                entries.Add(new(row.Id, WorldKinds.Actor, row.Name, row.Position,
                    () => _actors.TryRetainCandidate(row.Id, out _),
                    () => _actorClaims.Adopt(row.Id, out var actor).Success && actor != null
                        ? () => _bindings.GetActorId(actor) is { } id ? SelectionId.ForActor(id) : null : null,
                    on => _actors.SetHighlight(row.Id, on)));
            }
        if ((due & WorldKinds.Light) != 0)
            foreach (var row in _lights.GetWorldLightCandidates())
                entries.Add(new((row.Handle, row.Generation), WorldKinds.Light, "World light", row.Position,
                    () => _lights.GetWorldLightCandidates().Any(l => l.Handle == row.Handle && l.Generation == row.Generation),
                    () => _lights.CaptureWorldLight(row) is { } light
                        ? () => _bindings.GetLightId(light) is { } id ? SelectionId.ForLight(id) : null : null));
        if ((due & WorldKinds.Object) != 0) AddObjects(false);
        if ((due & WorldKinds.Effect) != 0) AddObjects(true);
        _book.Refresh(due, entries);
        Changed?.Invoke();
        return Snapshot;

        void AddObjects(bool effects)
        {
            foreach (var row in effects ? _objects.GetEffectCandidates() : _objects.GetCandidates())
            {
                if (!_objects.TryObserve(row.Address, out var identity)) continue;
                byte outline = 0;
                bool marked = false;
                entries.Add(new(identity, effects ? WorldKinds.Effect : WorldKinds.Object, row.Name, row.Position,
                    () => _objects.TryObserve(row.Address, out var current) && current == identity,
                    () => _history.AdoptWorldObject(row.Address) is IWorldObject obj
                        ? () => _bindings.GetWorldObjectId(obj) is { } id ? SelectionId.ForWorldObject(id) : null : null,
                    on =>
                    {
                        if (!_objects.TryObserve(row.Address, out var current) || current != identity) return;
                        if (on)
                        {
                            marked = _objects.TryReadOutline(row.Address, out outline);
                            if (marked) _objects.WriteOutline(row.Address, WorldObjectOutline.Hover);
                        }
                        else if (marked) _objects.WriteOutline(row.Address, outline);
                    }));
            }
        }
    }

    public async Task<WorldAcquisition> Acquire(WorldCandidateId candidate)
    {
        WorldAcquisition? failure = null;
        SessionGeneration? session = null;
        Func<SelectionId?>? binding = await _framework.RunOnFrameworkThread(() =>
        {
            ReconcileSession();
            session = _session;
            if (_disposed || !_gpose.IsGPosing)
            {
                failure = new(WorldCommandStatus.Unavailable, Detail: "World borrowing requires GPose.");
                return null;
            }
            HighlightCore(null);
            var status = _book.Acquire(candidate, out var result);
            if (result == null) failure = new(status, Detail: status == WorldCommandStatus.StaleCandidate
                ? "That world asset is no longer available." : "That world asset could not be borrowed.");
            else Changed?.Invoke();
            return result;
        });
        if (binding == null) return failure ?? new(WorldCommandStatus.Refused);
        for (int tick = 0; tick < 120; tick++)
        {
            var landed = await _framework.RunOnFrameworkThread(() =>
            {
                if (_disposed || !_gpose.IsGPosing || session != _sessions.ActiveSessionGeneration)
                    return (WorldAcquisition?)null;
                var id = binding();
                if (id?.Actor is { } actor && _config.Config.SpawnFrozen) _animation.Pause(actor);
                return id is { } entity
                    ? new WorldAcquisition(WorldCommandStatus.Applied, _claims.Add(entity), entity) : null;
            });
            if (landed != null) return landed;
            if (_disposed || !_gpose.IsGPosing || session != _sessions.ActiveSessionGeneration) break;
            await _framework.RunOnTick(() => { }, delayTicks: 1);
        }
        return new(WorldCommandStatus.Unavailable, Detail: "The borrowed asset did not receive a scene identity.");
    }

    public Task<WorldRelease> Release(SelectionId entity) =>
        _framework.RunOnFrameworkThread(() => _claims.Release(entity, ReleaseCore));
    public Task<WorldRelease> Release(WorldClaimId claim) =>
        _framework.RunOnFrameworkThread(() => _claims.Release(claim, ReleaseCore));

    private WorldRelease ReleaseCore(SelectionId entity)
    {
        if (_disposed) return new(WorldCommandStatus.Unavailable);
        if (entity is { Kind: SceneEntityKind.Actor, Actor: { } actorId })
        {
            var actor = _bindings.Resolve(actorId).Value;
            if (actor == null) return new(WorldCommandStatus.AlreadyReleased);
            return _actorClaims.Release(actor) ? new(WorldCommandStatus.Applied) : new(WorldCommandStatus.Refused, "That actor is not borrowed from the world.");
        }
        if (entity.WorldObject is { } objectId)
        {
            var obj = _bindings.Resolve(objectId).Value;
            if (obj == null || !obj.IsValid) return new(WorldCommandStatus.AlreadyReleased);
            _history.ReleaseWorldObject(obj);
            return new(WorldCommandStatus.Applied);
        }
        if (entity.Light is { } lightId)
        {
            var light = _bindings.Resolve(lightId).Value;
            if (light == null || !light.IsValid) return new(WorldCommandStatus.AlreadyReleased);
            if (light.Ownership == LightOwnership.Spawned) return new(WorldCommandStatus.Refused, "That light is not borrowed.");
            _lights.ReleaseLight(light);
            return new(WorldCommandStatus.Applied);
        }
        return new(WorldCommandStatus.Refused, "That entity is not a borrowed world asset.");
    }

    public void Highlight(WorldCandidateId? candidate) => _ = _framework.RunOnFrameworkThread(() =>
    {
        if (!_disposed) HighlightCore(candidate);
    });
    private void HighlightCore(WorldCandidateId? candidate)
    {
        if (_hoveredId == candidate) return;
        _hovered?.Highlight?.Invoke(false);
        _hovered = null; _hoveredId = null;
        if (candidate is { } id && _book.TryGet(id, out var entry) && entry.Valid())
        {
            entry.Highlight?.Invoke(true);
            _hovered = entry; _hoveredId = id;
        }
    }

    private void Tick(IFramework framework)
    {
        if (!_objects.AnchorPumpedFromRender) _objects.HoldPausedAnimations();
        ReconcileSession();
    }
    private void ReconcileSession()
    {
        var session = _sessions.ActiveSessionGeneration;
        if (_session != session)
        {
            HighlightCore(null); _book.Clear(); _claims.Clear(); _refreshed.Clear(); Changed?.Invoke();
            _session = session;
        }
    }
    public void Dispose()
    {
        _disposed = true;
        _framework.Update -= Tick;
        _ = _framework.RunOnFrameworkThread(() => { HighlightCore(null); _book.Clear(); _claims.Clear(); });
    }
}
