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

    // ── adoption captures, and writes nothing ────────────────────────────

    [Fact]
    public void Adopting_captures_the_placement_and_flags_and_writes_nothing()
    {
        var world = new World();
        var address = world.Port.Add("bg/tree.mdl", Placed, flags: 0x21, visible: true);

        var adopted = world.Service.Adopt(address);

        Assert.NotNull(adopted);
        Assert.Equal(Placed, adopted!.InitialPlacement);
        Assert.Equal((byte)0x21, adopted.InitialFlags);
        Assert.True(adopted.InitialVisible);
        Assert.Equal("tree", adopted.Name);
        Assert.Equal("bg/tree.mdl", adopted.Path);
        // Nothing was written: adoption is a READ plus a record of it.
        Assert.Equal(0, world.Port.Writes);
        Assert.Equal(Placed, world.Port.PlacementOf(address));
    }

    [Fact]
    public void Adopting_the_same_address_twice_is_one_claim_and_one_capture()
    {
        var world = new World();
        var address = world.Port.Add("bg/tree.mdl", Placed);
        var first = world.Service.Adopt(address)!;
        first.Transform = Moved;

        var second = world.Service.Adopt(address);

        Assert.Same(first, second);
        Assert.Single(world.Service.Adopted);
        // The second adoption must not re-capture the moved placement as the
        // thing to restore, or the release would give the map back the user's
        // edit instead of the map's own value.
        Assert.Equal(Placed, second!.InitialPlacement);
    }

    [Fact]
    public void Adopting_an_address_the_world_does_not_hold_refuses()
    {
        var world = new World();

        Assert.Null(world.Service.Adopt(0x1234));
        Assert.Empty(world.Service.Adopted);
    }

    // ── the write-through half ───────────────────────────────────────────

    [Fact]
    public void Moving_an_adopted_object_writes_through_to_the_world()
    {
        var world = new World();
        var address = world.Port.Add("bg/tree.mdl", Placed);
        var adopted = world.Service.Adopt(address)!;

        adopted.Transform = Moved;

        Assert.Equal(Moved, world.Port.PlacementOf(address));
        Assert.Equal(Moved, adopted.Transform);
    }

    // ── release restores ─────────────────────────────────────────────────

    [Fact]
    public void Releasing_puts_the_captured_placement_and_flags_back()
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
        Assert.Empty(world.Service.Adopted);
        Assert.False(adopted.IsValid);
    }

    [Fact]
    public void Releasing_never_destroys_the_object()
    {
        var world = new World();
        var address = world.Port.Add("bg/tree.mdl", Placed);
        var adopted = world.Service.Adopt(address)!;

        world.Service.Release(adopted);

        // The map still holds it, and it is listable again — a released claim
        // is a claim given back, never an object taken away.
        Assert.True(world.Port.IsAlive(address));
        Assert.Contains(
            world.Service.GetCandidates(),
            candidate => candidate.Address == address);
    }

    [Fact]
    public void Releasing_twice_is_a_no_op()
    {
        var world = new World();
        var address = world.Port.Add("bg/tree.mdl", Placed);
        var adopted = world.Service.Adopt(address)!;
        adopted.Transform = Moved;
        world.Service.Release(adopted);
        int writesAfterFirst = world.Port.Writes;

        Assert.False(world.Service.Release(adopted));

        Assert.Equal(writesAfterFirst, world.Port.Writes);
        Assert.Equal(Placed, world.Port.PlacementOf(address));
    }

    [Fact]
    public void A_released_handle_writes_nothing()
    {
        var world = new World();
        var address = world.Port.Add("bg/tree.mdl", Placed);
        var adopted = world.Service.Adopt(address)!;
        world.Service.Release(adopted);

        adopted.Transform = Moved;
        adopted.Visible = false;

        Assert.Equal(Placed, world.Port.PlacementOf(address));
        Assert.True(world.Port.VisibleOf(address));
    }

    // ── session and process edges ────────────────────────────────────────

    [Fact]
    public void Clearing_the_scene_restores_every_adopted_object()
    {
        var world = new World();
        var first = world.Port.Add("bg/a.mdl", Placed);
        var second = world.Port.Add("bg/b.mdl", Placed);
        world.Service.Adopt(first)!.Transform = Moved;
        world.Service.Adopt(second)!.Transform = Moved;

        world.Service.ReleaseAll();

        Assert.Equal(Placed, world.Port.PlacementOf(first));
        Assert.Equal(Placed, world.Port.PlacementOf(second));
        Assert.Empty(world.Service.Adopted);
    }

    [Fact]
    public void Leaving_gpose_restores_every_adopted_object()
    {
        var world = new World();
        var address = world.Port.Add("bg/tree.mdl", Placed);
        world.Service.Adopt(address)!.Transform = Moved;

        world.Events.Publish(new GPoseStateChangedEvent(false));

        Assert.Equal(Placed, world.Port.PlacementOf(address));
        Assert.Empty(world.Service.Adopted);
    }

    [Fact]
    public void Entering_gpose_releases_nothing()
    {
        var world = new World();
        var address = world.Port.Add("bg/tree.mdl", Placed);
        world.Service.Adopt(address)!.Transform = Moved;

        world.Events.Publish(new GPoseStateChangedEvent(true));

        Assert.Single(world.Service.Adopted);
        Assert.Equal(Moved, world.Port.PlacementOf(address));
    }

    [Fact]
    public void Unloading_the_plugin_restores_every_adopted_object()
    {
        var world = new World();
        var address = world.Port.Add("bg/tree.mdl", Placed);
        world.Service.Adopt(address)!.Transform = Moved;

        world.Service.Dispose();

        Assert.Equal(Placed, world.Port.PlacementOf(address));
        Assert.Empty(world.Service.Adopted);
    }

    [Fact]
    public void Gpose_exit_then_unload_is_correct_and_so_is_the_other_order()
    {
        var exitFirst = new World();
        var a = exitFirst.Port.Add("bg/tree.mdl", Placed);
        exitFirst.Service.Adopt(a)!.Transform = Moved;
        exitFirst.Events.Publish(new GPoseStateChangedEvent(false));
        exitFirst.Service.Dispose();
        Assert.Equal(Placed, exitFirst.Port.PlacementOf(a));

        var unloadFirst = new World();
        var b = unloadFirst.Port.Add("bg/tree.mdl", Placed);
        unloadFirst.Service.Adopt(b)!.Transform = Moved;
        unloadFirst.Service.Dispose();
        // The unsubscribe means the exit cannot reach a disposed service; the
        // object is already back either way.
        unloadFirst.Events.Publish(new GPoseStateChangedEvent(false));
        Assert.Equal(Placed, unloadFirst.Port.PlacementOf(b));
    }

    [Fact]
    public void A_disposed_service_adopts_nothing()
    {
        var world = new World();
        var address = world.Port.Add("bg/tree.mdl", Placed);
        world.Service.Dispose();

        Assert.Null(world.Service.Adopt(address));
        Assert.Empty(world.Service.GetCandidates());
    }

    // ── the address that stopped being one ───────────────────────────────

    [Fact]
    public void An_object_that_has_gone_is_released_without_a_write()
    {
        var world = new World();
        var address = world.Port.Add("bg/tree.mdl", Placed);
        var adopted = world.Service.Adopt(address)!;
        adopted.Transform = Moved;
        world.Port.Kill(address);
        int writesBefore = world.Port.Writes;

        Assert.True(world.Service.Release(adopted));

        // Nothing was written onto an address that is no longer a BG object.
        Assert.Equal(writesBefore, world.Port.Writes);
        Assert.Empty(world.Service.Adopted);
        Assert.False(adopted.IsValid);
    }

    [Fact]
    public void A_port_that_throws_on_restore_still_ends_the_claim()
    {
        var world = new World();
        var address = world.Port.Add("bg/tree.mdl", Placed);
        var adopted = world.Service.Adopt(address)!;
        world.Port.ThrowOnWrite = true;

        Assert.True(world.Service.Release(adopted));

        Assert.Empty(world.Service.Adopted);
        Assert.False(adopted.IsValid);
    }

    // ── the listing ──────────────────────────────────────────────────────

    [Fact]
    public void The_listing_excludes_what_the_scene_has_taken()
    {
        var world = new World();
        var first = world.Port.Add("bg/a.mdl", Placed);
        world.Port.Add("bg/b.mdl", Placed);

        world.Service.Adopt(first);

        Assert.DoesNotContain(
            world.Service.GetCandidates(),
            candidate => candidate.Address == first);
        Assert.Single(world.Service.GetCandidates());
    }

    /// <summary>
    /// The listing is neither ranged nor ranked here. The adoption range is
    /// measured from the CAMERA and is shared by the three adoption classes, so
    /// it belongs to the overlay's one listing pass; ordering by a distance
    /// from the PLAYER over every BG object in the zone would only be work the
    /// overlay throws away and redoes.
    ///
    /// <para>What this service does owe is the whole listing, with its world
    /// points intact, so the overlay has something to measure.</para>
    /// </summary>
    [Fact]
    public void The_listing_is_whole_and_carries_the_points_the_overlay_ranges_by()
    {
        var world = new World();
        var far = world.Port.Add(
            "bg/far.mdl",
            new Transform(new Vector3(50f, 0f, 0f), Quaternion.Identity, Vector3.One));
        var near = world.Port.Add(
            "bg/near.mdl",
            new Transform(new Vector3(2f, 0f, 0f), Quaternion.Identity, Vector3.One));

        var candidates = world.Service.GetCandidates();

        Assert.Equal(2, candidates.Count);
        Assert.Equal(
            new Vector3(50f, 0f, 0f),
            Assert.Single(candidates, c => c.Address == far).Position);
        Assert.Equal(
            new Vector3(2f, 0f, 0f),
            Assert.Single(candidates, c => c.Address == near).Position);
    }

    // ── a scene load's re-adoption ───────────────────────────────────────

    [Fact]
    public void Adopting_at_a_saved_placement_still_restores_the_maps_own()
    {
        var world = new World();
        var address = world.Port.Add("bg/tree.mdl", Placed, flags: 0x21, visible: true);

        var adopted = world.Service.AdoptAt(address, Moved, visible: false)!;

        Assert.Equal(Moved, world.Port.PlacementOf(address));
        Assert.False(world.Port.VisibleOf(address));
        Assert.Equal(Placed, adopted.InitialPlacement);

        world.Service.Release(adopted);

        Assert.Equal(Placed, world.Port.PlacementOf(address));
        Assert.Equal((byte)0x21, world.Port.FlagsOf(address));
        Assert.True(world.Port.VisibleOf(address));
    }

    /// <summary>A saved scene names an object by the pair the MAP owns — the
    /// model path and the point the map stands it at — and never by the address
    /// it was claimed at, which belonged to the run that saved it.</summary>
    [Fact]
    public void A_saved_entry_finds_its_object_by_path_and_map_point()
    {
        var world = new World();
        world.Port.Add("bg/rock.mdl", Placed);
        var wanted = world.Port.Add("bg/tree.mdl", Placed, flags: 0x21);

        var adopted = world.Service.AdoptByIdentity(
            "bg/tree.mdl", Placed.Position, Moved, visible: true, out var detail);

        Assert.NotNull(adopted);
        Assert.Null(detail);
        Assert.Equal(wanted, adopted!.Address);
        Assert.Equal(Moved, world.Port.PlacementOf(wanted));
        // The claim still captured what the MAP had, so the release is the
        // map's placement and not the file's.
        Assert.Equal(Placed, adopted.InitialPlacement);
        world.Service.Release(adopted);
        Assert.Equal(Placed, world.Port.PlacementOf(wanted));
    }

    /// <summary>The codec turns a float into a decimal string and back, so the
    /// point that comes out is near the point that went in, not equal to it.
    /// </summary>
    [Fact]
    public void A_map_point_within_tolerance_is_the_same_object()
    {
        var world = new World();
        var address = world.Port.Add("bg/tree.mdl", Placed);
        var nudged = Placed.Position + new Vector3(0.001f, -0.001f, 0.001f);

        var adopted = world.Service.AdoptByIdentity(
            "bg/tree.mdl", nudged, Moved, visible: true, out _);

        Assert.NotNull(adopted);
        Assert.Equal(address, adopted!.Address);
    }

    /// <summary>THE refusal that keeps this safe: an object of the right model
    /// standing somewhere else is a DIFFERENT object, and writing the file's
    /// placement onto it would displace a fixture nobody asked about.</summary>
    [Fact]
    public void The_same_model_standing_elsewhere_is_refused_by_name()
    {
        var world = new World();
        var elsewhere = world.Port.Add(
            "bg/tree.mdl",
            Placed with { Position = Placed.Position + new Vector3(30f, 0f, 0f) });

        var adopted = world.Service.AdoptByIdentity(
            "bg/tree.mdl", Placed.Position, Moved, visible: true, out var detail);

        Assert.Null(adopted);
        Assert.Contains("tree", detail);
        Assert.Equal(0, world.Port.Writes);
        Assert.Equal(
            Placed.Position + new Vector3(30f, 0f, 0f),
            world.Port.PlacementOf(elsewhere).Position);
    }

    [Fact]
    public void An_entry_whose_model_is_not_in_this_zone_is_refused_by_name()
    {
        var world = new World();
        world.Port.Add("bg/rock.mdl", Placed);

        var adopted = world.Service.AdoptByIdentity(
            "bg/tree.mdl", Placed.Position, Moved, visible: true, out var detail);

        Assert.Null(adopted);
        Assert.Contains("tree", detail);
        Assert.Equal(0, world.Port.Writes);
    }

    /// <summary>Two of a model within tolerance of one recorded point is a
    /// degenerate map, but the nearest one is still the answer — never both,
    /// and never an arbitrary one.</summary>
    [Fact]
    public void The_nearest_candidate_within_tolerance_wins()
    {
        var world = new World();
        var far = world.Port.Add(
            "bg/tree.mdl",
            Placed with { Position = Placed.Position + new Vector3(0.04f, 0f, 0f) });
        var near = world.Port.Add(
            "bg/tree.mdl",
            Placed with { Position = Placed.Position + new Vector3(0.005f, 0f, 0f) });

        var adopted = world.Service.AdoptByIdentity(
            "bg/tree.mdl", Placed.Position, Moved, visible: true, out _);

        Assert.Equal(near, adopted!.Address);
        Assert.NotEqual(far, adopted.Address);
    }

    /// <summary>An object this session already borrowed is standing where the
    /// USER left it, so re-adopting it would capture that as the map's own
    /// placement and lose what the release owes the map.</summary>
    [Fact]
    public void An_object_already_borrowed_is_refused_rather_than_reclaimed()
    {
        var world = new World();
        var address = world.Port.Add("bg/tree.mdl", Placed, flags: 0x21);
        var first = world.Service.Adopt(address)!;
        first.Transform = Moved;

        var again = world.Service.AdoptByIdentity(
            "bg/tree.mdl", Placed.Position, Placed, visible: true, out var detail);

        Assert.Null(again);
        Assert.Contains("already borrowed", detail);
        Assert.Equal(1, world.Service.Count);

        // The one claim still owes the map its original placement.
        world.Service.Release(first);
        Assert.Equal(Placed, world.Port.PlacementOf(address));
        Assert.Equal((byte)0x21, world.Port.FlagsOf(address));
    }

    [Fact]
    public void An_entry_naming_no_model_is_refused()
    {
        var world = new World();
        world.Port.Add("bg/tree.mdl", Placed);

        var adopted = world.Service.AdoptByIdentity(
            string.Empty, Placed.Position, Moved, visible: true, out var detail);

        Assert.Null(adopted);
        Assert.NotNull(detail);
        Assert.Equal(0, world.Port.Writes);
    }

    /// <summary>Every exit from the contract still applies to a claim a scene
    /// load made: it is the same claim, whichever surface took it.</summary>
    [Fact]
    public void Gpose_exit_restores_an_object_a_scene_load_borrowed()
    {
        var world = new World();
        var address = world.Port.Add("bg/tree.mdl", Placed, flags: 0x21, visible: true);
        world.Service.AdoptByIdentity(
            "bg/tree.mdl", Placed.Position, Moved, visible: false, out _);
        Assert.Equal(Moved, world.Port.PlacementOf(address));

        world.Events.Publish(new GPoseStateChangedEvent(false));

        Assert.Equal(Placed, world.Port.PlacementOf(address));
        Assert.Equal((byte)0x21, world.Port.FlagsOf(address));
        Assert.True(world.Port.VisibleOf(address));
        Assert.Equal(0, world.Service.Count);
    }

    // ── how a claim appears in the list ──────────────────────────────────

    /// <summary>The label is the model's own stem, not the path and not the
    /// address. The full path stays available as the object pane's detail.
    /// </summary>
    [Fact]
    public void A_claim_is_named_by_its_model_stem_and_not_its_path()
    {
        var world = new World();
        var address = world.Port.Add(
            "bg/ffxiv/fst_f1/twn/f1t2/bgparts/f1t2_a1_bals1.mdl", Placed);

        var adopted = world.Service.Adopt(address)!;

        Assert.Equal("f1t2_a1_bals1", adopted.Name);
        Assert.Equal(
            "bg/ffxiv/fst_f1/twn/f1t2/bgparts/f1t2_a1_bals1.mdl", adopted.Path);
    }

    /// <summary>A map stands dozens of copies of one model. Three identical
    /// rows in the tree is three rows the user cannot tell apart — so repeats
    /// are numbered, and the FIRST is not.</summary>
    [Fact]
    public void Repeats_of_one_model_are_numbered_and_the_first_is_not()
    {
        var world = new World();
        var first = world.Service.Adopt(world.Port.Add("bg/chair.mdl", Placed))!;
        var second = world.Service.Adopt(world.Port.Add("bg/chair.mdl", Placed))!;
        var third = world.Service.Adopt(world.Port.Add("bg/chair.mdl", Placed))!;

        Assert.Equal("chair", first.Name);
        Assert.Equal("chair 2", second.Name);
        Assert.Equal("chair 3", third.Name);
    }

    [Fact]
    public void A_different_model_starts_its_own_numbering()
    {
        var world = new World();
        world.Service.Adopt(world.Port.Add("bg/chair.mdl", Placed));
        var table = world.Service.Adopt(world.Port.Add("bg/table.mdl", Placed))!;

        Assert.Equal("table", table.Name);
    }

    /// <summary>The suffix is the lowest free one, not a running count: giving
    /// the middle of three back and borrowing another must reuse the gap rather
    /// than mint a name the list already shows.</summary>
    [Fact]
    public void Releasing_one_frees_its_number_for_the_next_claim()
    {
        var world = new World();
        world.Service.Adopt(world.Port.Add("bg/chair.mdl", Placed));
        var second = world.Service.Adopt(world.Port.Add("bg/chair.mdl", Placed))!;
        var third = world.Service.Adopt(world.Port.Add("bg/chair.mdl", Placed))!;
        Assert.Equal("chair 3", third.Name);

        world.Service.Release(second);
        var replacement =
            world.Service.Adopt(world.Port.Add("bg/chair.mdl", Placed))!;

        Assert.Equal("chair 2", replacement.Name);
        // And nothing in the list shares a name.
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var claim in world.Service.Adopted)
            Assert.True(names.Add(claim.Name), $"duplicate row name: {claim.Name}");
    }

    /// <summary>An object with no loaded model names itself by its address
    /// (Ktisis does the same, WorldObject.cs:32) — still a row, never a
    /// blank.</summary>
    [Fact]
    public void An_object_with_no_model_still_has_a_name()
    {
        var world = new World();
        var address = world.Port.Add(string.Empty, Placed);

        var adopted = world.Service.Adopt(address)!;

        Assert.False(string.IsNullOrWhiteSpace(adopted.Name));
    }

    // ── fixtures ─────────────────────────────────────────────────────────

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

        public bool IsAvailable => true;

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
