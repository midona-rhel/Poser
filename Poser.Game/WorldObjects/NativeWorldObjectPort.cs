using System;
using System.Collections.Generic;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using CSObject = FFXIVClientStructs.FFXIV.Client.Graphics.Scene.Object;
using CSWorld = FFXIVClientStructs.FFXIV.Client.Graphics.Scene.World;

namespace Poser.Game.WorldObjects;

/// <summary>
/// The one implementation of <see cref="IWorldObjectPort"/> that touches the
/// game: <c>World.Instance()</c>'s object graph, walked for its BG objects,
/// and their transforms read and written in place.
///
/// <para>GAME-VERSION FRAGILITY, stated once: this seam depends on the shape
/// of the graph (an object's first child and its sibling ring) and on three
/// members of a BG object — its model resource handle for the name, its flags
/// byte, and the render/culling re-state calls. It uses no signature scan and
/// no hardcoded offset; every member is one FFXIVClientStructs names, so a
/// patch that moves them breaks the BUILD rather than the game. Brio reaches
/// the same object through a hand-written struct with literal offsets
/// (<c>Brio/Game/WorldObjects/Interop/BgObjectEx.cs:12-24</c> — Flags at 0x38,
/// the resource handle at 144), which is the shape this deliberately does not
/// copy: a moved offset there is a silent wrong read.</para>
///
/// <para>The walk is Ktisis' own
/// (<c>Ktisis/Services/Game/WorldService.cs:46-67</c> with
/// <c>Structs/Objects/WorldObject.cs:82-104</c>): the world root's siblings and
/// children, each recursed. The sibling ring is circular and the graph is not
/// guaranteed acyclic, so this walk carries an explicit visited set and a hard
/// node cap instead of Ktisis' pairwise address comparisons — the same
/// traversal, terminating by construction.</para>
/// </summary>
public sealed unsafe class NativeWorldObjectPort : IWorldObjectPort
{
    /// <summary>The hard stop on one walk. A zone's graph is thousands of
    /// nodes, not hundreds of thousands; a count this high can only mean the
    /// walk is following something that is not the graph it was told about.
    /// </summary>
    private const int MaxNodes = 100_000;

    private readonly IPluginLog _log;
    private readonly List<WorldObjectRow> _rows = new();
    private readonly HashSet<nint> _visited = new();
    private readonly Stack<nint> _pending = new();

    public NativeWorldObjectPort(IPluginLog log) => _log = log;

    public bool IsAvailable => CSWorld.Instance() != null;

    public IReadOnlyList<WorldObjectRow> Enumerate()
    {
        _rows.Clear();
        _visited.Clear();
        _pending.Clear();
        try
        {
            var world = CSWorld.Instance();
            if (world == null)
                return Array.Empty<WorldObjectRow>();

            // The root itself is not a listing row — Ktisis says the same in
            // as many words (WorldService.cs:49, "don't include World root").
            var root = &world->Object;
            _visited.Add((nint)root);
            PushRing(root->ChildObject);
            PushRing(root->NextSiblingObject);

            while (_pending.Count > 0 && _visited.Count < MaxNodes)
            {
                var address = _pending.Pop();
                var node = (CSObject*)address;
                if (node == null)
                    continue;
                PushRing(node->ChildObject);
                if (node->GetObjectType() != ObjectType.BgObject)
                    continue;
                _rows.Add(ReadRow(address, (BgObject*)node));
            }

            if (_visited.Count >= MaxNodes)
                _log.Warning(
                    "NativeWorldObjectPort: the world walk hit its node cap; "
                    + "the listing is truncated.");
        }
        catch (Exception ex)
        {
            // A graph that cannot be walked is an empty listing, never a
            // throw into the overlay's draw.
            _log.Error($"NativeWorldObjectPort: walking the world failed: {ex.Message}");
            _rows.Clear();
        }
        return _rows.Count == 0
            ? Array.Empty<WorldObjectRow>()
            : _rows.ToArray();
    }

    public bool IsAlive(nint address) => Resolve(address) != null;

    public bool TryRead(nint address, out Transform placement)
    {
        placement = Transform.Identity;
        var node = Resolve(address);
        if (node == null)
            return false;
        placement = new Transform(node->Position, node->Rotation, node->Scale);
        return true;
    }

    public void Write(nint address, in Transform placement)
    {
        var node = Resolve(address);
        if (node == null)
            return;
        node->Position = placement.Position;
        node->Rotation = placement.Rotation;
        node->Scale = placement.Scale;
        // Ktisis' WorldObject.Update() (Structs/Objects/WorldObject.cs:44-51):
        // the placement alone does not move what is DRAWN — the render state
        // and the BG object's culling volume both hang off it, and Brio
        // re-states the same two after every write (BGOObject.SetTransform,
        // Brio/Game/WorldObjects/Objects/BGOObject.cs:92-93).
        node->UpdateRender();
        ((BgObject*)node)->UpdateCulling();
    }

    public bool TryReadFlags(nint address, out byte flags)
    {
        flags = 0;
        var node = Resolve(address);
        if (node == null)
            return false;
        flags = ((DrawObject*)node)->Flags;
        return true;
    }

    public void WriteFlags(nint address, byte flags)
    {
        var node = Resolve(address);
        if (node == null)
            return;
        ((DrawObject*)node)->Flags = flags;
    }

    public bool TryReadVisible(nint address, out bool visible)
    {
        visible = false;
        var node = Resolve(address);
        if (node == null)
            return false;
        visible = ((DrawObject*)node)->IsVisible;
        return true;
    }

    public void WriteVisible(nint address, bool visible)
    {
        var node = Resolve(address);
        if (node == null)
            return;
        ((DrawObject*)node)->IsVisible = visible;
    }

    /// <summary>The one address check every read and write goes through: a
    /// non-null pointer that still answers BgObject. An address that has
    /// stopped being one is inert rather than written blind.</summary>
    private CSObject* Resolve(nint address)
    {
        if (address == nint.Zero)
            return null;
        try
        {
            var node = (CSObject*)address;
            return node->GetObjectType() == ObjectType.BgObject ? node : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Pushes one sibling ring, stopping at the first node already
    /// seen. The ring is circular, so "already seen" is its terminator.
    /// </summary>
    private void PushRing(CSObject* first)
    {
        var cursor = first;
        while (cursor != null && _visited.Add((nint)cursor))
        {
            _pending.Push((nint)cursor);
            cursor = cursor->NextSiblingObject;
        }
    }

    private WorldObjectRow ReadRow(nint address, BgObject* bg)
    {
        // Ktisis defaults the path to the address and overwrites it from the
        // model resource when there is one (WorldObject.cs:32, :38-40), so an
        // object with no loaded model still has a name that identifies it.
        string path = address.ToString("X");
        try
        {
            var resource = bg->ModelResourceHandle;
            if (resource != null)
                path = resource->FileName.ToString();
        }
        catch (Exception)
        {
            // A half-loaded resource names itself by address; it is still a
            // legitimate row.
        }
        var node = (CSObject*)bg;
        return new WorldObjectRow(
            address,
            path,
            new Transform(node->Position, node->Rotation, node->Scale),
            ((DrawObject*)node)->Flags);
    }
}
