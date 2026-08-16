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
        public WorldAdoptionSource Source { get; }

        public HoverWorld()
        {
            var objects = new WorldObjectService(
                Port,
                new SilentBus(),
                DispatchProxy.Create<Dalamud.Plugin.Services.IPluginLog, SilentLog>());
            Source = new WorldAdoptionSource(
                null!, null!, objects, null!, null!, null!, null!, null!, null!,
                null!, null!);
        }
    }

    private sealed class FakeOutlinePort : IWorldObjectPort
    {
        private readonly Dictionary<nint, byte> _outlines = new();
        private nint _next = 0x1000;

        public List<(nint Address, byte Outline)> OutlineWrites { get; } = new();

        public nint Add(byte resting)
        {
            var address = _next;
            _next += 0x100;
            _outlines[address] = resting;
            return address;
        }

        public byte OutlineOf(nint address) => _outlines[address];
        public bool IsAvailable => true;
        public bool TryReadOutline(nint address, out byte outline) =>
            _outlines.TryGetValue(address, out outline);

        public void WriteOutline(nint address, byte outline)
        {
            OutlineWrites.Add((address, outline));
            if (_outlines.ContainsKey(address))
                _outlines[address] = outline;
        }

        public IReadOnlyList<WorldObjectRow> Enumerate() => Array.Empty<WorldObjectRow>();
        public IReadOnlyList<nint> EnumerateLights() => Array.Empty<nint>();
        public bool IsAlive(nint address) => _outlines.ContainsKey(address);

        public bool TryRead(nint address, out PoserTransform placement)
        {
            placement = PoserTransform.Identity;
            return _outlines.ContainsKey(address);
        }

        public void Write(nint address, in PoserTransform placement) { }

        public bool TryReadFlags(nint address, out byte flags)
        {
            flags = 0;
            return _outlines.ContainsKey(address);
        }

        public void WriteFlags(nint address, byte flags) { }

        public bool TryReadVisible(nint address, out bool visible)
        {
            visible = true;
            return _outlines.ContainsKey(address);
        }

        public void WriteVisible(nint address, bool visible) { }
    }

    private sealed class SilentBus : IEventBus
    {
        public void Subscribe<T>(Action<T> handler) where T : IEvent { }
        public void Unsubscribe<T>(Action<T> handler) where T : IEvent { }
        public void Publish<T>(T evt) where T : IEvent { }
        public void Dispose() { }
    }

    private sealed class SilentLog : DispatchProxy
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
