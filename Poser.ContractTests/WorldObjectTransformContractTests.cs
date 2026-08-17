using System.Numerics;
using System.Reflection;
using Poser.Core;
using Poser.Domain.Transforms;
using Poser.Game.WorldObjects;
using Poser.Services;
using PoserTransform = Poser.Transform;

namespace Poser.ContractTests;

public sealed class WorldObjectTransformContractTests
{
    [Fact]
    public void Unreadable_and_refused_objects_are_not_claimed_and_release_restores()
    {
        var world = new WorldFixture();
        var unreadable = world.Port.Add(
            "bg/ffxiv/unreadable.mdl", Transform.Identity, 0x31,
            readable: false);
        Assert.Null(world.Service.Adopt(unreadable));

        var refused = world.Service.AdoptByIdentity(
            "bg/ffxiv/missing.mdl",
            Vector3.Zero,
            Transform.Identity,
            visible: true,
            out var detail);
        Assert.Null(refused);
        Assert.Contains("not standing", detail!);

        var initial = new Transform
        {
            Position = new Vector3(1f, 2f, 3f),
            Rotation = Quaternion.Identity,
            Scale = Vector3.One,
        };
        var address = world.Port.Add("bg/ffxiv/live.mdl", initial, 0x41);
        var handle = world.Service.Adopt(address);
        Assert.NotNull(handle);
        var moved = new Transform
        {
            Position = new Vector3(8f, 9f, 10f),
            Rotation = initial.Rotation,
            Scale = initial.Scale,
        };
        handle!.Transform = moved;
        Assert.Equal(moved, world.Port.PlacementOf(address));
        Assert.Contains(
            world.Port.TransformWrites,
            write => write.Address == address && write.Placement.Equals(moved));

        Assert.True(world.Service.Release(handle));
        Assert.False(handle!.IsValid);
        Assert.Equal(initial, world.Port.PlacementOf(address));
        var writesAfterRelease = world.Port.TransformWrites.Count;
        handle!.Transform = new Transform
        {
            Position = new Vector3(99f, 99f, 99f),
            Rotation = Quaternion.Identity,
            Scale = Vector3.One,
        };
        Assert.Equal(writesAfterRelease, world.Port.TransformWrites.Count);
        Assert.Equal(initial, world.Port.PlacementOf(address));
    }

    private sealed class WorldFixture
    {
        public FakeOutlinePort Port { get; } = new();
        public WorldObjectService Service { get; }

        public WorldFixture()
        {
            Service = new WorldObjectService(
                Port,
                new SilentBus(),
                DispatchProxy.Create<Dalamud.Plugin.Services.IPluginLog, SilentLog>());
        }
    }

    private sealed class FakeOutlinePort : IWorldObjectPort
    {
        private sealed class ObjectState
        {
            public string Path = string.Empty;
            public Transform Placement;
            public byte Flags;
            public bool Visible = true;
            public bool Readable = true;
            public bool Alive = true;
        }

        private readonly Dictionary<nint, ObjectState> _objects = new();
        private readonly Dictionary<nint, byte> _outlines = new();
        private nint _next = 0x1000;

        public List<(nint Address, byte Outline)> OutlineWrites { get; } = new();
        public List<(nint Address, Transform Placement)> TransformWrites { get; } = new();

        public nint Add(byte resting)
        {
            return Add("bg/ffxiv/test.mdl", Transform.Identity, resting);
        }

        public nint Add(
            string path,
            Transform placement,
            byte resting,
            bool readable = true)
        {
            var address = _next;
            _next += 0x100;
            _objects[address] = new ObjectState
            {
                Path = path,
                Placement = placement,
                Flags = 0,
                Readable = readable,
            };
            _outlines[address] = resting;
            return address;
        }

        public byte OutlineOf(nint address) => _outlines[address];
        public Transform PlacementOf(nint address) => _objects[address].Placement;
        public bool IsAvailable => true;
        public bool TryReadOutline(nint address, out byte outline) =>
            _outlines.TryGetValue(address, out outline);

        public void WriteOutline(nint address, byte outline)
        {
            OutlineWrites.Add((address, outline));
            if (_outlines.ContainsKey(address))
                _outlines[address] = outline;
        }

        public IReadOnlyList<WorldObjectRow> Enumerate() =>
            _objects.Where(item => item.Value.Alive)
                .Select(item => new WorldObjectRow(
                    item.Key,
                    item.Value.Path,
                    item.Value.Placement,
                    item.Value.Flags))
                .ToArray();
        public IReadOnlyList<nint> EnumerateLights() => Array.Empty<nint>();
        public bool IsAlive(nint address) =>
            _objects.TryGetValue(address, out var state) && state.Alive;

        public bool TryRead(nint address, out PoserTransform placement)
        {
            if (_objects.TryGetValue(address, out var state) &&
                state.Alive && state.Readable)
            {
                placement = state.Placement;
                return true;
            }
            placement = PoserTransform.Identity;
            return false;
        }

        public void Write(nint address, in PoserTransform placement)
        {
            if (!_objects.TryGetValue(address, out var state) || !state.Alive)
                return;
            state.Placement = placement;
            TransformWrites.Add((address, placement));
        }

        public bool TryReadFlags(nint address, out byte flags)
        {
            if (_objects.TryGetValue(address, out var state) &&
                state.Alive && state.Readable)
            {
                flags = state.Flags;
                return true;
            }
            flags = 0;
            return false;
        }

        public void WriteFlags(nint address, byte flags)
        {
            if (_objects.TryGetValue(address, out var state) && state.Alive)
                state.Flags = flags;
        }

        public bool TryReadVisible(nint address, out bool visible)
        {
            if (_objects.TryGetValue(address, out var state) &&
                state.Alive && state.Readable)
            {
                visible = state.Visible;
                return true;
            }
            visible = true;
            return false;
        }

        public void WriteVisible(nint address, bool visible)
        {
            if (_objects.TryGetValue(address, out var state) && state.Alive)
                state.Visible = visible;
        }

        public void Remove(nint address)
        {
            if (_objects.TryGetValue(address, out var state))
                state.Alive = false;
        }
    }

    private sealed class SilentBus : IEventBus
    {
        public void Subscribe<T>(Action<T> handler) where T : IEvent { }
        public void Unsubscribe<T>(Action<T> handler) where T : IEvent { }
        public void Publish<T>(T evt) where T : IEvent { }
        public void Dispose() { }
    }

    private class SilentLog : DispatchProxy
    {
        protected override object? Invoke(
            MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.ReturnType is { IsValueType: true } type
                && type != typeof(void))
                return Activator.CreateInstance(type);
            return null;
        }
    }
}
