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
using CSObject = FFXIVClientStructs.FFXIV.Client.Graphics.Scene.Object;
using CSVfx = FFXIVClientStructs.FFXIV.Client.Graphics.Scene.VfxObject;
using CSWorld = FFXIVClientStructs.FFXIV.Client.Graphics.Scene.World;

namespace Poser.Game.WorldObjects;

/// <summary>The one address check every native world-object read and write
/// goes through. Furniture layouts are addressed through their driver, never
/// as a raw graph node.</summary>
internal sealed unsafe class WorldNodeResolver
{
    private readonly FurnitureLayoutDriver _furniture;
    private readonly IPluginLog _log;

    public WorldNodeResolver(FurnitureLayoutDriver furniture, IPluginLog log)
    {
        _furniture = furniture;
        _log = log;
    }

    /// <summary>The one address check every read and write goes through: a
    /// non-null pointer that still answers BgObject. An address that has
    /// stopped being one is inert rather than written blind.</summary>
    public CSObject* Resolve(nint address)
    {
        if (address == nint.Zero || _furniture.Contains(address))
            return null;
        try
        {
            var node = (CSObject*)address;
            var type = node->GetObjectType();
            return type is ObjectType.BgObject or ObjectType.VfxObject
                ? node
                : null;
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
}
