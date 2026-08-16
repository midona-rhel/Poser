using System;
using System.Collections.Generic;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using CSObject = FFXIVClientStructs.FFXIV.Client.Graphics.Scene.Object;
using CSWorld = FFXIVClientStructs.FFXIV.Client.Graphics.Scene.World;

namespace Poser.Game.WorldObjects;

/// <summary>
/// Reads and writes BG objects through the game's world-object graph. The
/// graph uses child and sibling links, including circular sibling rings, so
/// traversal tracks visited addresses and limits processing to
/// <see cref="MaxNodes"/>.
/// Native fields are accessed through FFXIVClientStructs; writes refresh the
/// render and culling state that depends on placement.
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
    private readonly List<nint> _lights = new();
    private readonly HashSet<nint> _visited = new();
    private readonly Stack<nint> _pending = new();

    public NativeWorldObjectPort(IPluginLog log) => _log = log;

    public bool IsAvailable => CSWorld.Instance() != null;

    public IReadOnlyList<WorldObjectRow> Enumerate()
    {
        Walk(wantLights: false);
        return _rows.Count == 0
            ? Array.Empty<WorldObjectRow>()
            : _rows.ToArray();
    }

    public IReadOnlyList<nint> EnumerateLights()
    {
        Walk(wantLights: true);
        return _lights.Count == 0
            ? Array.Empty<nint>()
            : _lights.ToArray();
    }

    /// <summary>Walks the graph once and collects either BG objects or lights.
    /// Keeping one traversal avoids a second read of the graph.</summary>
    private void Walk(bool wantLights)
    {
        _rows.Clear();
        _lights.Clear();
        _visited.Clear();
        _pending.Clear();
        try
        {
            var world = CSWorld.Instance();
            if (world == null)
                return;

            // The world root is a container, not a listing row.
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
                var type = node->GetObjectType();
                if (wantLights)
                {
                    if (type == ObjectType.Light)
                        _lights.Add(address);
                    continue;
                }
                if (type != ObjectType.BgObject)
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
            _lights.Clear();
        }
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
        // Placement alone does not update the dependent render and culling
        // state, so refresh both after writing the native transform.
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

    public bool TryReadOutline(nint address, out byte outline)
    {
        outline = WorldObjectOutline.None;
        var node = Resolve(address);
        if (node == null)
            return false;
        outline = ((DrawObject*)node)->OutlineFlags;
        return true;
    }

    public void WriteOutline(nint address, byte outline)
    {
        var node = Resolve(address);
        if (node == null)
            return;
        // Outline is an independent field; changing it does not require a
        // placement refresh.
        ((DrawObject*)node)->OutlineFlags = outline;
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
        catch (Exception ex)
        {
            // Answering null is right — an address that cannot be read is not
            // written — but doing it silently made every read and write past
            // this point a no-op nobody could account for.
            _log.Warning(
                $"NativeWorldObjectPort: {address:X} could not be resolved: {ex.Message}");
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
        // Use the address when no model resource can provide a path, so an
        // object without a loaded model still has an identifiable row.
        string path = address.ToString("X");
        try
        {
            var resource = bg->ModelResourceHandle;
            if (resource != null)
                path = resource->FileName.ToString();
        }
        catch (Exception ex)
        {
            // A half-loaded resource names itself by address; it is still a
            // legitimate row — but the fallback is stated rather than assumed,
            // so a listing full of hex names has a reason in the log.
            _log.Debug(
                $"NativeWorldObjectPort: {address:X} has no readable model name: {ex.Message}");
        }
        var node = (CSObject*)bg;
        return new WorldObjectRow(
            address,
            path,
            new Transform(node->Position, node->Rotation, node->Scale),
            ((DrawObject*)node)->Flags);
    }
}
