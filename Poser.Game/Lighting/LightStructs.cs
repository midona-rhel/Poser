using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using FFXIVClientStructs.FFXIV.Client.System.Resource.Handle;
using NativeAABounds = FFXIVClientStructs.FFXIV.Common.Math.AxisAlignedBounds;
using NativeTransform = FFXIVClientStructs.FFXIV.Client.Graphics.Transform;

namespace Poser.Game.Lighting;

/// <summary>
/// Native scene light. The game's light factory allocates this object; the
/// plugin never allocates or frees it itself — <see cref="Destroy"/> hands it
/// back through the object's own virtual table.
/// </summary>
[StructLayout(LayoutKind.Explicit, Size = 0xB0)]
public unsafe struct GameLight
{
    [StructLayout(LayoutKind.Explicit)]
    public struct GameLightVirtualTable
    {
        [FieldOffset(0x00)]
        public delegate* unmanaged<GameLight*, bool, nint> Destructor;

        [FieldOffset(0x08)]
        public delegate* unmanaged<GameLight*, void> Cleanup;
    }

    [FieldOffset(0x00)] public GameLightVirtualTable* VirtualTable;

    [FieldOffset(0x00)] public DrawObject DrawObject;

    [FieldOffset(0x50)] public NativeTransform Transform;

    /// <summary>0 = off; 79 is the value the game's own GPose light toggle
    /// writes when turning a light on.</summary>
    [FieldOffset(0x88)] public byte VisibilityFlags;

    [FieldOffset(0x90)] public LightRenderObject* LightRenderObject;

    // Present in the layout for completeness; no reference implementation
    // writes this; inert.
    [FieldOffset(0x98)] public TextureResourceHandle* ProjectedCubemapTexture;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Update()
    {
        UpdateCulling();
        UpdateMaterials();
    }

    /// <summary>Cleanup then destructor with free = TRUE: the game allocated
    /// the object, so the game frees it.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Destroy()
    {
        VirtualTable->Cleanup((GameLight*)Unsafe.AsPointer(ref this));
        VirtualTable->Destructor((GameLight*)Unsafe.AsPointer(ref this), true);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void UpdateCulling()
    {
        DrawObject.VirtualTable->UpdateCulling((DrawObject*)Unsafe.AsPointer(in this));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void UpdateMaterials()
    {
        DrawObject.VirtualTable->UpdateMaterials((DrawObject*)Unsafe.AsPointer(in this));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void UpdateTransforms(bool unk)
    {
        DrawObject.VirtualTable->UpdateTransforms((DrawObject*)Unsafe.AsPointer(in this), unk);
    }
}

/// <summary>Render-side properties of a <see cref="GameLight"/>.</summary>
[StructLayout(LayoutKind.Explicit, Size = 0x160)]
public unsafe struct LightRenderObject
{
    [FieldOffset(0x18)] public LightFlags LightFlags;
    [FieldOffset(0x1C)] public LightType EmissionType;
    [FieldOffset(0x20)] public NativeTransform* Transform;
    [FieldOffset(0x28)] public Vector4 ColorIntensity;
    [FieldOffset(0x40)] public NativeAABounds MaxRange;
    [FieldOffset(0x60)] public float ShadowPlaneNear;
    [FieldOffset(0x64)] public float ShadowPlaneFar;
    [FieldOffset(0x68)] public FalloffType FalloffType;
    [FieldOffset(0x70)] public Vector2 AreaAngle;
    [FieldOffset(0x80)] public float Falloff;
    [FieldOffset(0x84)] public float LightAngle;
    [FieldOffset(0x88)] public float FalloffAngle;
    [FieldOffset(0x8C)] public float Range;
    [FieldOffset(0x90)] public float CharacterShadowRange;

    [FieldOffset(0xA0)] public NativeAABounds CullingBounds;
    [FieldOffset(0xC0)] public NativeAABounds RangeBounds;

    public Vector3 Color
    {
        readonly get => new(ColorIntensity.X, ColorIntensity.Y, ColorIntensity.Z);
        set => ColorIntensity = new Vector4(value, ColorIntensity.W);
    }

    public float Intensity
    {
        readonly get => ColorIntensity.W;
        set => ColorIntensity.W = value;
    }
}

[Flags]
public enum LightFlags
{
    None = 0,
    Reflection = 1,
    Dynamic = 2,
    CharaShadow = 4,
    ObjectShadow = 8,
}

public enum LightType : uint
{
    WorldLight = 1,
    PointLight = 2,
    SpotLight = 3,
    FlatLight = 4,
}

public enum FalloffType : uint
{
    Linear = 0,
    Quadratic = 1,
    Cubic = 2,
}

