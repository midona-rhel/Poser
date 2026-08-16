extern alias ProductionPoser;

using System.Numerics;
using System.Reflection;
using Poser.Application.Scene;
using Poser.Application.Selection;
using Poser.Core;
using Poser.Domain.Identity;
using Poser.Domain.Scene;
using Poser.Domain.Transforms;
using Poser.Game.WorldObjects;
using Poser.Services;
using ProductionPoser::Poser.UI;
using PoserTransform = Poser.Transform;

namespace Poser.ContractTests;

public sealed class WorldObjectTransformContractTests
{
    [Fact]
    public void Adoption_and_hover_restore_keep_world_objects_live_and_paired()
    {
        var lineage = Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd");
        var objectId = new WorldObjectId(lineage, 0);
        var session = new SceneSession(new SelectionSession());
        Assert.Equal(
            SceneRefreshOutcome.Applied,
            session.TryRefresh(Scene(1, Borrowed(objectId))).Outcome);
        Assert.True(session.Contains(TransformTargetId.ForWorldObject(objectId)));

        var world = new HoverWorld();
        var address = world.Port.Add(0x23);
        world.Source.SetHovered(Candidate(address));
        Assert.Equal(WorldObjectOutline.Hover, world.Port.OutlineOf(address));
        world.Source.EndSession();
        Assert.Equal((byte)0x23, world.Port.OutlineOf(address));
    }

    [Fact]
    public void Candidate_filter_and_multi_frame_hover_restore_are_address_paired()
    {
        var world = new HoverWorld();
        var first = world.Port.Add(
            "bg/ffxiv/first.mdl", Transform.Identity, 0x11);
        var second = world.Port.Add(
            "bg/ffxiv/second.mdl", Transform.Identity, 0x22);
        var firstCandidate = Candidate(first);
        var secondCandidate = Candidate(second);

        Assert.Equal(2, world.Service.GetCandidates().Count);
        var adopted = world.Service.Adopt(first);
        Assert.NotNull(adopted);
        Assert.Single(world.Service.GetCandidates());
        Assert.DoesNotContain(
            world.Service.GetCandidates(), item => item.Address == first);

        world.Source.SetHovered(firstCandidate);
        world.Source.SetHovered(firstCandidate);
        world.Source.SetHovered(secondCandidate);
        world.Source.SetHovered(null);

        Assert.Equal((byte)0x11, world.Port.OutlineOf(first));
        Assert.Equal((byte)0x22, world.Port.OutlineOf(second));
        Assert.Contains(
            world.Port.OutlineWrites,
            write => write.Address == first &&
                write.Outline == (byte)0x11);
        Assert.Contains(
            world.Port.OutlineWrites,
            write => write.Address == second &&
                write.Outline == (byte)0x22);
        Assert.True(adopted!.IsValid);
    }

    [Fact]
    public void Unreadable_and_refused_objects_are_not_claimed_and_release_restores()
    {
        var world = new HoverWorld();
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

    private static WorldObjectDescriptor Borrowed(WorldObjectId id) =>
        new(id, $"world-object-{id.LogicalId:N}", "bg/ffxiv/test.mdl");

    private static SceneSnapshot Scene(
        ulong revision,
        params WorldObjectDescriptor[] worldObjects) =>
        new(
            revision,
            Array.Empty<ActorDescriptor>(),
            Array.Empty<LightDescriptor>(),
            Array.Empty<CameraDescriptor>(),
            Array.Empty<PropDescriptor>(),
            WorldObjects: worldObjects);

    private static WorldAdoptionCandidate Candidate(nint address) =>
        new(WorldAdoptionKind.WorldObject, "borrowed", Vector3.Zero, 0f,
            WorldObject: address);

    private sealed class HoverWorld
    {
        public FakeOutlinePort Port { get; } = new();
        public WorldObjectService Service { get; }
        public WorldAdoptionSource Source { get; }

        public HoverWorld()
        {
            Service = new WorldObjectService(
                Port,
                new SilentBus(),
                DispatchProxy.Create<Dalamud.Plugin.Services.IPluginLog, SilentLog>());
            Source = new WorldAdoptionSource(
                null!, null!, Service, null!, null!, null!, null!, null!, null!,
                null!, null!);
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
