using System;
using System.Collections.Generic;
using System.Numerics;
using System.Reflection;
using Dalamud.Plugin.Services;
using Poser.Core;
using Poser.Game.WorldObjects;
using Poser.Services;

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
        Assert.Equal(0, world.Port.Writes);
    }

    [Fact]
    public void Moving_then_releasing_restores_map_state_without_destroying_the_object()
    {
        var world = new World();
        var address = world.Port.Add("bg/tree.mdl", Placed, flags: 0x21, visible: true);
        var adopted = world.Service.Adopt(address)!;
        adopted.Transform = Moved;
        adopted.Visible = false;

        Assert.True(world.Service.Release(adopted));
        Assert.Equal(Placed, world.Port.PlacementOf(address));
        Assert.Equal((byte)0x21, world.Port.FlagsOf(address));
        Assert.True(world.Port.VisibleOf(address));
        Assert.True(world.Port.IsAlive(address));
        Assert.False(adopted.IsValid);
    }

    // Identity re-adoption is GONE by ruling (2026-09-01): a document
    // never carries a borrow, so a load never matches the map. The
    // dead-address guard survives on the plain Adopt path.
    [Fact]
    public void Dead_address_refuses_without_writing()
    {
        var world = new World();
        var dead = world.Port.Add("bg/tree.mdl", Placed);
        world.Port.Kill(dead);
        Assert.Null(world.Service.Adopt(dead));
        Assert.Equal(0, world.Port.Writes);
    }

    [Fact]
    public void Repeated_playing_vfx_transforms_make_no_playback_requests()
    {
        var world = new World();
        var address = world.Port.Add("vfx/fire.avfx", Placed, isVfx: true);
        world.Port.SetVfxPlayback(address, VfxPlaybackState.Playing);
        var effect = world.Service.Adopt(address)!;

        effect.Transform = Moved;
        effect.Transform = Placed;
        Assert.Equal(2, world.Port.VfxTransformWrites);
        Assert.Equal(0, world.Port.ResumeCalls);

        effect.VfxPaused = true;
        Assert.Equal(1, world.Port.PauseCalls);
        effect.Transform = Placed;
        Assert.Equal(2, world.Port.VfxTransformWrites);

        effect.VfxPaused = false;
        Assert.Equal(1, world.Port.ResumeCalls);
        effect.Transform = Moved;
        effect.Transform = Placed;
        Assert.Equal(1, world.Port.ResumeCalls);

        world.Port.SetVfxPlayback(address, VfxPlaybackState.Inactive);
        effect.Transform = Moved;
        Assert.Equal(4, world.Port.VfxTransformWrites);
        Assert.Equal(Moved, world.Port.PlacementOf(address));
    }

    [Theory]
    [InlineData(VfxPlaybackState.Playing)]
    [InlineData(VfxPlaybackState.Paused)]
    [InlineData(VfxPlaybackState.Inactive)]
    public void Vfx_release_restores_the_exact_original_playback_state(
        VfxPlaybackState original)
    {
        var world = new World();
        var address = world.Port.Add("vfx/fire.avfx", Placed, isVfx: true);
        world.Port.SetVfxPlayback(address, original);
        var effect = world.Service.Adopt(address)!;
        effect.VfxPaused = true;
        effect.Transform = Moved;

        Assert.True(world.Service.Release(effect));
        Assert.Equal(original, world.Port.PlaybackOf(address));
        Assert.Equal(Placed, world.Port.PlacementOf(address));
    }

    [Fact]
    public void Inactive_restore_stops_before_restoring_authored_speed()
    {
        var world = new World();
        var address = world.Port.Add("vfx/fire.avfx", Placed, isVfx: true);
        world.Port.SetVfxPlayback(address, VfxPlaybackState.Inactive);
        var effect = world.Service.Adopt(address)!;
        Assert.True(world.Service.Release(effect));

        Assert.Equal(VfxPlaybackState.Inactive, world.Port.PlaybackOf(address));
        Assert.Equal(1, world.Port.PauseCalls);
        Assert.True(world.Port.SpeedWrites >= 1);
        Assert.Equal(new[] { "pause", "speed" },
            world.Port.VfxOperations.TakeLast(2));
    }

    [Fact]
    public void Observed_vfx_stays_vfx_when_resource_path_is_unavailable()
    {
        var world = new World();
        var address = world.Port.Add(string.Empty, Placed, isVfx: true);
        world.Port.SetVfxPlayback(address, VfxPlaybackState.Playing);

        Assert.True(world.Service.Adopt(address)!.IsVfx);
        Assert.Contains(world.Service.EnumerateWorld(), row => row.IsEffect);
    }

    [Fact]
    public void Native_vfx_kind_survives_an_unavailable_row()
    {
        var world = new World();
        var address = world.Port.Add("", Placed, isVfx: true);
        world.Port.HideRows = true;

        Assert.True(world.Service.Adopt(address)!.IsVfx);
    }

    [Fact]
    public void Observed_vfx_refuses_when_playback_is_unavailable()
    {
        var world = new World();
        var address = world.Port.Add(string.Empty, Placed, isVfx: true);
        world.Port.SetVfxPlayback(address, VfxPlaybackState.Unavailable);

        Assert.Null(world.Service.Adopt(address));
    }

    [Fact]
    public void Vfx_owner_requires_resource_and_rejects_replacement_identity()
    {
        var world = new World();
        var address = world.Port.Add("", Placed, isVfx: true);
        world.Port.SetVfxResource(address, nint.Zero);
        var owner = new VfxLifecycleOwner(world.Port);

        Assert.False(owner.TryCapture(address, out _, out _));

        world.Port.SetVfxResource(address, 0xCAFE);
        Assert.True(owner.TryCapture(address, out var identity, out _));
        Assert.True(owner.IsCurrent(identity));
        world.Port.Replace(address, "", Moved, isVfx: true);
        Assert.False(owner.IsCurrent(identity));
    }

    [Fact]
    public void Release_and_gpose_exit_are_idempotent()
    {
        var world = new World();
        var address = world.Port.Add("vfx/fire.avfx", Placed, isVfx: true);
        var effect = world.Service.Adopt(address)!;

        Assert.True(world.Service.Release(effect));
        Assert.False(world.Service.Release(effect));
        world.Events.Publish(new GPoseStateChangedEvent(false));
        world.Service.Dispose();

        Assert.Equal(0, world.Service.Count);
        Assert.True(world.Port.IsAlive(address));
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
    public void Spawned_vfx_is_destroyed_once_on_release()
    {
        var world = new World();
        var spawned = world.Service.Spawn(
            "vfx/fire.avfx", Placed, true, out var detail);

        Assert.NotNull(spawned);
        Assert.Null(detail);
        var address = spawned!.Address;
        Assert.True(world.Service.Release(spawned));
        Assert.Contains(address, world.Port.Destroyed);
        Assert.False(world.Service.Release(spawned));
    }

    [Fact]
    public void Two_spawned_same_path_instances_release_independently()
    {
        var world = new World();
        var first = world.Service.Spawn(
            "vfx/fire.avfx", Placed, true, out _)!;
        var second = world.Service.Spawn(
            "vfx/fire.avfx", Moved, true, out _)!;

        Assert.True(world.Service.Release(first));
        Assert.True(second.IsValid);
        Assert.DoesNotContain(first.Address, world.Port.LiveAddresses);
        Assert.True(world.Service.Release(second));
        Assert.Empty(world.Port.LiveAddresses);
    }

    [Fact]
    public void Respawn_validates_fresh_identity_before_destroying_old()
    {
        var world = new World();
        var spawned = world.Service.Spawn(
            "vfx/fire.avfx", Placed, true, out _)!;
        world.Port.FailIdentityReads = 3;

        Assert.False(spawned.Respawn("vfx/new.avfx", out _));
        Assert.DoesNotContain(spawned.Address, world.Port.Destroyed);
        Assert.True(spawned.IsValid);
    }

    [Fact]
    public void Respawn_old_failure_cleans_fresh_and_retains_pending_until_retry()
    {
        var world = new World();
        var spawned = world.Service.Spawn(
            "vfx/fire.avfx", Placed, true, out _)!;
        world.Port.FailDestroyAddresses.Add(spawned.Address);
        world.Port.FailFreshDestroy = true;

        Assert.False(spawned.Respawn("vfx/new.avfx", out var detail));
        Assert.Equal("Respawn cleanup remains outstanding.", detail);
        Assert.True(spawned.IsValid);

        world.Port.FailDestroyAddresses.Clear();
        world.Port.FailFreshDestroy = false;
        world.Service.Dispose();
        Assert.False(spawned.IsValid);
    }

    [Fact]
    public void Failed_spawned_teardown_retains_handle_for_retry()
    {
        var world = new World();
        var spawned = world.Service.Spawn(
            "vfx/fire.avfx", Placed, true, out _)!;
        world.Port.FailDestroy = true;

        Assert.False(world.Service.Release(spawned));
        Assert.True(spawned.IsValid);
        world.Port.FailDestroy = false;
        Assert.True(world.Service.Release(spawned));
        Assert.False(spawned.IsValid);
    }

    [Fact]
    public void Gpose_exit_failure_enters_teardown_only_mode()
    {
        var world = new World();
        var spawned = world.Service.Spawn(
            "vfx/fire.avfx", Placed, true, out _)!;
        world.Port.FailDestroy = true;

        world.Events.Publish(new GPoseStateChangedEvent(false));
        int speedWrites = world.Port.SpeedWrites;
        spawned.VfxSpeed = 2f;

        Assert.Equal(speedWrites, world.Port.SpeedWrites);
        Assert.True(spawned.IsValid);
        world.Port.FailDestroy = false;
        Assert.True(world.Service.Release(spawned));
    }

    [Fact]
    public void Failed_playback_commands_do_not_commit_managed_state()
    {
        var world = new World();
        var address = world.Port.Add("vfx/fire.avfx", Placed, isVfx: true);
        var effect = world.Service.Adopt(address)!;
        effect.VfxPaused = true;
        world.Port.SetVfxPlayback(address, VfxPlaybackState.Unavailable);

        effect.VfxPaused = false;
        Assert.True(effect.VfxPaused);

        world.Port.Replace(address, "vfx/replacement.avfx", Moved, isVfx: true);
        float priorSpeed = effect.VfxSpeed;
        effect.VfxSpeed = 3f;
        Assert.Equal(priorSpeed, effect.VfxSpeed);
    }

    [Fact]
    public void Native_noop_speed_pause_resume_and_refresh_refuse()
    {
        var world = new World();
        var address = world.Port.Add("vfx/fire.avfx", Placed, isVfx: true);
        var effect = world.Service.Adopt(address)!;
        world.Port.NoOpSpeed = true;
        float priorSpeed = effect.VfxSpeed;
        effect.VfxSpeed = 3f;
        Assert.Equal(priorSpeed, effect.VfxSpeed);

        effect.VfxPaused = true;
        Assert.True(effect.VfxPaused);
        world.Port.NoOpPlayback = true;
        effect.VfxPaused = false;
        Assert.True(effect.VfxPaused);
        effect.Transform = Moved;
        world.Port.NoOpRefresh = true;
        world.Port.SetVfxPlayback(address, VfxPlaybackState.Playing);
        effect.Transform = Placed;
        Assert.Equal(Moved, world.Port.PlacementOf(address));
    }

    [Fact]
    public void Failed_vfx_restore_keeps_handle_for_retry()
    {
        var world = new World();
        var address = world.Port.Add("vfx/fire.avfx", Placed, isVfx: true);
        var effect = world.Service.Adopt(address)!;
        world.Port.NoOpRestore = true;

        Assert.False(world.Service.Release(effect));
        Assert.True(effect.IsValid);

        world.Port.NoOpRestore = false;
        Assert.True(world.Service.Release(effect));
    }

    [Fact]
    public void Vfx_restore_preserves_fractional_alpha_snapshot()
    {
        var world = new World();
        var address = world.Port.Add("vfx/fire.avfx", Placed, isVfx: true);
        world.Port.SetVfxColor(address, new Vector4(1f, 1f, 1f, 0.35f));
        var effect = world.Service.Adopt(address)!;

        Assert.True(world.Service.Release(effect));
        Assert.Equal(0.35f, world.Port.ColorOf(address).W);
        Assert.Equal(0, world.Port.VisibleWrites);
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
        public bool ThrowOnWrite { get; set; }
        public bool NoOpSpeed { get; set; }
        public bool NoOpPlayback { get; set; }
        public bool NoOpRefresh { get; set; }
        public bool NoOpRestore { get; set; }
        public bool HideRows { get; set; }
        public bool FailDestroy { get; set; }
        public int FailIdentityReads { get; set; }
        public bool FailFreshDestroy { get; set; }
        public HashSet<nint> FailDestroyAddresses { get; } = new();
        public readonly List<nint> Destroyed = new();
        public int VfxTransformWrites { get; private set; }
        public int PauseCalls { get; private set; }
        public int ResumeCalls { get; private set; }
        public int SpeedWrites { get; private set; }
        public int VisibleWrites { get; private set; }
        public List<string> VfxOperations { get; } = new();
        public IReadOnlyCollection<nint> LiveAddresses => _nodes.Keys;
        public nint LastSpawned { get; private set; }

        public bool IsAvailable => true;

        public bool TryReadIncarnation(
            nint address, out WorldObjectIncarnation incarnation)
        {
            if (FailIdentityReads > 0)
            {
                FailIdentityReads--;
                incarnation = default;
                return false;
            }
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
            VfxOperations.Add("speed");
            SpeedWrites++;
            if (_nodes.TryGetValue(address, out var node))
                node.Speed = speed;
        }

        public bool TrySetVfxSpeed(nint address, float speed)
        {
            if (NoOpSpeed)
                return false;
            SetVfxSpeed(address, speed);
            return true;
        }

        public void WriteVfxTint(
            nint address, System.Numerics.Vector3 tint) { }
        public bool WriteBgTint(
            nint address, System.Numerics.Vector3? tint) => true;
        public bool IsBgReady(nint address) => true;
        public bool? CanDyeBg(nint address) => null;
        public bool? ReadBgNightState(nint address) => null;
        public bool WriteBgAnimationSpeed(nint address, float speed) =>
            true;
        public byte? ReadBgTailByte(nint address, int offset) => null;
        public void WriteBgTailByte(nint address, int offset, byte value) { }
        public string DescribeBgAnimation(nint address) => string.Empty;
        public bool TryReadBgTail(nint address, byte[] into) => false;
        public void WriteBgTailHeld(nint address, byte[] values) { }
        public ulong? ReadBgObjectFlags(nint address) => null;
        public void WriteBgObjectFlags(nint address, ulong flags) { }
        public void WriteBgNightState(nint address, bool night) { }
        public void SetVfxIntensity(nint address, float intensity) { }
        public void PauseVfx(nint address)
        {
            VfxOperations.Add("pause");
            PauseCalls++;
            if (_nodes.TryGetValue(address, out var node))
                node.Playback = VfxPlaybackState.Paused;
        }
        public bool TryPauseVfx(nint address)
        {
            if (NoOpPlayback)
                return false;
            PauseVfx(address);
            return true;
        }
        public bool TryResumeVfx(nint address, float speed)
        {
            if (NoOpPlayback)
                return false;
            ResumeVfx(address, speed);
            return true;
        }
        public void ResumeVfx(nint address, float speed)
        {
            VfxOperations.Add("resume");
            ResumeCalls++;
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

        public void WriteOpacity(nint address, float opacity) { }

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

        public void Destroy(nint address)
        {
            Destroyed.Add(address);
            _nodes.Remove(address);
        }

        public bool TryDestroy(nint address)
        {
            if (FailDestroy || FailDestroyAddresses.Contains(address)
                || (FailFreshDestroy && address == LastSpawned))
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

        public void SetVfxResource(nint address, nint resource) =>
            _nodes[address].ResourceIdentity = resource;

        public VfxPlaybackState PlaybackOf(nint address) =>
            _nodes[address].Playback;

        public void SetVfxColor(nint address, Vector4 color) =>
            _nodes[address].Color = color;

        public Vector4 ColorOf(nint address) => _nodes[address].Color;

        public byte FlagsOf(nint address) => _nodes[address].Flags;

        public bool VisibleOf(nint address) => _nodes[address].Visible;

        public IReadOnlyList<WorldObjectRow> Enumerate()
        {
            if (HideRows)
                return Array.Empty<WorldObjectRow>();
            var rows = new List<WorldObjectRow>(_nodes.Count);
            foreach (var (address, node) in _nodes)
                rows.Add(new WorldObjectRow(
                    address, node.Path, node.Placement, node.Flags, node.IsVfx));
            return rows;
        }

        /// <summary>The graph's light-typed nodes. A light is never a BG
        /// object, so this listing and <see cref="Enumerate"/>'s never
        /// overlap — the same partition the real walk makes by ObjectType.
        /// </summary>
        public List<nint> Lights { get; } = new();

        public IReadOnlyList<nint> EnumerateLights() => Lights.ToArray();

        /// <summary>Every outline byte this fake was ever asked to write, in
        /// order. The hover contract is a PAIRING — what goes on comes off —
        /// and a sequence is the only thing that can state it.</summary>
        public List<(nint Address, byte Outline)> OutlineWrites { get; } = new();

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
            OutlineWrites.Add((address, outline));
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
            if (ThrowOnWrite)
                throw new InvalidOperationException("the world refused a write");
            if (!_nodes.TryGetValue(address, out var node))
                return;
            node.Placement = placement;
            Writes++;
        }

        public void WriteVfxTransform(
            nint address, in Transform placement)
        {
            Write(address, placement);
            VfxTransformWrites++;
        }

        public bool TryWriteVfxTransform(
            nint address, in Transform placement)
        {
            if (NoOpRefresh)
                return false;
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
            VisibleWrites++;
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
            public byte Outline = WorldObjectOutline.None;
        }
    }

    private sealed class FakeEventBus : IEventBus
    {
        private readonly Dictionary<Type, List<Delegate>> _handlers = new();

        public int ListChanges { get; private set; }

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
            if (evt is WorldObjectListChangedEvent)
                ListChanges++;
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
