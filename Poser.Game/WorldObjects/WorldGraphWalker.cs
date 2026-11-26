using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using Dalamud.Game;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using FFXIVClientStructs.FFXIV.Client.System.Resource.Handle;
using Poser.Application.World;
using Poser.Domain.Scene;
using Poser.Domain.Transforms;
using CSObject = FFXIVClientStructs.FFXIV.Client.Graphics.Scene.Object;
using CSVfx = FFXIVClientStructs.FFXIV.Client.Graphics.Scene.VfxObject;
using CSWorld = FFXIVClientStructs.FFXIV.Client.Graphics.Scene.World;

namespace Poser.Game.WorldObjects;

/// <summary>
/// Walks the game's tree of world objects. Each object links to its first
/// child and to its next sibling, and the siblings form a ring, so the walk
/// remembers every address it has seen. It stops after <see cref="MaxNodes"/>
/// objects, so a broken link in game memory can't keep it reading forever.
/// </summary>
internal sealed unsafe class WorldGraphWalker
{
    /// <summary>The most objects one walk reads. Checked for every object
    /// found, including while a sibling ring is being collected.</summary>
    private const int MaxNodes = 100_000;

    private readonly IPluginLog _log;
    private readonly FurnitureLayoutDriver _furniture;
    private readonly Action<HashSet<nint>> _retainPresent;
    private readonly List<WorldObjectRow> _rows = new();
    private readonly List<nint> _lights = new();
    private readonly HashSet<nint> _visited = new();
    private readonly Stack<nint> _pending = new();

    /// <param name="retainPresent">Called after every walk that wasn't cut
    /// off, with every address it reached, so Poser can forget objects that
    /// are gone.</param>
    public WorldGraphWalker(
        IPluginLog log,
        FurnitureLayoutDriver furniture,
        Action<HashSet<nint>> retainPresent)
    {
        _log = log;
        _furniture = furniture;
        _retainPresent = retainPresent;
    }

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

    /// <summary>One walk, collecting either the map objects and effects or
    /// the lights.</summary>
    private void Walk(bool wantLights)
    {
        _furniture.RefreshGraphics();
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

            while (_pending.Count > 0)
            {
                var address = _pending.Pop();
                var node = (CSObject*)address;
                PushRing(node->ChildObject);
                // These belong to a piece of furniture, not to the map. Taking
                // one over would leave Poser holding it after the furniture is gone.
                if (_furniture.OwnsGraphics(address)) continue;
                var type = node->GetObjectType();
                if (wantLights)
                {
                    if (type == ObjectType.Light)
                        _lights.Add(address);
                    continue;
                }
                if (type == ObjectType.BgObject)
                    _rows.Add(ReadRow(address, (BgObject*)node));
                else if (type == ObjectType.VfxObject)
                    // Effects in the world, such as a bonfire's flame, are
                    // listed and taken over the same way as map objects.
                    _rows.Add(ReadVfxRow(address, (CSVfx*)node));
            }

            // A cut-off walk didn't see every object, so it can't tell which
            // remembered objects are gone.
            if (_visited.Count >= MaxNodes)
                _log.Warning(
                    "NativeWorldObjectPort: the world walk hit its node cap; "
                    + "the listing is truncated.");
            else
                _retainPresent(_visited);
        }
        catch (Exception ex)
        {
            // A walk that fails lists nothing instead of throwing into the
            // window that asked for the list.
            _log.Error($"NativeWorldObjectPort: walking the world failed: {ex.Message}");
            _rows.Clear();
            _lights.Clear();
        }
    }

    /// <summary>Adds one ring of siblings to the walk. The ring ends where it
    /// reaches an object already seen, or at the object limit.</summary>
    private void PushRing(CSObject* first)
    {
        var cursor = first;
        while (cursor != null && _visited.Count < MaxNodes && _visited.Add((nint)cursor))
        {
            _pending.Push((nint)cursor);
            cursor = cursor->NextSiblingObject;
        }
    }

    /// <summary>A world effect's row: the .avfx path read through the
    /// resource chain (instance → resource object → apricot handle), the
    /// address as ever when nothing is readable yet.</summary>
    private WorldObjectRow ReadVfxRow(nint address, CSVfx* vfx)
    {
        string path = address.ToString("X");
        try
        {
            var instance = vfx->VfxResourceInstance;
            if (instance != null
                && instance->VfxResourceObject != null
                && instance->VfxResourceObject->ApricotResourceHandle != null)
                path = instance->VfxResourceObject->ApricotResourceHandle
                    ->FileName.ToString();
        }
        catch (Exception ex)
        {
            _log.Debug(
                $"NativeWorldObjectPort: {address:X} has no readable effect name: {ex.Message}");
        }
        var node = (CSObject*)vfx;
        return new WorldObjectRow(
            address,
            path,
            new Transform(node->Position, node->Rotation, node->Scale),
            ((DrawObject*)node)->Flags,
            IsEffect: true);
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
