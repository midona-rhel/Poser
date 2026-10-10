using System.Numerics;
using Poser.Application.Scene;
using Poser.Application.Selection;
using Poser.Application.Transforms;
using Poser.Core;
using Poser.Domain;
using Poser.Domain.Companions;
using Poser.Domain.Identity;
using Poser.Domain.Presentation;
using Poser.Domain.Scene;
using Poser.Domain.Transforms;
using Poser.Domain.Posing;
using Poser.Application.Presentation;
using Poser.Game.Journal;
using Poser.Game.Lighting;
using Poser.Game.Presentation;
using Poser.Game.WorldObjects;
using Poser.Entities;
using Poser.Game.Scene;
using Poser.Services;

using Poser.Domain.Cameras;

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
    public void Prop_transform_history_rebinds_after_removal()
    {
        var world = new World();
        var prop = (FakeProp)world.Lifecycle.SpawnProp(Apple)!;
        TransformTargetId Current() => TransformTargetId.ForProp(((FakeProp)Assert.Single(world.Props.Live)).StableId);
        var oldTarget = TransformTargetId.ForProp(prop.StableId);
        var state = new TransformTargetState(oldTarget, PoseTransform.Identity, new BonePose(), false);
        world.History.Append(new TransformPatch("Move entity", [state], [state]));
        world.Lifecycle.DestroyProp(prop);
        world.History.Reconcile(_ => false);
        Assert.True(world.Undo());
        var newTarget = Current();
        Assert.NotEqual(oldTarget, newTarget);
        var patch = Assert.IsType<TransformPatch>(world.History.PeekUndo());
        Assert.Equal(newTarget, Assert.Single(patch.Before).Target);
        world.History.CommitUndo(patch);
        Assert.True(world.Undo());
        world.History.Reconcile(_ => false);
        Assert.True(world.Redo());
        var thirdTarget = Current();
        Assert.NotEqual(newTarget, thirdTarget);
        Assert.Equal(thirdTarget, Assert.Single(Assert.IsType<TransformPatch>(world.History.PeekRedo()).After).Target);
    }

    [Fact]
    public void Borrowed_light_edits_survive_release_and_repeated_restoration()
    {
        var world = new World();
        var light = Borrow(world);
        var id = LightId.New();
        var values = new EntityValues<LightId>(new ValueJournal(world.History), new HandleValuePort<LightId, ILight>(
            exact => exact == id && world.Lighting.Lights.Contains(light) ? light : null, SelectionId.ForLight,
            l => l.IsValid, world.Lifecycle, LightAccessors.Create(world.Lighting), "Gone"), "Gone");
        Assert.True(values.Set(id, LightProperties.Intensity, 7f).Success);
        Assert.True(values.Set(id, LightProperties.IsOn, false).Success);
        world.Lifecycle.DestroyLight(light);
        for (int i = 0; i < 3; i++)
        {
            Assert.True(world.Undo()); // release
            var restored = Assert.Single(world.Lighting.Lights);
            Assert.NotSame(light, restored);
            Assert.Equal(LightOwnership.World, restored.Ownership);
            Assert.Equal(7, restored.Intensity);
            Assert.False(restored.IsOn);
            Assert.True(world.Undo()); // off
            Assert.True(restored.IsOn);
            Assert.True(world.Undo()); // intensity
            Assert.Equal(1, restored.Intensity);
            Assert.True(world.Undo()); // acquire
            Assert.Empty(world.Lighting.Lights);
            Assert.True(world.Redo());
            Assert.True(world.Redo());
            Assert.True(world.Redo());
            restored = Assert.Single(world.Lighting.Lights);
            Assert.Equal(7, restored.Intensity);
            Assert.False(restored.IsOn);
            Assert.True(world.Redo());
            Assert.Empty(world.Lighting.Lights);
        }
    }

    [Fact]
    public void Released_light_cannot_reclaim_a_different_incarnation_at_the_same_address()
    {
        var world = new World();
        var light = Borrow(world);
        world.Lifecycle.DestroyLight(light);
        world.Lighting.Source = world.Lighting.Source with { Generation = 2 };
        Assert.False(world.Undo());
        Assert.Empty(world.Lighting.Lights);
        Assert.StartsWith("Release light", world.History.UndoDescription);
    }

    [Fact]
    public void Owned_actor_removal_restores_latest_state_without_its_creation_entry()
    {
        var world = new World();
        int spawns = 0;
        var actor = world.Lifecycle.SpawnActor("Add", () =>
        {
            spawns++;
            return world.Actors.Spawn("Imported");
        })!;
        world.History.Clear();
        actor.Name = "Authored actor";
        var authored = Posed(new Vector3(12, 3, -4), visible: false);
        world.Actors.Edit(actor, authored);

        Assert.True(world.Lifecycle.DespawnActor(actor));
        Assert.Empty(world.Actors.Live);
        Assert.Empty(world.Actors.Notes);
        for (int i = 0; i < 2; i++)
        {
            Assert.True(world.Undo());
            var restored = Assert.Single(world.Actors.Live);
            Assert.Equal("Authored actor", restored.Name);
            Assert.Equal(authored, world.Actors.StateOf(restored));
            Assert.Same(restored, ((IEntityHistoryResolver<IActor>)world.Lifecycle).Resolve(actor));
            Assert.True(world.Redo());
            Assert.Empty(world.Actors.Live);
        }
        Assert.Equal(1, spawns); // Restore recreates the body, never replays the clone source.
    }

    [Fact]
    public void Scene_replay_rebinds_later_actor_removal_to_its_new_instance()
    {
        var world = new World();
        var original = world.Actors.Spawn("Imported")!;
        Assert.True(world.Lifecycle.DespawnActor(original));
        Assert.True(world.Undo());
        var restored = Assert.Single(world.Actors.Live);
        world.Actors.DestroyActor(restored);
        var replayed = world.Actors.Spawn("Reloaded")!;
        var bindings = (IEntityHistoryBinding<IActor>)world.Lifecycle;
        bindings.BindReplacement(original, replayed);

        Assert.Same(replayed, bindings.Resolve(original));
        Assert.Same(replayed, bindings.Resolve(restored));
        Assert.True(world.Redo());
        Assert.Empty(world.Actors.Live);
        Assert.True(world.Undo());
        Assert.Equal("Reloaded", Assert.Single(world.Actors.Live).Name);
    }

    [Fact]
    public void Refusals_do_not_add_history_or_discard_the_previous_entry()
    {
        var world = new World
        {
            Lighting = { RefuseSpawn = true },
            Props = { RefuseSpawn = true },
        };

        Assert.Null(world.Lifecycle.SpawnLight(LightKind.Spot));
        Assert.Null(world.Lifecycle.SpawnProp(Apple));
        Assert.False(world.History.CanUndo);

        var actor = world.Lifecycle.SpawnActor("Add actor", () => world.Actors.Spawn("Lead"))!;
        Assert.Equal("Add actor", world.History.UndoDescription);
        world.Actors.RefuseDestroy = true;
        world.Lifecycle.DespawnActor(actor);
        Assert.Single(world.Actors.Live);
        Assert.Equal("Add actor", world.History.UndoDescription);

        // A refused borrowed-light release keeps both the claim and its history.
        var light = Borrow(world);
        var acquisition = world.History.PeekUndo();
        world.Lighting.RefuseDestroy = true;
        world.Lifecycle.DestroyLight(light);
        Assert.Same(light, Assert.Single(world.Lighting.Lights));
        Assert.Same(acquisition, world.History.PeekUndo());
    }

    [Fact]
    public void Refused_actor_removal_does_not_lose_successful_sibling_inverses()
    {
        var world = new World();
        var actor = world.Lifecycle.SpawnActor("Add actor", () => world.Actors.Spawn("Lead"))!;
        var prop = world.Lifecycle.SpawnProp(Apple)!;
        var light = world.Lifecycle.SpawnLight(LightKind.Spot)!;
        world.Actors.RefuseDestroy = true;
        Assert.Equal(2, world.Lifecycle.DestroySelection(
            actors: [actor], props: [prop], lights: [light]));
        Assert.Same(actor, Assert.Single(world.Actors.Live));
        Assert.Empty(world.Props.Live);
        Assert.Empty(world.Lighting.Live);
        Assert.True(world.Undo());
        Assert.Same(actor, Assert.Single(world.Actors.Live));
        Assert.Single(world.Props.Live);
        Assert.Single(world.Lighting.Live);
        Assert.NotEqual("Remove selection", world.History.UndoDescription);
        Assert.True(world.Redo());
        Assert.Same(actor, Assert.Single(world.Actors.Live));
        Assert.Empty(world.Props.Live);
        Assert.Empty(world.Lighting.Live);
    }

    [Fact]
    public void Released_world_object_undo_reclaims_authored_state_and_adoption_undo_restores_the_map()
    {
        var world = new World();
        var address = world.WorldObjects.Place(0x1000, MapStood);
        var claim = world.Lifecycle.AdoptWorldObject(address)!;
        world.WorldObjects.Apply(claim, world.WorldObjects.Read(claim) with
        {
            Placement = UserPut,
            Visible = false,
        });

        Assert.True(world.Lifecycle.ReleaseWorldObject(claim));
        Assert.Empty(world.WorldObjects.Live);
        Assert.Equal(MapStood, world.WorldObjects.MapPlacement(address));
        Assert.True(world.Undo());
        var restored = Assert.Single(world.WorldObjects.Live);
        Assert.Equal(UserPut, world.WorldObjects.Read(restored).Placement);
        Assert.False(world.WorldObjects.Read(restored).Visible);

        Assert.True(world.Undo());
        Assert.Empty(world.WorldObjects.Live);
        Assert.Equal(MapStood, world.WorldObjects.MapPlacement(address));
        Assert.True(world.Redo());
        restored = Assert.Single(world.WorldObjects.Live);
        Assert.Equal(UserPut, world.WorldObjects.Read(restored).Placement);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Released_scenery_is_invalidated_when_its_address_is_reused_or_gone(bool reused)
    {
        var world = new World();
        world.History.Append(new JournalStep("Earlier unrelated edit", () => true, () => true));
        var address = world.WorldObjects.Place(0x3000, MapStood);
        var claim = world.Lifecycle.AdoptWorldObject(address)!;
        var target = world.WorldObjects.Target(claim);
        var state = new TransformTargetState(target, PoseTransform.Identity, new BonePose(), false);
        world.History.Append(new TransformPatch("Move borrowed BG", [state], [state]));
        Assert.True(world.Lifecycle.ReleaseWorldObject(claim));
        if (reused) world.WorldObjects.Place(address, UserPut); // Same address, new incarnation.
        else world.WorldObjects.Remove(address);

        Assert.Equal("Remove world object", world.History.UndoDescription);
        Assert.False(world.Undo()); // Fails safely and drops the permanently un-restorable claim.
        Assert.Empty(world.WorldObjects.Live);
        if (reused) Assert.Equal(UserPut, world.WorldObjects.MapPlacement(address));
        Assert.Equal("Earlier unrelated edit", world.History.UndoDescription);
        Assert.True(world.Undo());
        Assert.False(world.History.CanUndo);
        Assert.Equal(1, world.WorldObjects.AdoptCalls);
    }

    /// <summary>Borrows the fake's world light the way WorldService does:
    /// the lighting service captures it and the lifecycle records the claim.</summary>
    private static ILight Borrow(World world) =>
        world.Lifecycle.RecordSpawnedLight("Acquire world light",
            world.Lighting.CaptureWorldLight(world.Lighting.Source))!;

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
        public List<string> Notices { get; } = new();

        /// <param name="capacity">Undo depth; below 1 is undo switched off.
        /// </param>
        public World(int capacity = TransformHistory.DefaultCapacity)
        {
            History = new TransformHistory(() => capacity);
            Lifecycle = new SceneLifecycleHistory(
                History, Lighting, Cameras, Actors, Props, Overlays,
                WorldObjects, Lighting.Target,
                worldObject => worldObject is FakeWorldObject fake
                    ? TransformTargetId.ForWorldObject(fake.Id) : null,
                prop => prop is FakeProp { IsValid: true } fake ? TransformTargetId.ForProp(fake.StableId) : null,
                overlay => overlay is FakeOverlay { IsValid: true, Kind: OverlayNodeKind.Collider } fake
                    ? TransformTargetId.ForCollider(fake.StableId) : null);
        }

        public bool Undo()
        {
            var entry = History.PeekUndo()!;
            if (!(entry is InverseEntry inverse && inverse.Undo()))
            {
                if (entry is SceneLifecyclePatch patch && RefusalPolicy.Decide(patch) == RefusalAction.DropNow)
                {
                    History.Drop(entry);
                    Notices.Add(patch.FailureDetail?.Invoke() ?? "Lifecycle restore refused.");
                }
                return false;
            }
            History.CommitUndo(entry);
            return true;
        }

        public bool Redo()
        {
            var entry = History.PeekRedo()!;
            if (!(entry is InverseEntry inverse && inverse.Redo()))
            {
                if (entry is SceneLifecyclePatch patch && RefusalPolicy.Decide(patch) == RefusalAction.DropNow)
                {
                    History.Drop(entry);
                    Notices.Add(patch.FailureDetail?.Invoke() ?? "Lifecycle restore refused.");
                }
                return false;
            }
            History.CommitRedo(entry);
            return true;
        }
    }

    private sealed class FakeLighting : ILightingService
    {
        private readonly List<ILight> _lights = new();
        private readonly Dictionary<ILight, LightId> _ids = new();
        private readonly Dictionary<ILight, WorldLightCandidate> _sources = new();
        public WorldLightCandidate Source = new(0x1234, 0, Generation: 1);
        public TransformTargetId? Target(ILight light)
        {
            if (!light.IsValid || !_lights.Contains(light)) return null;
            if (!_ids.TryGetValue(light, out var id)) _ids[light] = id = LightId.New();
            return TransformTargetId.ForLight(id);
        }

        public bool RefuseSpawn { get; set; }
        public IReadOnlyList<ILight> Live => _lights;
        public bool RefuseDestroy { get; set; }

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
            if (RefuseDestroy) return;
            _lights.Remove(light);
            ((FakeLight)light).IsValid = false;
        }

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
        public ILight? CaptureWorldLight(WorldLightCandidate candidate)
        {
            if (candidate != Source || _lights.Any(l => l.Ownership == LightOwnership.World)) return null;
            var light = AddBorrowed();
            _sources[light] = candidate;
            return light;
        }
        public WorldLightCandidate? GetWorldSource(ILight light) =>
            _sources.TryGetValue(light, out var source) ? source : null;
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
        public bool SuppressFlightKeys { get; set; }
        public bool FlightActive => false;
        private readonly List<IVirtualCamera> _cameras = new();

        public IReadOnlyList<IVirtualCamera> Live => _cameras;

        public bool IsAvailable => true;
        public IReadOnlyList<IVirtualCamera> Cameras => _cameras;
        public IVirtualCamera? LiveCamera { get; private set; }

        public FreeCameraSpeedNotice? SpeedNotice => null;
        public void Dispose() { }

        public IVirtualCamera? CreateCamera(CameraKind kind, bool makeLive = true)
        {
            var camera = new FakeCamera(kind);
            _cameras.Add(camera);
            if (makeLive) SetLive(camera);
            return camera;
        }

        public IVirtualCamera? CloneCamera(IVirtualCamera source) =>
            CreateCamera(source.Kind);

        public void DestroyCamera(IVirtualCamera camera)
        {
            _cameras.Remove(camera);
            if (ReferenceEquals(LiveCamera, camera))
            {
                ((FakeCamera)camera).IsLive = false;
                LiveCamera = null;
                if (_cameras.FirstOrDefault(candidate => candidate.IsDefault) is { } fallback)
                    SetLive(fallback);
            }
            ((FakeCamera)camera).IsValid = false;
        }

        public void DestroyAllCameras() => _cameras.Clear();
        public void SetLive(IVirtualCamera camera)
        {
            if (LiveCamera is FakeCamera previous) previous.IsLive = false;
            LiveCamera = camera;
            ((FakeCamera)camera).IsLive = true;
        }
        public bool SetTargetActor(
            IVirtualCamera camera, IActor actor, ActorId actorId,
            string displayName) => false;
        public void ClearTargetActor(IVirtualCamera camera) { }
        // Camera framing is outside lifecycle-history coverage.
        public Outcome CenterOnActor(IActor actor) =>
            Outcome.Fail("not available in lifecycle fake");
        public Outcome CenterOnBone(IBone bone) =>
            Outcome.Fail("not available in lifecycle fake");
    }

    private sealed class FakeCamera(CameraKind kind) : IVirtualCamera
    {
        public float DefaultFoV { get; private set; }
        public float DefaultRoll { get; private set; }
        public System.Numerics.Vector3 DefaultRotation { get; private set; }
        public void CaptureOwnedDefaults()
        {
            DefaultFoV = FoV;
            DefaultRoll = Roll;
            DefaultRotation = Rotation;
        }

        public bool IsValid { get; set; } = true;
        public string Name { get; set; } = "Camera";
        public CameraKind Kind { get; } = kind;
        public bool IsLive { get; set; }
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
        public IActor? TargetActor { get; set; }
        public ActorId? TargetActorId { get; set; }
        public bool IsTargetLocked { get; set; }
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
        private readonly Dictionary<nint, WorldObjectIncarnation> _identities = new();
        private readonly List<object> _adopted = new();
        private long _nextGeneration;
        public int AdoptCalls { get; private set; }

        public IReadOnlyList<object> Live => _adopted;
        public IReadOnlyList<object> WorldObjects => _adopted.ToList();

        /// <summary>Where the map stands one address, which is what every
        /// release has to put back.</summary>
        public Transform MapPlacement(nint address) => _map[address];

        public nint Place(nint address, Transform placement)
        {
            _map[address] = placement;
            _identities[address] = new WorldObjectIncarnation(
                address, ++_nextGeneration, nint.Zero, false);
            return address;
        }

        public void Remove(nint address)
        {
            _map.Remove(address);
            _identities.Remove(address);
        }

        public TransformTargetId Target(object worldObject) =>
            TransformTargetId.ForWorldObject(((FakeWorldObject)worldObject).Id);

        public object? Adopt(nint address)
        {
            AdoptCalls++;
            if (!_map.ContainsKey(address))
                return null;
            var claim = new FakeWorldObject
            {
                Owner = this,
                State = new WorldObjectState(
                    address, "bg/fake.mdl", false, _map[address], true)
                {
                    Identity = _identities[address],
                },
                MapPlacement = _map[address],
            };
            _adopted.Add(claim);
            return claim;
        }

        public bool CanReclaim(WorldObjectState state, out string detail)
        {
            if (!_map.ContainsKey(state.Address))
            {
                detail = "Unable to undo: the original world object is no longer in the current world graph.";
                return false;
            }
            var current = _identities[state.Address];
            bool matches = state.Identity.IsVfx
                ? current == state.Identity
                : current.SameAllocation(state.Identity);
            detail = matches
                ? string.Empty
                : "Unable to undo: the world object at this address no longer matches the captured identity.";
            return matches;
        }

        public object? Reclaim(WorldObjectState state, out string detail)
        {
            if (!CanReclaim(state, out detail))
                return null;
            return Adopt(state.Address);
        }

        public object? Spawn(string path, Transform placement, bool visible)
        {
            var claim = new FakeWorldObject
            {
                Owner = this,
                State = new WorldObjectState(
                    nint.Zero, path, true, placement, visible),
                MapPlacement = placement,
            };
            _adopted.Add(claim);
            return claim;
        }

        public bool IsLive(object worldObject) =>
            ((FakeWorldObject)worldObject).IsValid;

        public bool Release(object worldObject)
        {
            var claim = (FakeWorldObject)worldObject;
            _adopted.Remove(claim);
            if (claim.IsValid)
                _map[claim.State.Address] = claim.MapPlacement;
            claim.IsValid = false;
            return true;
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
        public WorldObjectId Id { get; } = WorldObjectId.New();
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

        public PropState Read(object prop) => ((FakeProp)prop).State;

        public void Apply(object prop, PropState state) =>
            ((FakeProp)prop).State = ((FakeProp)prop).State with
            {
                Name = state.Name,
                Transform = state.Transform,
                Visible = state.Visible,
            };
    }

    private sealed class FakeProp : IPropHandle
    {
        public PropId StableId { get; } = new(Guid.NewGuid(), 1);
        public bool IsValid { get; set; } = true;
        public PropState State { get; set; }
        public int Id => 0;
        public nint Address => 0;
        public string Name { get => State.Name; set => State = State with { Name = value }; }
        public PropModel Model => State.Model;
        public bool Visible { get => State.Visible; set => State = State with { Visible = value }; }
        public Transform Transform { get => State.Transform; set => State = State with { Transform = value }; }
        public Vector3 Position { get => Transform.Position; set { var t = Transform; t.Position = value; Transform = t; } }
        public Quaternion Rotation { get => Transform.Rotation; set { var t = Transform; t.Rotation = value; Transform = t; } }
        public Vector3 Scale { get => Transform.Scale; set { var t = Transform; t.Scale = value; Transform = t; } }
        public bool Respawn(PropModel model, out string? detail)
        {
            State = State with { Model = model };
            detail = null;
            return IsValid;
        }
        public void Destroy() => IsValid = false;
    }

    private sealed class FakeOverlays : IOverlayLifecycle
    {
        private readonly List<object> _overlays = new();

        public IReadOnlyList<object> Live => _overlays;
        public IReadOnlyList<object> Overlays => _overlays.ToList();

        public object? Create(OverlayNodeState state)
        {
            var overlay = new FakeOverlay { State = state };
            _overlays.Add(overlay);
            return overlay;
        }

        public bool IsLive(object overlay) => ((FakeOverlay)overlay).IsValid;

        public void Destroy(object overlay)
        {
            _overlays.Remove(overlay);
            ((FakeOverlay)overlay).IsValid = false;
        }

        public OverlayNodeState Read(object overlay) =>
            ((FakeOverlay)overlay).State;

        public void Write(object overlay, Func<OverlayNodeState, OverlayNodeState> edit) =>
            ((FakeOverlay)overlay).State = edit(((FakeOverlay)overlay).State);
    }

    private sealed class FakeOverlay : IOverlayNode
    {
        public OverlayId StableId { get; } = new(Guid.NewGuid(), 1);
        public bool IsValid { get; set; } = true;
        public OverlayNodeState State { get; set; } = new();
        public int Id => 0;
        public OverlayNodeKind Kind => State.Kind;
        public string Name { get => State.Name; set => State = State with { Name = value }; }
        public Vector2 Position { get => State.Position; set => State = State with { Position = value }; }
        public float Scale { get => State.Scale; set => State = State with { Scale = value }; }
        public float Alpha { get => State.Alpha; set => State = State with { Alpha = value }; }
        public bool Visible { get => State.Visible; set => State = State with { Visible = value }; }
        public bool Draggable { get => State.Draggable; set => State = State with { Draggable = value }; }
        public string Text { get => State.Text; set => State = State with { Text = value }; }
        public string Speaker { get => State.Speaker; set => State = State with { Speaker = value }; }
        public uint FontSize { get => State.FontSize; set => State = State with { FontSize = value }; }
        public TalkBackground TalkBackground { get => State.TalkBackground; set => State = State with { TalkBackground = value }; }
        public TalkCursor TalkCursor { get => State.TalkCursor; set => State = State with { TalkCursor = value }; }
        public BalloonChannel BalloonChannel { get => State.BalloonChannel; set => State = State with { BalloonChannel = value }; }
        public BalloonGradient BalloonGradient { get => State.BalloonGradient; set => State = State with { BalloonGradient = value }; }
        public bool ArrowVisible { get => State.ArrowVisible; set => State = State with { ArrowVisible = value }; }
        public float ArrowX { get => State.ArrowX; set => State = State with { ArrowX = value }; }
        public StatusKind StatusKind { get => State.StatusKind; set => State = State with { StatusKind = value }; }
        public uint StatusIconId { get => State.StatusIconId; set => State = State with { StatusIconId = value }; }
        public void Destroy() => IsValid = false;
    }

    private sealed class FakeActors : IActorLifecycle
    {
        public void DetachGaze(object actor) { }
        public string GetName(object actor) => ((IActor)actor).Name;
        public void SetName(object actor, string name) => ((IActor)actor).Name = name;
        public void NameCreated(object actor, string seed) => SetName(actor,
            EntityNames.Next(seed, _actors.Where(x => !ReferenceEquals(x, actor)).Select(x => x.Name)));

        private readonly List<IActor> _actors = new();

        /// <summary>What each live actor currently IS, so a removal's capture
        /// and a restore's re-application are observable without a body.
        /// </summary>
        private readonly Dictionary<IActor, ActorState> _states =
            new(ReferenceEqualityComparer.Instance);

        private int _next;

        public IReadOnlyList<IActor> Live => _actors;
        public bool RefuseDestroy { get; set; }

        /// <summary>Every refusal the seam named rather than skipping.
        /// </summary>
        public List<string> Notes { get; } = new();

        public IActor? Spawn(string name)
        {
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

        public void WhenPosable(object actor, Action<object> act) => act(actor);

        public ActorState Read(object actor) => _states[(IActor)actor];

        public IActor? Recreate(ActorState state) => Spawn("Fresh body");

        public void Restore(object actor, ActorState state, Func<bool>? stillCurrent = null)
        {
            if (stillCurrent?.Invoke() != false)
                _states[(IActor)actor] = state;
        }

        public void Note(string detail) => Notes.Add(detail);
    }
}
