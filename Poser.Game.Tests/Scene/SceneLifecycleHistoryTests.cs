using System.Collections;
using System.Numerics;
using System.Reflection;
using Poser.Application.Transforms;
using Poser.Core;
using Poser.Domain.Companions;
using Poser.Domain.Presentation;
using Poser.Domain.Scene;
using Poser.Game.Overlays;
using Poser.Game.WorldObjects;
using Poser.Entities;
using Poser.Game.Scene;
using Poser.Services;

namespace Poser.Game.Tests.Scene;

/// <summary>
/// The lifecycle seam's contract: an add or a remove is one entry in the
/// SAME history the transforms use, its two directions are exact inverses,
/// and the entity's IDENTITY survives a destroy/respawn pair so entries
/// stacked on one entity keep naming that entity rather than its corpse.
/// </summary>
public sealed class SceneLifecycleHistoryTests
{

    [Fact]
    public void Add_remove_undo_redo_preserves_latest_state_and_the_entity_slot()
    {
        var world = new World();
        var original = world.Lifecycle.SpawnProp(Apple)!;
        var moved = Transform.Identity;
        moved.Position = new Vector3(4, 5, 6);
        world.Props.Apply(original, new PropState("Fruit", Apple, moved, false));

        Assert.True(world.Undo());
        Assert.Empty(world.Props.Live);
        Assert.True(world.Redo());

        var restored = Assert.Single(world.Props.Live);
        Assert.NotSame(original, restored);
        Assert.Equal("Fruit", world.Props.Read(restored).Name);
        Assert.Equal(moved, world.Props.Read(restored).Transform);
        Assert.False(world.Props.Read(restored).Visible);

        world.Lifecycle.DestroyProp(restored);
        Assert.True(world.Undo());
        Assert.True(world.Undo());
        Assert.Empty(world.Props.Live);
    }

    [Fact]
    public void Refusals_do_not_add_history_or_discard_the_previous_entry()
    {
        var world = new World
        {
            Lighting = { RefuseSpawn = true },
            Props = { RefuseSpawn = true },
            Overlays = { RefuseCreate = true },
        };

        Assert.Null(world.Lifecycle.SpawnLight(LightKind.Spot));
        Assert.Null(world.Lifecycle.SpawnProp(Apple));
        Assert.Null(world.Lifecycle.SpawnOverlay(OverlayNodeKind.Talk));
        Assert.False(world.History.CanUndo);

        var actor = world.Lifecycle.SpawnActor("Add actor", () => world.Actors.Spawn("Lead"))!;
        Assert.Equal("Add actor", world.History.UndoDescription);
        world.Actors.RefuseDestroy = true;
        world.Lifecycle.DespawnActor(actor);
        Assert.Single(world.Actors.Live);
        Assert.Equal("Add actor", world.History.UndoDescription);
    }

    [Fact]
    public void Selection_removal_is_one_undoable_entry_and_actors_keep_their_nonjournaled_rule()
    {
        var world = new World();
        var light = world.Lifecycle.SpawnLight(LightKind.Spot)!;
        var prop = world.Lifecycle.SpawnProp(Apple)!;
        var overlay = world.Lifecycle.SpawnOverlay(OverlayNodeKind.Talk)!;
        var camera = world.Lifecycle.CreateCamera(CameraKind.Free)!;

        Assert.Equal(4, world.Lifecycle.DestroySelection(
            props: new[] { prop }, lights: new[] { light },
            cameras: new[] { camera }, overlays: new[] { overlay }));
        Assert.Equal("Remove 4 entities", world.History.UndoDescription);
        Assert.True(world.Undo());
        Assert.Single(world.Lighting.Live);
        Assert.Single(world.Props.Live);
        Assert.Single(world.Overlays.Live);
        Assert.Single(world.Cameras.Live);

        var actor = world.Lifecycle.SpawnActor("Add actor", () => world.Actors.Spawn("Lead"))!;
        prop = world.Lifecycle.SpawnProp(Apple)!;
        Assert.Equal(2, world.Lifecycle.DestroySelection(new[] { actor }, new[] { prop }));
        Assert.Equal("Remove 1 entity", world.History.UndoDescription);
        Assert.True(world.Undo());
        Assert.Empty(world.Actors.Live);
        Assert.Single(world.Props.Live);
    }

    [Fact]
    public void World_adoption_undo_redo_releases_and_reclaims_the_same_address()
    {
        var world = new World();
        var address = world.WorldObjects.Place(0x1000, MapStood);
        var claim = world.Lifecycle.AdoptWorldObject(address)!;
        world.WorldObjects.Apply(claim, new WorldObjectState(address, UserPut, false));

        Assert.True(world.Undo());
        Assert.Empty(world.WorldObjects.Live);
        Assert.Equal(MapStood, world.WorldObjects.MapPlacement(address));
        Assert.True(world.Redo());

        var restored = Assert.Single(world.WorldObjects.Live);
        Assert.Equal(address, world.WorldObjects.Read(restored).Address);
        Assert.Equal(UserPut, world.WorldObjects.Read(restored).Placement);
        Assert.False(world.WorldObjects.Read(restored).Visible);

        world.Lifecycle.ReleaseWorldObject(restored);
        Assert.Equal("Remove world object", world.History.UndoDescription);
        Assert.True(world.Undo());
        Assert.Equal(UserPut, world.WorldObjects.Read(Assert.Single(world.WorldObjects.Live)).Placement);
    }

    [Fact]
    public void History_keeps_lifecycle_entries_ordered_and_clears_slots_when_disabled()
    {
        var world = new World();
        world.History.Append(new TransformPatch("edit", [], []));
        world.Lifecycle.SpawnLight(LightKind.Spot);
        Assert.Equal("Add spot light", world.History.UndoDescription);
        Assert.True(world.Undo());
        Assert.Equal("edit", world.History.UndoDescription);
        world.History.Reconcile(static _ => false);
        Assert.True(world.History.CanUndo);

        var disabled = new World(capacity: 0);
        Assert.NotNull(disabled.Lifecycle.SpawnLight(LightKind.Spot));
        Assert.False(disabled.History.CanUndo);
        Assert.Empty(Slots(disabled.Lifecycle, "_lightSlots"));
    }

    [Fact]
    public void Add_remove_undo_redo_covers_actor_camera_and_overlay_identity()
    {
        var world = new World();
        var actor = world.Lifecycle.SpawnActor("Add actor", () => world.Actors.Spawn("Lead"))!;
        var camera = world.Lifecycle.CreateCamera(CameraKind.Free)!;
        var overlay = world.Lifecycle.SpawnOverlay(OverlayNodeKind.Talk)!;

        Assert.True(world.Undo());
        Assert.Empty(world.Overlays.Live);
        Assert.True(world.Undo());
        Assert.Empty(world.Cameras.Live);
        Assert.True(world.Undo());
        Assert.Empty(world.Actors.Live);

        Assert.True(world.Redo());
        Assert.True(world.Redo());
        Assert.True(world.Redo());
        Assert.Single(world.Actors.Live);
        Assert.Single(world.Cameras.Live);
        Assert.Single(world.Overlays.Live);
        Assert.NotSame(actor, world.Actors.Live[0]);
        Assert.NotSame(camera, world.Cameras.Live[0]);
        Assert.NotSame(overlay, world.Overlays.Live[0]);
    }

    private static ActorState Posed(Vector3 position, bool visible)
    {
        var placement = Transform.Identity;
        placement.Position = position;
        return new ActorState(placement, visible, new Poser.Files.PoseFile());
    }

    private static readonly PropModel Apple =
        new("Apple", 9001, 249, 1, "The default prop");

    private static readonly Transform MapStood = new(
        new Vector3(4f, 0f, 8f), Quaternion.Identity, Vector3.One);

    private static readonly Transform UserPut = new(
        new Vector3(40f, 6f, 80f), Quaternion.Identity, new Vector3(2f, 2f, 2f));

    private static IDictionary Slots(
        SceneLifecycleHistory lifecycle, string field) =>
        (IDictionary)typeof(SceneLifecycleHistory)
            .GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(lifecycle)!;

    // ── harness ──────────────────────────────────────────────────────────

    /// <summary>The seam over fake services, plus the undo/redo dispatch the
    /// gesture service performs on a lifecycle entry: run the direction, and
    /// move the entry between stacks only if it landed.</summary>
    private sealed class World
    {
        public TransformHistory History { get; }
        public FakeLighting Lighting { get; } = new();
        public FakeCameras Cameras { get; } = new();
        public FakeActors Actors { get; } = new();
        public FakeProps Props { get; } = new();
        public FakeOverlays Overlays { get; } = new();
        public FakeWorldObjects WorldObjects { get; } = new();
        public SceneLifecycleHistory Lifecycle { get; }

        /// <param name="capacity">Undo depth; below 1 is undo switched off.
        /// </param>
        public World(int capacity = TransformHistory.DefaultCapacity)
        {
            History = new TransformHistory(() => capacity);
            Lifecycle = new SceneLifecycleHistory(
                History, Lighting, Cameras, Actors, Props, Overlays,
                WorldObjects);
        }

        public bool Undo()
        {
            var entry = (SceneLifecyclePatch)History.PeekUndo()!;
            if (!entry.Undo())
                return false;
            History.CommitUndo(entry);
            return true;
        }

        public bool Redo()
        {
            var entry = (SceneLifecyclePatch)History.PeekRedo()!;
            if (!entry.Redo())
                return false;
            History.CommitRedo(entry);
            return true;
        }
    }

    private sealed class FakeLighting : ILightingService
    {
        private readonly List<ILight> _lights = new();

        public bool RefuseSpawn { get; set; }
        public IReadOnlyList<ILight> Live => _lights;

        public bool IsAvailable => true;
        public IReadOnlyList<ILight> Lights => _lights;
        public IReadOnlyList<GoboEntry> Gobos => Array.Empty<GoboEntry>();
        public void Dispose() { }

        public ILight? SpawnLight(LightKind kind)
        {
            if (RefuseSpawn)
                return null;
            var light = new FakeLight { Kind = kind };
            _lights.Add(light);
            return light;
        }

        public ILight? CloneLight(ILight source) => SpawnLight(source.Kind);

        public void DestroyLight(ILight light)
        {
            _lights.Remove(light);
            ((FakeLight)light).IsValid = false;
        }

        /// <summary>The light leaves without this seam's knowledge — a scene
        /// import, or the game.</summary>
        public void VanishWithoutNotice(ILight light) => DestroyLight(light);

        public ILight AddBorrowed()
        {
            var light = new FakeLight { Ownership = LightOwnership.World };
            _lights.Add(light);
            return light;
        }

        public void DestroyAllLights() => _lights.Clear();

        public bool IsSpawnedLight(ILight light) =>
            light.Ownership == LightOwnership.Spawned;

        public void ReleaseLight(ILight light) => _lights.Remove(light);
        public bool ApplyGobo(ILight light, GoboEntry gobo) => false;
        public void ClearGobo(ILight light) { }
        public IReadOnlyList<WorldLightCandidate> GetWorldLightCandidates() =>
            Array.Empty<WorldLightCandidate>();
        public ILight? CaptureWorldLight(WorldLightCandidate candidate) => null;
    }

    private sealed class FakeLight : ILight
    {
        public bool IsValid { get; set; } = true;
        public string Name { get; set; } = "Light";
        public LightKind Kind { get; set; }
        public bool IsOn { get; set; } = true;
        public Transform Transform { get; set; } = Transform.Identity;
        public Vector3 Color { get; set; } = Vector3.One;
        public float Intensity { get; set; } = 1f;
        public float Range { get; set; } = 1f;
        public float Falloff { get; set; }
        public LightFalloffType FalloffType { get; set; }
        public float SpotAngle { get; set; }
        public float FalloffAngle { get; set; }
        public Vector2 AreaAngle { get; set; }
        public bool HasReflection { get; set; }
        public bool CastsDynamicShadows { get; set; }
        public bool CastsCharacterShadow { get; set; }
        public bool CastsObjectShadow { get; set; }
        public float CharacterShadowRange { get; set; }
        public float ShadowPlaneNear { get; set; }
        public float ShadowPlaneFar { get; set; }
        public LightOwnership Ownership { get; set; } = LightOwnership.Spawned;
        public string? GoboPath => null;
        public IBone? AttachedBone { get; set; }
    }

    private sealed class FakeCameras : IVirtualCameraService
    {
        private readonly List<IVirtualCamera> _cameras = new();

        public IReadOnlyList<IVirtualCamera> Live => _cameras;

        public bool IsAvailable => true;
        public IReadOnlyList<IVirtualCamera> Cameras => _cameras;
        public IVirtualCamera? LiveCamera => null;

        public FreeCameraSpeedNotice? SpeedNotice => null;
        public void Dispose() { }

        public IVirtualCamera? CreateCamera(CameraKind kind)
        {
            var camera = new FakeCamera(kind);
            _cameras.Add(camera);
            return camera;
        }

        public IVirtualCamera? CloneCamera(IVirtualCamera source) =>
            CreateCamera(source.Kind);

        public IVirtualCamera AddDefault()
        {
            var camera = new FakeCamera(CameraKind.Game) { IsDefault = true };
            _cameras.Add(camera);
            return camera;
        }

        public void DestroyCamera(IVirtualCamera camera)
        {
            _cameras.Remove(camera);
            ((FakeCamera)camera).IsValid = false;
        }

        public void DestroyAllCameras() => _cameras.Clear();
        public void SetLive(IVirtualCamera camera) { }
        public bool SetTargetActor(
            IVirtualCamera camera, IActor actor, string displayName) => false;
        public void ClearTargetActor(IVirtualCamera camera) { }
    }

    private sealed class FakeCamera(CameraKind kind) : IVirtualCamera
    {
        public bool IsValid { get; set; } = true;
        public string Name { get; set; } = "Camera";
        public CameraKind Kind { get; } = kind;
        public bool IsLive => false;
        public bool IsDefault { get; set; }
        public bool IsLocked { get; set; }
        public Vector2 Angle { get; set; }
        public Vector2 Pan { get; set; }
        public float Roll { get; set; }
        public float Zoom { get; set; }
        public Vector2 ZoomLimits => Vector2.Zero;
        public float FoV { get; set; }
        public Vector3 PositionOffset { get; set; }
        public Vector3? FixedPosition { get; set; }
        public Vector3 TargetOffset { get; set; }
        public string TargetActorName { get; set; } = string.Empty;
        public Vector3 WorldPosition => Vector3.Zero;
        public bool DisableCollision { get; set; }
        public bool DelimitCamera { get; set; }
        public bool IsPortraitMode => false;
        public void TogglePortraitMode() { }
        public Vector3 Position { get; set; }
        public Vector3 SpawnPosition => Vector3.Zero;
        public Vector3 Rotation { get; set; }
        public bool MovementEnabled { get; set; }
        public bool Move2D { get; set; }
        public float MovementSpeed { get; set; }
        public float MouseSensitivity { get; set; }
        public bool DelimitAngle { get; set; }
        public bool Orthographic { get; set; }
        public float OrthographicZoom { get; set; }
        public bool IsTracking { get; set; }
        public CameraTrackingMode TrackingMode { get; set; }
        public IList<IBone> TrackedBones { get; } = new List<IBone>();
        public void ResetProperties() { }
    }

    /// <summary>The prop half at its port: a token per spawned prop, with the
    /// state an entry reads and writes back.</summary>
    /// <summary>
    /// The adopted-world-object half at its port: a MAP the claims are taken
    /// against, so a release genuinely gives the object back and a re-adoption
    /// finds it again — the one property that separates this half from every
    /// other, all of which destroy and re-create.
    /// </summary>
    private sealed class FakeWorldObjects : IWorldObjectLifecycle
    {
        private readonly Dictionary<nint, Transform> _map = new();
        private readonly List<object> _adopted = new();

        public bool RefuseAdopt { get; set; }
        public IReadOnlyList<object> Live => _adopted;
        public IReadOnlyList<object> WorldObjects => _adopted.ToList();

        /// <summary>Where the map stands one address, which is what every
        /// release has to put back.</summary>
        public Transform MapPlacement(nint address) => _map[address];

        public nint Place(nint address, Transform placement)
        {
            _map[address] = placement;
            return address;
        }

        public object? Adopt(nint address)
        {
            if (RefuseAdopt || !_map.ContainsKey(address))
                return null;
            var claim = new FakeWorldObject
            {
                Owner = this,
                State = new WorldObjectState(address, _map[address], true),
                MapPlacement = _map[address],
            };
            _adopted.Add(claim);
            return claim;
        }

        public bool IsLive(object worldObject) =>
            ((FakeWorldObject)worldObject).IsValid;

        public void Release(object worldObject)
        {
            var claim = (FakeWorldObject)worldObject;
            _adopted.Remove(claim);
            if (claim.IsValid)
                _map[claim.State.Address] = claim.MapPlacement;
            claim.IsValid = false;
        }

        public WorldObjectState Read(object worldObject) =>
            ((FakeWorldObject)worldObject).State;

        public void Apply(object worldObject, WorldObjectState state)
        {
            var claim = (FakeWorldObject)worldObject;
            claim.State = state;
            _map[state.Address] = state.Placement;
        }
    }

    private sealed class FakeWorldObject
    {
        public FakeWorldObjects Owner { get; set; } = null!;
        public bool IsValid { get; set; } = true;
        public WorldObjectState State { get; set; }

        /// <summary>The map's own placement, captured at adoption and written
        /// back on release. It is the fake's stand-in for the service's
        /// InitialPlacement.</summary>
        public Transform MapPlacement { get; set; }
    }

    private sealed class FakeProps : IPropLifecycle
    {
        private readonly List<object> _props = new();

        public bool RefuseSpawn { get; set; }
        public IReadOnlyList<object> Live => _props;
        public IReadOnlyList<object> Props => _props.ToList();

        public object? Spawn(PropModel model)
        {
            if (RefuseSpawn)
                return null;
            var prop = new FakeProp
            {
                State = new PropState(model.Name, model, Transform.Identity, true),
            };
            _props.Add(prop);
            return prop;
        }

        public bool IsLive(object prop) => ((FakeProp)prop).IsValid;

        public void Destroy(object prop)
        {
            _props.Remove(prop);
            ((FakeProp)prop).IsValid = false;
        }

        /// <summary>The prop leaves without this seam's knowledge — a scene
        /// import, or the game.</summary>
        public void VanishWithoutNotice(object prop) => Destroy(prop);

        public PropState Read(object prop) => ((FakeProp)prop).State;

        public void Apply(object prop, PropState state) =>
            ((FakeProp)prop).State = ((FakeProp)prop).State with
            {
                Name = state.Name,
                Transform = state.Transform,
                Visible = state.Visible,
            };
    }

    private sealed class FakeProp
    {
        public bool IsValid { get; set; } = true;
        public PropState State { get; set; }
    }

    /// <summary>The overlay half at its port: a token per staged node holding
    /// the one document that IS its identity.</summary>
    private sealed class FakeOverlays : IOverlayLifecycle
    {
        private readonly List<object> _overlays = new();

        public bool RefuseCreate { get; set; }
        public IReadOnlyList<object> Live => _overlays;
        public IReadOnlyList<object> Overlays => _overlays.ToList();

        public object? Create(OverlayNodeState state)
        {
            if (RefuseCreate)
                return null;
            var overlay = new FakeOverlay
            {
                // The service names an unnamed document; the port fake says so
                // too, because the history's entry descriptions read the name
                // back out of the created node.
                State = state.Name.Length == 0
                    ? state with { Name = KindName(state.Kind) }
                    : state,
            };
            _overlays.Add(overlay);
            return overlay;
        }

        public bool IsLive(object overlay) => ((FakeOverlay)overlay).IsValid;

        public void Destroy(object overlay)
        {
            _overlays.Remove(overlay);
            ((FakeOverlay)overlay).IsValid = false;
        }

        /// <summary>The node leaves without this seam's knowledge — a scene
        /// import, or the game taking its UI down.</summary>
        public void VanishWithoutNotice(object overlay) => Destroy(overlay);

        public OverlayNodeState Read(object overlay) =>
            ((FakeOverlay)overlay).State;

        public void Write(object overlay, Func<OverlayNodeState, OverlayNodeState> edit) =>
            ((FakeOverlay)overlay).State = edit(((FakeOverlay)overlay).State);

        private static string KindName(OverlayNodeKind kind) => kind switch
        {
            OverlayNodeKind.Balloon => "Balloon",
            OverlayNodeKind.Status => "Status",
            _ => "Dialog",
        };
    }

    private sealed class FakeOverlay
    {
        public bool IsValid { get; set; } = true;
        public OverlayNodeState State { get; set; } = new();
    }

    private sealed class FakeActors : IActorLifecycle
    {
        private readonly List<IActor> _actors = new();

        /// <summary>What each live actor currently IS, so a removal's capture
        /// and a restore's re-application are observable without a body.
        /// </summary>
        private readonly Dictionary<IActor, ActorState> _states =
            new(ReferenceEqualityComparer.Instance);

        private int _next;

        public IReadOnlyList<IActor> Live => _actors;
        public int SpawnCalls { get; private set; }
        public bool RefuseDestroy { get; set; }

        /// <summary>Every refusal the seam named rather than skipping.
        /// </summary>
        public List<string> Notes { get; } = new();

        public IActor? Spawn(string name)
        {
            SpawnCalls++;
            var actor = new ActorBase(
                new EntityId($"{name}-{_next++}"),
                name,
                (nint)(_next + 1),
                ActorKind.Player);
            _actors.Add(actor);
            _states[actor] = new ActorState(Transform.Identity, true, null);
            return actor;
        }

        /// <summary>What the user made of a live actor after it was spawned.
        /// </summary>
        public void Edit(IActor actor, ActorState state) => _states[actor] = state;

        public ActorState StateOf(IActor actor) => _states[actor];

        /// <summary>The actor leaves without this seam's knowledge — a scene
        /// import, or the game.</summary>
        public void DestroyActor(IActor actor) => Destroy(actor);

        public bool IsSpawned(object actor) => _actors.Contains((IActor)actor);

        public bool Destroy(object actor)
        {
            if (RefuseDestroy)
                return false;
            _states.Remove((IActor)actor);
            return _actors.Remove((IActor)actor);
        }

        public ActorState Read(object actor) => _states[(IActor)actor];

        public void Restore(object actor, ActorState state) =>
            _states[(IActor)actor] = state;

        public void Note(string detail) => Notes.Add(detail);
    }
}
