using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using StructsTransforms = FFXIVClientStructs.FFXIV.Client.Graphics.Transform;

namespace Poser.Game.Structs;

/// <summary>
/// Native game light structure.
/// Based on Brio's implementation.
/// </summary>
[StructLayout(LayoutKind.Explicit, Size = 0xA0)]
public unsafe struct GameLight
{
    [FieldOffset(0x00)] public GameLightVirtualTable* VirtualTable;
    [FieldOffset(0x00)] public DrawObject DrawObject;
    [FieldOffset(0x50)] public StructsTransforms Transform;
    [FieldOffset(0x88)] public byte LightFlags; // 0 = off, 79 = on
    [FieldOffset(0x90)] public LightRenderObject* LightRenderObject;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public unsafe void Destroy()
    {
        VirtualTable->Cleanup((GameLight*)Unsafe.AsPointer(ref this));
        VirtualTable->Destructor((GameLight*)Unsafe.AsPointer(ref this), false);
    }
}

/// <summary>
/// Virtual table for GameLight destruction.
/// </summary>
[StructLayout(LayoutKind.Explicit)]
public unsafe struct GameLightVirtualTable
{
    [FieldOffset(0)]
    public delegate* unmanaged<GameLight*, bool, void> Destructor;

    [FieldOffset(8)]
    public delegate* unmanaged<GameLight*, void> Cleanup;
}

/// <summary>
/// Light render properties.
/// </summary>
[StructLayout(LayoutKind.Explicit, Size = 0xA0)]
public unsafe struct LightRenderObject
{
    [FieldOffset(0x00)] public nint* VirtualTable;
    [FieldOffset(0x18)] public LightFlags LightFlags;
    [FieldOffset(0x1C)] public LightType EmissionType;
    [FieldOffset(0x20)] public StructsTransforms* Transform;
    [FieldOffset(0x28)] public Vector3 Color;
    [FieldOffset(0x34)] public float Intensity;
    [FieldOffset(0x40)] public Vector3 MaxRangeNegative;
    [FieldOffset(0x50)] public Vector3 MaxRangePositive;
    [FieldOffset(0x60)] public float ShadowPlaneNear;
    [FieldOffset(0x64)] public float ShadowPlaneFar;
    [FieldOffset(0x68)] public FalloffType FalloffType;
    [FieldOffset(0x70)] public Vector2 Angle;
    [FieldOffset(0x80)] public float Falloff;
    [FieldOffset(0x84)] public float LightAngle;
    [FieldOffset(0x88)] public float FalloffAngle;
    [FieldOffset(0x8C)] public float Range;
    [FieldOffset(0x90)] public float CharacterShadowRange;
}

/// <summary>
/// Light type enumeration.
/// </summary>
public enum LightType : uint
{
    WorldLight = 1,
    AreaLight = 2,  // Point light
    SpotLight = 3,
    FlatLight = 4
}

/// <summary>
/// Light flags for rendering behavior.
/// </summary>
[Flags]
public enum LightFlags
{
    None = 0,
    Reflection = 1,
    Dynamic = 2,
    CharaShadow = 4,
    ObjectShadow = 8
}

/// <summary>
/// Falloff type for light intensity.
/// </summary>
public enum FalloffType : uint
{
    Linear = 0,
    Quadratic = 1,
    Cubic = 2
}
