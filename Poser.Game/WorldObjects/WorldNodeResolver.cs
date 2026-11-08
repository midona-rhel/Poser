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

/// <summary>Validates native world-object addresses before use. Furniture
/// layouts are handled by their driver rather than as raw graph nodes.</summary>
internal sealed unsafe class WorldNodeResolver
{
    private readonly FurnitureLayoutDriver _furniture;
    private readonly IPluginLog _log;

    public WorldNodeResolver(FurnitureLayoutDriver furniture, IPluginLog log)
    {
        _furniture = furniture;
        _log = log;
    }

    /// <summary>Returns a currently readable background or VFX object, or null.</summary>
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
            // Refuse the write and retain a diagnostic for the unreadable address.
            _log.Warning(
                $"NativeWorldObjectPort: {address:X} could not be resolved: {ex.Message}");
            return null;
        }
    }
}
