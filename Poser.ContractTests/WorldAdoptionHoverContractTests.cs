extern alias ProductionPoser;

using System.Numerics;
using System.Reflection;
using Poser.Core;
using Poser.Game.WorldObjects;
using Poser.Services;
using ProductionPoser::Poser.UI;
using Xunit;
using PoserTransform = Poser.Transform;

namespace Poser.ContractTests;

/// <summary>
/// The hover mark is a PAIRING, and the pairing is the whole contract: what a
/// hover paints on a world object, leaving it unpaints — with the byte the
/// hover found, never a stated one. An outline that outlives its hover is a
/// mark on the map that nothing in Poser still knows about, which is the one
/// way this feature could do harm.
///
/// <para>The port is faked, so these facts hold with no game attached. Only
/// the object lane is expressible here: the actor lane's mark is a native
/// GameObject call with no port between, and the light lane takes no mark at
/// all (Ktisis marks neither — see WorldAdoptionSource.SetHovered).</para>
/// </summary>
public sealed class WorldAdoptionHoverContractTests
{
    private const byte Resting = 0x03;
    private const byte Painted = WorldObjectOutline.Hover;

    [Fact]
    public void Hovering_an_object_paints_it_and_leaving_puts_the_byte_back()
    {
        var world = new World();
        var address = world.Port.Add(resting: Resting);

        world.Source.SetHovered(Candidate(address));
        Assert.Equal(Painted, world.Port.OutlineOf(address));

        world.Source.SetHovered(null);
        Assert.Equal(Resting, world.Port.OutlineOf(address));
    }

    [Fact]
    public void A_resting_byte_that_was_not_none_is_still_what_comes_back()
    {
        var world = new World();
        // The byte carries more than the colour, so the restore may not be a
        // literal: whatever the object stood with is what it stands with after.
        var address = world.Port.Add(resting: 0x23);

        world.Source.SetHovered(Candidate(address));
        world.Source.SetHovered(null);

        Assert.Equal((byte)0x23, world.Port.OutlineOf(address));
    }

    [Fact]
    public void Holding_one_hover_across_frames_writes_the_mark_once()
    {
        var world = new World();
        var address = world.Port.Add(resting: Resting);

        // The overlay calls this every frame. Re-painting each one would make
        // the SECOND frame capture the mark as the resting value, and the
        // restore would then paint the object permanently.
        for (int frame = 0; frame < 5; frame++)
            world.Source.SetHovered(Candidate(address));

        Assert.Single(world.Port.OutlineWrites);
        world.Source.SetHovered(null);
        Assert.Equal(Resting, world.Port.OutlineOf(address));
    }

    [Fact]
    public void Moving_to_another_object_unpaints_the_first()
    {
        var world = new World();
        var first = world.Port.Add(resting: Resting);
        var second = world.Port.Add(resting: Resting);

        world.Source.SetHovered(Candidate(first));
        world.Source.SetHovered(Candidate(second));

        Assert.Equal(Resting, world.Port.OutlineOf(first));
        Assert.Equal(Painted, world.Port.OutlineOf(second));
    }

    [Fact]
    public void Ending_the_session_leaves_no_mark_behind()
    {
        var world = new World();
        var address = world.Port.Add(resting: Resting);
        world.Source.SetHovered(Candidate(address));

        world.Source.EndSession();

        Assert.Equal(Resting, world.Port.OutlineOf(address));
    }

    [Fact]
    public void An_object_that_cannot_be_read_is_never_painted()
    {
        var world = new World();

        world.Source.SetHovered(Candidate(0xDEAD));

        // Nothing was written, and the null that follows must not write either
        // — an unpaired restore onto an address Poser cannot address is the
        // write this contract exists to prevent.
        Assert.Empty(world.Port.OutlineWrites);
        world.Source.SetHovered(null);
        Assert.Empty(world.Port.OutlineWrites);
    }

    private static WorldAdoptionCandidate Candidate(nint address) =>
        new(
            WorldAdoptionKind.WorldObject,
            "borrowed",
            Vector3.Zero,
            0f,
            WorldObject: address);

    /// <summary>The source with only the seam these facts exercise attached.
    /// Every other service is absent on purpose: a hover neither adopts nor
    /// selects, and a fake that could would be pinning something else.
    /// </summary>
    private sealed class World
    {
        public FakeOutlinePort Port { get; } = new();

        public WorldObjectService Objects { get; }

        public WorldAdoptionSource Source { get; }

        public World()
        {
            Objects = new WorldObjectService(
                Port,
                new SilentBus(),
                DispatchProxy.Create<Dalamud.Plugin.Services.IPluginLog, SilentLog>());
            Source = new WorldAdoptionSource(
                null!, null!, Objects, null!, null!, null!, null!, null!, null!,
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

        public IReadOnlyList<WorldObjectRow> Enumerate() =>
            Array.Empty<WorldObjectRow>();

        public IReadOnlyList<nint> EnumerateLights() => Array.Empty<nint>();

        public bool IsAlive(nint address) => _outlines.ContainsKey(address);

        public bool TryRead(nint address, out PoserTransform placement)
        {
            placement = PoserTransform.Identity;
            return _outlines.ContainsKey(address);
        }

        public void Write(nint address, in PoserTransform placement)
        {
        }

        public bool TryReadFlags(nint address, out byte flags)
        {
            flags = 0;
            return _outlines.ContainsKey(address);
        }

        public void WriteFlags(nint address, byte flags)
        {
        }

        public bool TryReadVisible(nint address, out bool visible)
        {
            visible = true;
            return _outlines.ContainsKey(address);
        }

        public void WriteVisible(nint address, bool visible)
        {
        }
    }

    private sealed class SilentBus : IEventBus
    {
        public void Subscribe<T>(Action<T> handler) where T : IEvent
        {
        }

        public void Unsubscribe<T>(Action<T> handler) where T : IEvent
        {
        }

        public void Publish<T>(T evt) where T : IEvent
        {
        }

        public void Dispose()
        {
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
