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

    [Fact]
    public void Saved_identity_re_adopts_by_path_and_map_point_with_tolerance()
    {
        var world = new World();
        var address = world.Port.Add("bg/tree.mdl", Placed);
        var adopted = world.Service.AdoptByIdentity(
            "bg/tree.mdl", Placed.Position + new Vector3(.001f, -.001f, .001f),
            Moved, visible: false, out var detail);

        Assert.NotNull(adopted);
        Assert.Null(detail);
        Assert.Equal(address, adopted!.Address);
        Assert.Equal(Moved, world.Port.PlacementOf(address));
        world.Service.Release(adopted);
        Assert.Equal(Placed, world.Port.PlacementOf(address));
    }

    [Fact]
    public void Dead_address_or_wrong_map_point_refuses_without_writing()
    {
        var world = new World();
        var dead = world.Port.Add("bg/tree.mdl", Placed);
        world.Port.Kill(dead);
        Assert.Null(world.Service.Adopt(dead));

        world.Port.Add("bg/tree.mdl", Placed with { Position = Placed.Position + new Vector3(20, 0, 0) });
        Assert.Null(world.Service.AdoptByIdentity("bg/tree.mdl", Placed.Position, Moved, true, out var detail));
        Assert.Contains("not standing", detail!);
        Assert.Equal(0, world.Port.Writes);
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
        private nint _next = 0x1000;

        public int Writes { get; private set; }
        public bool ThrowOnWrite { get; set; }
        public readonly List<nint> Destroyed = new();

        public bool IsAvailable => true;

        public void SetVfxSpeed(nint address, float speed) { }

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
        public void PauseVfx(nint address) { }
        public void ResumeVfx(nint address, float speed) { }
        public bool IsVfxActive(nint address) => true;

        public void WriteOpacity(nint address, float opacity) { }

        public nint Spawn(string path, in Transform placement)
        {
            var address = _next++;
            _nodes[address] = new Node
            {
                Placement = placement,
                Flags = 0,
                Visible = true,
            };
            return address;
        }

        public void Destroy(nint address)
        {
            Destroyed.Add(address);
            _nodes.Remove(address);
        }

        public nint Add(
            string path, Transform placement, byte flags = 0, bool visible = true)
        {
            var address = _next;
            _next += 0x100;
            _nodes[address] = new Node
            {
                Path = path,
                Placement = placement,
                Flags = flags,
                Visible = visible,
            };
            return address;
        }

        /// <summary>The address stops being a BG object — a zone streaming
        /// event, or the object simply going away under the claim.</summary>
        public void Kill(nint address) => _nodes.Remove(address);

        public Transform PlacementOf(nint address) => _nodes[address].Placement;

        public byte FlagsOf(nint address) => _nodes[address].Flags;

        public bool VisibleOf(nint address) => _nodes[address].Visible;

        public IReadOnlyList<WorldObjectRow> Enumerate()
        {
            var rows = new List<WorldObjectRow>(_nodes.Count);
            foreach (var (address, node) in _nodes)
                rows.Add(new WorldObjectRow(
                    address, node.Path, node.Placement, node.Flags));
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
