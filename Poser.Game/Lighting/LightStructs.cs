using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using FFXIVClientStructs.FFXIV.Client.System.Resource.Handle;
using NativeAABounds = FFXIVClientStructs.FFXIV.Common.Math.AxisAlignedBounds;
using NativeTransform = FFXIVClientStructs.FFXIV.Client.Graphics.Transform;
using Poser.Game.Natives;

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
        [FieldOffset(NativeFields.GameLightVtbl_Destructor)]
        public delegate* unmanaged<GameLight*, bool, nint> Destructor;

        [FieldOffset(NativeFields.GameLightVtbl_Cleanup)]
        public delegate* unmanaged<GameLight*, void> Cleanup;
    }

    [FieldOffset(NativeFields.GameLight_VirtualTable)] public GameLightVirtualTable* VirtualTable;

    [FieldOffset(NativeFields.GameLight_DrawObject)] public DrawObject DrawObject;

    [FieldOffset(NativeFields.GameLight_Transform)] public NativeTransform Transform;

    /// <summary>DrawObject flags (patchcheck GameLight.VisibilityFlags): the game
    /// draws and culls an object only with bits 0 (shown) and 6 both set; bit 2 is
    /// the render-state dirty bit. A fresh light has 0x4B.</summary>
    [FieldOffset(NativeFields.GameLight_VisibilityFlags)] public byte VisibilityFlags;

    private const byte DrawnBits = 0x41, ShownBit = 0x01;

    [FieldOffset(NativeFields.GameLight_LightRenderObject)] public LightRenderObject* LightRenderObject;

    /// <summary>Gobo texture handle. Written by the game's set-light-texture
    /// call; released with <c>DecRef</c> when the gobo is cleared.</summary>
    [FieldOffset(NativeFields.GameLight_ProjectedCubemapTexture)] public TextureResourceHandle* ProjectedCubemapTexture;

    /// <summary>Drawn as the game tests it, (+0x88 &amp; 0x41) == 0x41; hidden as
    /// ILayoutInstance::SetActive hides it, by its shown bit.</summary>
    public bool IsVisible
    {
        readonly get => (VisibilityFlags & DrawnBits) == DrawnBits;
        set => VisibilityFlags = value
            ? (byte)(VisibilityFlags | DrawnBits)
            : (byte)(VisibilityFlags & ~ShownBit);
    }

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

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void UpdateRender()
    {
        DrawObject.VirtualTable->UpdateRender((DrawObject*)Unsafe.AsPointer(in this));
    }
}

/// <summary>
/// The GPose camera-light controller (<c>EventGPoseController</c>). Only the
/// three camera-light slots are mapped; the same struct backs both the
/// event-framework instance and the toggle function's first argument.
/// </summary>
[StructLayout(LayoutKind.Explicit)]
public unsafe struct GPoseLightController
{
    public const int LightCount = 3;

    [FieldOffset(NativeFields.GPoseLightController_Lights)] public fixed ulong Lights[LightCount];

    public GameLight* GetLight(uint index) =>
        index < LightCount ? (GameLight*)Lights[index] : null;
}

/// <summary>Render-side properties of a <see cref="GameLight"/>; CreateLight
/// allocates 0x130 bytes for it.</summary>
[StructLayout(LayoutKind.Explicit, Size = 0x130)]
public unsafe struct LightRenderObject
{
    [FieldOffset(NativeFields.LightRenderObject_LightFlags)] public LightFlags LightFlags;
    [FieldOffset(NativeFields.LightRenderObject_EmissionType)] public LightType EmissionType;
    [FieldOffset(NativeFields.LightRenderObject_Transform)] public NativeTransform* Transform;
    [FieldOffset(NativeFields.LightRenderObject_ColorIntensity)] public Vector4 ColorIntensity;
    [FieldOffset(NativeFields.LightRenderObject_MaxRange)] public NativeAABounds MaxRange;
    [FieldOffset(NativeFields.LightRenderObject_ShadowPlaneNear)] public float ShadowPlaneNear;
    [FieldOffset(NativeFields.LightRenderObject_ShadowPlaneFar)] public float ShadowPlaneFar;
    [FieldOffset(NativeFields.LightRenderObject_FalloffType)] public FalloffType FalloffType;
    [FieldOffset(NativeFields.LightRenderObject_AreaAngle)] public Vector2 AreaAngle;
    [FieldOffset(NativeFields.LightRenderObject_Falloff)] public float Falloff;
    [FieldOffset(NativeFields.LightRenderObject_LightAngle)] public float LightAngle;
    [FieldOffset(NativeFields.LightRenderObject_FalloffAngle)] public float FalloffAngle;
    [FieldOffset(NativeFields.LightRenderObject_Range)] public float Range;
    [FieldOffset(NativeFields.LightRenderObject_CharacterShadowRange)] public float CharacterShadowRange;

    [FieldOffset(NativeFields.LightRenderObject_CullingBounds)] public NativeAABounds CullingBounds;
    [FieldOffset(NativeFields.LightRenderObject_RangeBounds)] public NativeAABounds RangeBounds;

    /// <summary>Render-side copy of the gobo texture handle. Cleared to null
    /// alongside the scene light's own handle.</summary>
    [FieldOffset(NativeFields.LightRenderObject_Texture)] public void* Texture;

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

