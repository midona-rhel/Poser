using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Reflection;
using Dalamud.Plugin.Services;
using Poser.Core;
using Poser.Application.Transforms;
using Poser.Domain.Identity;
using Poser.Domain.Posing;
using Poser.Domain.Transforms;
using Poser.Application.Presentation;
using Poser.Game.Presentation;
using Poser.Game.Scene;
using Poser.Game.WorldObjects;
using Poser.Services;

using Poser.Domain.Scene;

namespace Poser.Game.Tests.WorldObjects;

/// <summary>
/// THE RESTORE CONTRACT, proven edge by edge. An adopted world object belongs
/// to the map, not to Poser: it is never created, never destroyed, and must
/// never be left displaced. Every path that can end a claim writes the captured
/// placement and flags back — an explicit release, a scene clear, GPose exit,
/// and plugin unload — each idempotent, and any pair of them safe in either
/// order. A claim whose address has stopped being a BG object is dropped
/// without a write, because restoring onto whatever took its place is the one
/// way this contract could do harm.
/// </summary>
public sealed class WorldObjectRestoreTests
{
    [Fact]
    public void Edits_replay_after_acquisition_and_release_restore_new_wrappers()
    {
        var world = new World();
        var history = new TransformHistory();
        var ids = new Dictionary<AdoptedWorldObject, TransformTargetId>();
        TransformTargetId? Target(object instance)
        {
            var handle = (AdoptedWorldObject)instance;
            if (!handle.IsValid) return null;
            if (!ids.TryGetValue(handle, out var id))
                ids[handle] = id = TransformTargetId.ForWorldObject(WorldObjectId.New());
            return id;
        }
        // Exercise the real service, lifecycle owner and value journal. Scene
        // refresh occurs synchronously during native release/adoption.
        var lifecycle = new SceneLifecycleHistory(history, null!, null!, null!, null!, null!,
            new WorldObjectServiceLifecycle(world.Service), worldObjectTarget: Target);
        var objectId = WorldObjectId.New();
        AdoptedWorldObject? bound = null;
        var values = new EntityValues<WorldObjectId>(new ValueJournal(history), new HandleValuePort<WorldObjectId, IWorldObject>(
            exact => exact == objectId ? bound : null, SelectionId.ForWorldObject, o => o.IsValid, lifecycle,
            SceneObjectAccessors.WorldObjects(), "Gone"), "Gone");
        world.Events.Subscribe<WorldObjectListChangedEvent>(_ =>
            history.Reconcile(id => ids.Any(pair => pair.Value == id && pair.Key.IsValid)));
        var original = (AdoptedWorldObject)lifecycle.AdoptWorldObject(world.Port.Add("bg/tree.mdl", Placed))!;
        var target = Target(original)!.Value;
        original.Transform = Moved;
        TransformTargetState State(Transform value) =>
            new(target, new PoseTransform(value.Position, value.Rotation, value.Scale), new BonePose(), true);
        history.Append(new TransformPatch("Move object", [State(Placed)], [State(Moved)]));
        bound = original;
        Assert.True(values.Set(objectId, WorldObjectProperties.Visible, false).Success);

        void Step(bool undo)
        {
            var entry = (undo ? history.PeekUndo() : history.PeekRedo())!;
            Assert.NotNull(entry);
            switch (entry)
            {
                case SceneLifecyclePatch patch:
                    Assert.True(undo ? patch.Undo() : patch.Redo());
                    break;
                case JournalStep patch:
                    Assert.True(undo ? patch.Undo() : patch.Redo());
                    break;
                case TransformPatch patch:
                    foreach (var state in undo ? patch.Before : patch.After)
                    {
                        var current = Assert.Single(ids, pair => pair.Value == state.Target && pair.Key.IsValid).Key;
                        current.Transform = Transform.FromPose(state.Transform);
                    }
                    break;
                default:
                    throw new InvalidOperationException("Unexpected history entry.");
            }
            if (undo) history.CommitUndo(entry);
            else history.CommitRedo(entry);
        }

        for (int cycle = 0; cycle < 2; cycle++)
        {
            Step(true);
            Assert.True(Assert.Single(world.Service.Adopted).Visible);
            Step(true);
            Assert.Equal(Placed, Assert.Single(world.Service.Adopted).Transform);
            Step(true);
            Assert.Empty(world.Service.Adopted);
            Step(false);
            Assert.NotSame(original, Assert.Single(world.Service.Adopted));
            Step(false);
            Assert.Equal(Moved, Assert.Single(world.Service.Adopted).Transform);
            Step(false);
            Assert.False(Assert.Single(world.Service.Adopted).Visible);
        }

        Assert.True(lifecycle.ReleaseWorldObject(Assert.Single(world.Service.Adopted)));
        Step(true);
        Assert.Equal(Moved, Assert.Single(world.Service.Adopted).Transform);
        Assert.False(Assert.Single(world.Service.Adopted).Visible);
        Step(true);
        Assert.True(Assert.Single(world.Service.Adopted).Visible);
        Step(true);
        Assert.Equal(Placed, Assert.Single(world.Service.Adopted).Transform);
        Step(false);
        Step(false);
        Step(false);
        Assert.Empty(world.Service.Adopted);
        Step(true);
        Assert.Equal(Moved, Assert.Single(world.Service.Adopted).Transform);
        Assert.False(Assert.Single(world.Service.Adopted).Visible);
    }

    [Fact]
    public void Furniture_lifecycle_restores_stain_tint_lights_placement_and_opacity()
    {
        var world = new World();
        var lifecycle = new WorldObjectServiceLifecycle(world.Service);
        var furniture = world.Service.Spawn(FurnitureCatalog.PathFor(1, true), Placed, true, out _)!;
        Assert.True(furniture.IsFurniture);
        furniture.Transform = Moved;
        furniture.Stain = 42;
        furniture.NightState = true;
        furniture.Tint = new Vector3(.1f, .2f, .3f);
        furniture.Visible = false;
        furniture.Opacity = .4f;
        FurnitureLightState[] lights = [new("/0", false), new("/1/0", true)];
        furniture.FurnitureLights = lights;
        Assert.Equal(.4f, world.Port.OpacityOf(furniture.Address));
        var state = lifecycle.Read(furniture);
        for (int i = 0; i < 2; i++)
        {
            lifecycle.Release(furniture);
            furniture = (AdoptedWorldObject)lifecycle.Spawn(state.Path, state.Placement, state.Visible)!;
            lifecycle.Apply(furniture, state);
            var read = lifecycle.Read(furniture);
            Assert.Equal(lights, read.FurnitureLights);
            Assert.Equal(state with { Address = furniture.Address, FurnitureLights = read.FurnitureLights }, read);
            Assert.Equal((byte)42, world.Port.LastFurnitureStain);
            Assert.True(furniture.NightState);
            Assert.True(world.Port.LastNightState);
            Assert.Equal(state.Tint, world.Port.LastBgTint);
        }
    }

    private static readonly Transform Placed = new(
        new Vector3(10f, 2f, -4f),
        Quaternion.CreateFromYawPitchRoll(0.5f, 0f, 0f),
        new Vector3(1f, 1f, 1f));

    private static readonly Transform Moved = new(
        new Vector3(99f, 50f, 12f),
        Quaternion.CreateFromYawPitchRoll(1.5f, 0.25f, 0f),
        new Vector3(2f, 2f, 2f));

    [Fact]
    public void Adoption_captures_without_writing_and_duplicate_adoption_reuses_the_claim()
    {
        var world = new World();
        var address = world.Port.Add("bg/tree.mdl", Placed, flags: 0x21, visible: true);
        var first = world.Service.Adopt(address);
        var second = world.Service.Adopt(address);

        Assert.Same(first, second);
        Assert.Equal(Placed, first!.InitialPlacement);
        Assert.Equal((byte)0x21, first.InitialFlags);

        var dead = world.Port.Add("bg/tree.mdl", Placed);
        world.Port.Kill(dead);
        Assert.Null(world.Service.Adopt(dead));
        Assert.Equal(0, world.Port.Writes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Every_end_path_restores_map_state_once_without_destroying_the_object(bool gposeExit)
    {
        var world = new World();
        var address = world.Port.Add("bg/tree.mdl", Placed, flags: 0x21, visible: true);
        world.Port.WriteOpacity(address, .65f);
        var adopted = world.Service.Adopt(address)!;
        adopted.Transform = Moved;
        adopted.Visible = false;
        adopted.Opacity = .276f;

        if (gposeExit) world.Events.Publish(new GPoseStateChangedEvent(false));
        else Assert.True(world.Service.Release(adopted));
        Assert.Equal(Placed, world.Port.PlacementOf(address));
        Assert.Equal((byte)0x21, world.Port.FlagsOf(address));
        Assert.True(world.Port.VisibleOf(address));
        Assert.True(world.Port.TryReadOpacity(address, out var opacity));
        Assert.Equal(.65f, opacity);
        Assert.True(world.Port.IsAlive(address));
        Assert.False(adopted.IsValid);
        Assert.False(world.Service.Release(adopted));
        world.Service.Dispose();
        Assert.Equal(0, world.Service.Count);
    }

    [Fact]
    public void Vfx_release_restores_the_exact_original_playback_state_and_retries_a_failed_restore()
    {
        var world = new World();
        var address = world.Port.Add("vfx/fire.avfx", Placed, isVfx: true);
        world.Port.SetVfxPlayback(address, VfxPlaybackState.Inactive);
        world.Port.SetVfxColor(address, new Vector4(1f, 1f, 1f, 0.35f));
        var effect = world.Service.Adopt(address)!;
        effect.VfxPaused = true;
        effect.Transform = Moved;

        world.Port.NoOpRestore = true;
        Assert.False(world.Service.Release(effect));
        Assert.True(effect.IsValid);
        world.Port.NoOpRestore = false;
        Assert.True(world.Service.Release(effect));
        Assert.Equal(VfxPlaybackState.Inactive, world.Port.PlaybackOf(address));
        Assert.Equal(0.35f, world.Port.ColorOf(address).W);
        Assert.Equal(Placed, world.Port.PlacementOf(address));
    }

    [Fact]
    public void Reused_address_generation_refuses_stale_write_and_teardown()
    {
        var world = new World();
        var address = world.Port.Add("vfx/fire.avfx", Placed, isVfx: true);
        var adopted = world.Service.Adopt(address)!;
        world.Port.Replace(address, "vfx/new-fire.avfx", Moved, isVfx: true);

        adopted.Transform = Placed;
        Assert.Equal(Moved, world.Port.PlacementOf(address));
        Assert.True(world.Service.Release(adopted));
        Assert.Equal(Moved, world.Port.PlacementOf(address));
    }

    [Fact]
    public async System.Threading.Tasks.Task Respawn_waits_for_model_and_stain_before_committing_latest_settings()
    {
        var world = new World();
        var spawned = world.Service.Spawn("bg/old.mdl", Placed, true, out _)!;
        var original = spawned.Address;
        world.Port.BgReady = false;
        var operation = spawned.Respawn("bg/new.mdl");
        var fresh = world.Port.LastSpawned;
        Assert.False(operation.IsCompleted);
        Assert.Equal(original, spawned.Address);
        Assert.False(world.Port.VisibleOf(fresh));
        Assert.False((await spawned.Respawn("bg/third.mdl")).Succeeded);

        spawned.Transform = Moved;
        spawned.Tint = new System.Numerics.Vector3(.2f, .4f, .6f);
        spawned.NightState = true;
        world.Port.BgReady = true;
        world.Port.FailBgTint = true;
        world.Service.PumpRespawns(DateTime.UtcNow);
        Assert.False(operation.IsCompleted);
        Assert.DoesNotContain(original, world.Port.Destroyed);

        world.Port.FailBgTint = false;
        world.Service.PumpRespawns(DateTime.UtcNow);
        Assert.True((await operation).Succeeded);
        Assert.Equal(fresh, spawned.Address);
        Assert.Equal(Moved, spawned.Transform);
        Assert.True(world.Port.VisibleOf(fresh));
        Assert.Equal(spawned.Tint, world.Port.LastBgTint);
        Assert.True(world.Port.LastNightState);
        Assert.Contains(original, world.Port.Destroyed);
        world.Service.PumpRespawns(DateTime.UtcNow);
        Assert.Equal(new[] { fresh }, world.Port.LiveAddresses);
    }

    [Fact]
    public async System.Threading.Tasks.Task Respawn_loading_timeout_rolls_back_and_retains_failed_cleanup()
    {
        var world = new World();
        var spawned = world.Service.Spawn("bg/old.mdl", Placed, true, out _)!;
        var original = spawned.Address;
        world.Port.BgReady = false;
        var operation = spawned.Respawn("bg/new.mdl");
        world.Port.FailFreshDestroy = true;
        world.Service.PumpRespawns(DateTime.UtcNow.AddSeconds(16));
        Assert.False((await operation).Succeeded);
        Assert.Equal(original, spawned.Address);
        world.Port.FailFreshDestroy = false;
        world.Service.ReleaseAll();
        Assert.Empty(world.Port.LiveAddresses);
    }

    [Fact]
    public async System.Threading.Tasks.Task Respawn_pending_address_reuse_does_not_touch_replacement()
    {
        var world = new World();
        var spawned = world.Service.Spawn("bg/old.mdl", Placed, true, out _)!;
        var original = spawned.Address;
        world.Port.BgReady = false;
        var operation = spawned.Respawn("bg/new.mdl");
        var fresh = world.Port.LastSpawned;
        world.Port.Replace(fresh, "bg/unrelated.mdl", Moved);
        world.Port.BgReady = true;
        world.Service.PumpRespawns(DateTime.UtcNow);
        Assert.False((await operation).Succeeded);
        Assert.Equal(original, spawned.Address);
        Assert.Equal(Moved, world.Port.PlacementOf(fresh));
        Assert.DoesNotContain(fresh, world.Port.Destroyed);
    }

    [Fact]
    public void Scenery_does_not_reinterpret_its_own_output_as_native_motion()
    {
        var world = new World();
        var address = world.Port.Add("bg/animated.mdl", Transform.Identity);
        var obj = world.Service.Adopt(address)!;
        var placement = new Transform(new Vector3(10, 20, 30),
            Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2), Vector3.One);
        obj.Transform = placement;
        world.Service.HoldPausedAnimations();
        world.Port.Write(address, new Transform(Vector3.UnitX, Quaternion.Identity, Vector3.One));
        world.Service.HoldPausedAnimations();
        world.Port.Write(address, new Transform(Vector3.UnitX * 2, Quaternion.Identity, Vector3.One));
        world.Service.HoldPausedAnimations();
        var solved = world.Port.PlacementOf(address);

        // Multiple camera/render callbacks need not contain a native animation update.
        for (int i = 0; i < 4; i++)
        {
            world.Service.HoldPausedAnimations();
            Assert.Equal(solved, world.Port.PlacementOf(address));
        }

        obj.AnimationPaused = true;
        obj.AnimationPaused = false;
        world.Service.HoldPausedAnimations(); // The native fields still contain our held placement.
        world.Port.Write(address, new Transform(Vector3.UnitX * 5, Quaternion.Identity, Vector3.One));
        world.Service.HoldPausedAnimations();
        Assert.Equal(solved, world.Port.PlacementOf(address));
        world.Port.Write(address, new Transform(Vector3.UnitX * 6, Quaternion.Identity, Vector3.One));
        world.Service.HoldPausedAnimations();
        Assert.True(Vector3.Distance(solved.Position + Vector3.UnitY,
            world.Port.PlacementOf(address).Position) < 0.0001f);
        world.Service.Dispose();
    }

    private sealed class World
    {
        public FakePort Port { get; } = new();
        public FakeEventBus Events { get; } = new();
        public WorldObjectService Service { get; }

        public World() =>
            Service = new WorldObjectService(
                Port,
                Events,
                DispatchProxy.Create<IPluginLog, SilentLog>());
    }

    /// <summary>
    /// The world's graph, faithfully: a flat set of addressable BG objects with
    /// a placement, a flags byte and a drawn state; reads and writes that are
    /// inert for an address the world no longer holds; and a kill that models
    /// the one thing the real game can do behind Poser's back — stop an address
    /// being a BG object.
    /// </summary>
    private sealed class FakePort : IWorldObjectPort
    {
        private readonly Dictionary<nint, Node> _nodes = new();
        private readonly Dictionary<nint, long> _generations = new();
        private nint _next = 0x1000;

        public int Writes { get; private set; }
        public bool BgReady { get; set; } = true;
        public bool FailBgTint { get; set; }
        public System.Numerics.Vector3? LastBgTint { get; private set; }
        public byte LastFurnitureStain { get; private set; }
        private readonly Dictionary<nint, FurnitureLightState[]> _furnitureLights = new();
        public IReadOnlyList<FurnitureLightState> ReadFurnitureLights(nint address) =>
            _furnitureLights.TryGetValue(address, out var lights) ? lights : [];
        public void WriteFurnitureLights(nint address, IReadOnlyList<FurnitureLightState> lights) =>
            _furnitureLights[address] = System.Linq.Enumerable.ToArray(lights);
        public float OpacityOf(nint address) => _nodes[address].Opacity;
        public bool WriteFurnitureColor(nint address, byte stain, Vector3? tint)
        {
            LastFurnitureStain = stain;
            LastBgTint = tint;
            return BgReady;
        }
        public bool LastNightState { get; private set; }
        public bool NoOpRestore { get; set; }
        public bool FailFreshDestroy { get; set; }
        public readonly List<nint> Destroyed = new();
        public IReadOnlyCollection<nint> LiveAddresses => _nodes.Keys;
        public nint LastSpawned { get; private set; }

        public bool IsAvailable => true;

        public bool TryReadIncarnation(
            nint address, out WorldObjectIncarnation incarnation)
        {
            if (!_nodes.ContainsKey(address))
            {
                incarnation = default;
                return false;
            }
            if (!_generations.TryGetValue(address, out var generation))
                _generations[address] = generation = address.ToInt64();
            incarnation = new WorldObjectIncarnation(
                address, generation, _nodes[address].ResourceIdentity,
                _nodes[address].IsVfx);
            return true;
        }

        public void SetVfxSpeed(nint address, float speed)
        {
            if (_nodes.TryGetValue(address, out var node))
                node.Speed = speed;
        }

        public bool TrySetVfxSpeed(nint address, float speed)
        {
            SetVfxSpeed(address, speed);
            return true;
        }

        public void WriteVfxTint(
            nint address, System.Numerics.Vector3 tint) { }
        public bool WriteBgTint(nint address, System.Numerics.Vector3? tint)
        {
            if (FailBgTint) return false;
            LastBgTint = tint;
            return true;
        }
        public bool IsBgReady(nint address) => BgReady;
        public bool? CanDyeBg(nint address) => BgReady ? true : null;
        public bool? ReadBgNightState(nint address) => null;
        public bool WriteBgAnimationSpeed(nint address, float speed) =>
            true;
        public bool TryReadBgTail(nint address, byte[] into) => true;
        public void WriteBgTailHeld(nint address, byte[] values) { }
        public void WriteBgNightState(nint address, bool night) => LastNightState = night;
        public void SetVfxIntensity(nint address, float intensity) { }
        public void PauseVfx(nint address)
        {
            if (_nodes.TryGetValue(address, out var node))
                node.Playback = VfxPlaybackState.Paused;
        }
        public bool TryPauseVfx(nint address)
        {
            PauseVfx(address);
            return true;
        }
        public bool TryResumeVfx(nint address, float speed)
        {
            ResumeVfx(address, speed);
            return true;
        }
        public void ResumeVfx(nint address, float speed)
        {
            if (_nodes.TryGetValue(address, out var node))
                node.Playback = VfxPlaybackState.Playing;
            SetVfxSpeed(address, speed);
        }
        public bool IsVfxActive(nint address) => true;
        public bool TryReadVfxPlayback(
            nint address, out VfxPlaybackState playback)
        {
            if (_nodes.TryGetValue(address, out var node) && node.IsVfx
                && node.Playback != VfxPlaybackState.Unavailable)
            {
                playback = node.Playback;
                return true;
            }
            playback = VfxPlaybackState.Unavailable;
            return false;
        }
        public bool TryReadVfxState(
            nint address,
            out System.Numerics.Vector4 color,
            out System.Numerics.Vector3 intensity,
            out float speed)
        {
            color = _nodes.TryGetValue(address, out var node)
                ? node.Color
                : System.Numerics.Vector4.One;
            intensity = System.Numerics.Vector3.One;
            speed = 1f;
            return _nodes.TryGetValue(address, out var statedNode)
                && statedNode.IsVfx;
        }
        public void RestoreVfxState(
            nint address,
            System.Numerics.Vector4 color,
            System.Numerics.Vector3 intensity,
            float speed,
            bool resume) { }

        public bool TryRestoreVfxState(
            nint address, VfxStateSnapshot snapshot)
        {
            if (NoOpRestore)
                return false;
            if (!_nodes.TryGetValue(address, out var node) || !node.IsVfx)
                return false;
            node.Color = snapshot.Color;
            if (snapshot.Playback == VfxPlaybackState.Playing)
                ResumeVfx(address, snapshot.Speed);
            else
            {
                PauseVfx(address);
                SetVfxSpeed(address, snapshot.Speed);
                if (snapshot.Playback == VfxPlaybackState.Inactive)
                    node.Playback = VfxPlaybackState.Inactive;
            }
            return true;
        }

        public void WriteOpacity(nint address, float opacity)
        {
            if (_nodes.TryGetValue(address, out var node)) node.Opacity = opacity;
        }

        public bool TryReadOpacity(nint address, out float opacity)
        {
            opacity = _nodes.TryGetValue(address, out var node) ? node.Opacity : 1f;
            return node != null;
        }

        public nint Spawn(string path, in Transform placement)
        {
            var address = _next++;
            LastSpawned = address;
            _nodes[address] = new Node
            {
                Placement = placement,
                Flags = 0,
                Visible = true,
                IsVfx = path.EndsWith(".avfx", StringComparison.OrdinalIgnoreCase),
                ResourceIdentity = path.EndsWith(".avfx", StringComparison.OrdinalIgnoreCase)
                    ? address
                    : nint.Zero,
                Playback = path.EndsWith(".avfx", StringComparison.OrdinalIgnoreCase)
                    ? VfxPlaybackState.Playing
                    : VfxPlaybackState.Unavailable,
            };
            return address;
        }

        public nint Spawn(string path, in Transform placement, out WorldObjectIncarnation identity)
        {
            var address = Spawn(path, placement);
            identity = address == nint.Zero ? default : new(address, address.ToInt64(),
                _nodes[address].ResourceIdentity, _nodes[address].IsVfx);
            return address;
        }

        public void Destroy(nint address)
        {
            Destroyed.Add(address);
            _nodes.Remove(address);
        }

        public bool TryDestroy(nint address)
        {
            if (FailFreshDestroy && address == LastSpawned)
                return false;
            Destroy(address);
            return true;
        }

        public nint Add(
            string path, Transform placement, byte flags = 0, bool visible = true,
            bool isVfx = false)
        {
            var address = _next;
            _next += 0x100;
            _nodes[address] = new Node
            {
                Path = path,
                Placement = placement,
                Flags = flags,
                Visible = visible,
                IsVfx = isVfx,
                ResourceIdentity = isVfx ? address : nint.Zero,
                Playback = isVfx ? VfxPlaybackState.Playing : VfxPlaybackState.Unavailable,
            };
            return address;
        }

        /// <summary>The address stops being a BG object — a zone streaming
        /// event, or the object simply going away under the claim.</summary>
        public void Kill(nint address) => _nodes.Remove(address);

        public void Replace(
            nint address, string path, Transform placement, bool isVfx = false)
        {
            _nodes[address] = new Node
            {
                Path = path,
                Placement = placement,
                Visible = true,
                IsVfx = isVfx,
                ResourceIdentity = isVfx ? address : nint.Zero,
                Playback = isVfx
                    ? VfxPlaybackState.Playing
                    : VfxPlaybackState.Unavailable,
            };
            _generations[address] = _generations.TryGetValue(address, out var prior)
                ? prior + 1
                : address.ToInt64() + 1;
        }

        public Transform PlacementOf(nint address) => _nodes[address].Placement;

        public void SetVfxPlayback(nint address, VfxPlaybackState state) =>
            _nodes[address].Playback = state;

        public VfxPlaybackState PlaybackOf(nint address) =>
            _nodes[address].Playback;

        public void SetVfxColor(nint address, Vector4 color) =>
            _nodes[address].Color = color;

        public Vector4 ColorOf(nint address) => _nodes[address].Color;

        public byte FlagsOf(nint address) => _nodes[address].Flags;

        public bool VisibleOf(nint address) => _nodes[address].Visible;

        public IReadOnlyList<WorldObjectRow> Enumerate()
        {
            var rows = new List<WorldObjectRow>(_nodes.Count);
            foreach (var (address, node) in _nodes)
                rows.Add(new WorldObjectRow(
                    address, node.Path, node.Placement, node.Flags, node.IsVfx));
            return rows;
        }

        public IReadOnlyList<nint> EnumerateLights() => [];

        public bool TryReadOutline(nint address, out byte outline)
        {
            if (_nodes.TryGetValue(address, out var node))
            {
                outline = node.Outline;
                return true;
            }
            outline = WorldObjectOutline.None;
            return false;
        }

        public void WriteOutline(nint address, byte outline)
        {
            if (_nodes.TryGetValue(address, out var node))
                node.Outline = outline;
        }

        public bool IsAlive(nint address) => _nodes.ContainsKey(address);

        public bool TryReleaseVfxClaim(WorldObjectIncarnation incarnation) =>
            true;

        public bool TryRead(nint address, out Transform placement)
        {
            if (_nodes.TryGetValue(address, out var node))
            {
                placement = node.Placement;
                return true;
            }
            placement = Transform.Identity;
            return false;
        }

        public void Write(nint address, in Transform placement)
        {
            if (!_nodes.TryGetValue(address, out var node))
                return;
            node.Placement = placement;
            Writes++;
        }

        public void WriteVfxTransform(
            nint address, in Transform placement)
        {
            Write(address, placement);
        }

        public bool TryWriteVfxTransform(
            nint address, in Transform placement)
        {
            WriteVfxTransform(address, placement);
            return true;
        }

        public bool TryReadFlags(nint address, out byte flags)
        {
            if (_nodes.TryGetValue(address, out var node))
            {
                flags = node.Flags;
                return true;
            }
            flags = 0;
            return false;
        }

        public void WriteFlags(nint address, byte flags)
        {
            if (_nodes.TryGetValue(address, out var node))
                node.Flags = flags;
        }

        public bool TryReadVisible(nint address, out bool visible)
        {
            if (_nodes.TryGetValue(address, out var node))
            {
                visible = node.Visible;
                return true;
            }
            visible = false;
            return false;
        }

        public void WriteVisible(nint address, bool visible)
        {
            if (_nodes.TryGetValue(address, out var node))
                node.Visible = visible;
        }

        private sealed class Node
        {
            public string Path = string.Empty;
            public Transform Placement;
            public byte Flags;
            public bool Visible;
            public bool IsVfx;
            public nint ResourceIdentity;
            public Vector4 Color = Vector4.One;
            public VfxPlaybackState Playback = VfxPlaybackState.Unavailable;
            public float Speed = 1f;
            public float Opacity = 1f;
            public byte Outline = WorldObjectOutline.None;
        }
    }

    private sealed class FakeEventBus : IEventBus
    {
        private readonly Dictionary<Type, List<Delegate>> _handlers = new();

        public void Dispose() { }

        public void Subscribe<T>(Action<T> handler) where T : IEvent
        {
            if (!_handlers.TryGetValue(typeof(T), out var list))
                _handlers[typeof(T)] = list = new();
            list.Add(handler);
        }

        public void Unsubscribe<T>(Action<T> handler) where T : IEvent
        {
            if (_handlers.TryGetValue(typeof(T), out var list))
                list.Remove(handler);
        }

        public void Publish<T>(T evt) where T : IEvent
        {
            if (_handlers.TryGetValue(typeof(T), out var list))
                foreach (var handler in list.ToArray())
                    ((Action<T>)handler)(evt);
        }
    }

    private class SilentLog : DispatchProxy
    {
        protected override object? Invoke(
            MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.ReturnType is { IsValueType: true } type &&
                type != typeof(void))
                return Activator.CreateInstance(type);
            return null;
        }
    }
}
