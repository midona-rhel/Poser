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
using Poser.Game.Natives;

namespace Poser.Game.WorldObjects;

/// <summary>BG-model native state: dye, day/night dressing, animation speed
/// and the instance tail. Furniture layouts route to their driver.</summary>
internal sealed unsafe class BgObjectNative
{
    private readonly FurnitureLayoutDriver _furniture;
    private readonly WorldNodeResolver _resolver;

    public BgObjectNative(FurnitureLayoutDriver furniture, WorldNodeResolver resolver)
    {
        _furniture = furniture;
        _resolver = resolver;
    }

    private CSObject* Resolve(nint address) => _resolver.Resolve(address);

    /// <summary>Whether the BG object's model has fully streamed in —
    /// LoadState 7, the only state the render refreshes are safe in.
    /// </summary>
    internal static bool RenderReady(BgObject* bg)
    {
        var resource = bg->ModelResourceHandle;
        return resource != null && (byte)resource->LoadState == 7;
    }

    /// <summary>Writes the stain buffer; false means the buffer is unavailable.
    /// Converts each normalized channel to a square-root byte value as used by
    /// Stagehand, with full alpha. Null writes white to clear the tint.</summary>
    public bool WriteBgTint(nint address, System.Numerics.Vector3? tint)
    {
        if (_furniture.Contains(address)) return _furniture.SetTint(address, tint);
        var node = Resolve(address);
        if (node == null || node->GetObjectType() == ObjectType.VfxObject)
            return true;
        var bg = (BgObject*)node;
        if (bg->StainBuffer == null)
            return false;
        var stated = tint ?? System.Numerics.Vector3.One;
        var color = new FFXIVClientStructs.FFXIV.Client.Graphics.ByteColor
        {
            R = (byte)(Math.Sqrt(Math.Clamp(stated.X, 0f, 1f)) * 255f),
            G = (byte)(Math.Sqrt(Math.Clamp(stated.Y, 0f, 1f)) * 255f),
            B = (byte)(Math.Sqrt(Math.Clamp(stated.Z, 0f, 1f)) * 255f),
            A = byte.MaxValue,
        };
        return bg->TrySetStainColor(color);
    }

    /// <summary>Whether a BG object's model has fully streamed in — the
    /// moment its bytes are worth dumping.</summary>
    public bool IsBgReady(nint address)
    {
        if (_furniture.Contains(address)) return _furniture.Ready(address);
        var node = Resolve(address);
        return node != null
            && node->GetObjectType() != ObjectType.VfxObject
            && RenderReady((BgObject*)node);
    }

    /// <summary>Observed day/night byte at 0xCD: zero selects day; raw spawned
    /// models begin at 0xFF. Keep this separate from layout and light fields.</summary>
    private const int BgNightStateOffset = NativeFields.BgObject_NightState;

    /// <summary>Reports stain-buffer availability once the model has loaded.
    /// Models without a stain buffer cannot take this dye; null means still loading.</summary>
    public bool? CanDyeBg(nint address)
    {
        if (_furniture.Contains(address)) return _furniture.Ready(address) ? true : null;
        var node = Resolve(address);
        if (node == null || node->GetObjectType() == ObjectType.VfxObject)
            return false;
        var bg = (BgObject*)node;
        if (!RenderReady(bg))
            return null;
        return bg->StainBuffer != null;
    }

    /// <summary>Sets every Havok animation control on the BG object's
    /// render skeleton to the stated speed — the same lever the actor
    /// animation port pulls, reached through LoadedAnimationData. False
    /// until the skeleton and its controls exist (they stream in after
    /// the model, and a model without animation never grows them).
    /// </summary>
    public bool WriteBgAnimationSpeed(nint address, float speed)
    {
        var node = Resolve(address);
        if (node == null || node->GetObjectType() == ObjectType.VfxObject)
            return false;
        var bg = (BgObject*)node;
        var animation = bg->LoadedAnimationData;
        if (animation == null || animation->RenderSkeleton == null)
            return false;
        var skeleton = animation->RenderSkeleton;
        bool touched = false;
        for (int p = 0; p < skeleton->PartialSkeletonCount; p++)
        {
            var animated =
                skeleton->PartialSkeletons[p].GetHavokAnimatedSkeleton(0);
            if (animated == null)
                continue;
            for (int c = 0; c < animated->AnimationControls.Length; c++)
            {
                var control = animated->AnimationControls[c].Value;
                if (control == null)
                    continue;
                control->PlaybackSpeed = speed;
                touched = true;
            }
        }
        return touched;
    }

    /// <summary>The whole undocumented tail (0xC0..0xE0) in one read —
    /// the pause hold freezes it beside the transform, because part of
    /// it is the instance's own animation clock (it visibly counts up)
    /// and a held transform with a running clock JUMPS on unpause.
    /// </summary>
    public bool TryReadBgTail(nint address, byte[] into)
    {
        var node = Resolve(address);
        if (node == null || node->GetObjectType() == ObjectType.VfxObject
            || into.Length < 0x20)
            return false;
        for (int i = 0; i < 0x20; i++)
            into[i] = *((byte*)node + NativeFields.BgObject_Tail + i);
        return true;
    }

    /// <summary>Writes a captured tail back, skipping the DOCUMENTED
    /// bytes (night state, colour intensity, colour) so a held pause
    /// never overwrites a choice the user makes meanwhile.</summary>
    public void WriteBgTailHeld(nint address, byte[] values)
    {
        var node = Resolve(address);
        if (node == null || node->GetObjectType() == ObjectType.VfxObject
            || values.Length < 0x20)
            return;
        for (int i = 0; i < 0x20; i++)
        {
            int offset = 0xC0 + i;
            // Leave draw state (0xC0..0xC3), shadow index (0xC4..0xC7), and
            // shadow state (0xCC) under game control. Restoring unloaded sentinel
            // values here has hidden objects and crashed the shadow pass. Also
            // preserve editable night, intensity, and colour fields.
            if (offset is (>= 0xC0 and <= 0xC7) or 0xCC or 0xCD or 0xCE or (>= 0xD0 and <= 0xD3))
                continue;
            *((byte*)node + offset) = values[i];
        }
    }

    public bool? ReadBgNightState(nint address)
    {
        if (_furniture.Contains(address)) return _furniture.NightState(address);
        var node = Resolve(address);
        if (node == null || node->GetObjectType() == ObjectType.VfxObject)
            return null;
        return *((byte*)node + BgNightStateOffset) != 0;
    }

    public void WriteBgNightState(nint address, bool night)
    {
        if (_furniture.Contains(address)) { _furniture.SetNightState(address, night); return; }
        var node = Resolve(address);
        if (node == null || node->GetObjectType() == ObjectType.VfxObject)
            return;
        WriteModelNightState((BgObject*)node, night);
    }

    internal static void WriteModelNightState(BgObject* bg, bool night)
    {
        // The byte belongs to a BG graphics object, including a furniture
        // child model. It is never a field of its owning SGL or a light node.
        *((byte*)bg + BgNightStateOffset) = night ? byte.MaxValue : (byte)0;
        if (RenderReady(bg))
        {
            bg->UpdateCulling();
            bg->UpdateTransforms(false);
        }
    }
}
