using System.Numerics;
using System.Runtime.InteropServices;
using Poser.Game.Natives;

namespace Poser.Game.Environment;

// The game's environment state, laid out exactly as Ktisis mapped it and Brio
// re-verified (Ktisis Structs/Env/EnvState.cs + Structs/Env/Weather/*.cs;
// Brio Game/World/BrioEnvManager.cs). ClientStructs names the containing field
// — EnvManager.EnvState at 0x058, struct size 0x2F8 — but not its members, so
// the members live here and the CONTAINER is always reached through the
// ClientStructs symbol, never through a hand-counted offset.
//
// These types are internal on purpose: pointers and layouts stay inside
// Poser.Game, and the service converts to the plain records the interface uses.

[StructLayout(LayoutKind.Explicit, Size = 0x2F8)]
internal struct EnvStateNative
{
    [FieldOffset(NativeFields.EnvStateNative_SkyTextureId)] public uint SkyTextureId;
    [FieldOffset(NativeFields.EnvStateNative_Lighting)] public EnvLightingNative Lighting;
    [FieldOffset(NativeFields.EnvStateNative_Stars)] public EnvStarsNative Stars;
    [FieldOffset(NativeFields.EnvStateNative_Fog)] public EnvFogNative Fog;
    [FieldOffset(NativeFields.EnvStateNative_Clouds)] public EnvCloudsNative Clouds;
    [FieldOffset(NativeFields.EnvStateNative_Rain)] public EnvRainNative Rain;
    [FieldOffset(NativeFields.EnvStateNative_Particles)] public EnvParticlesNative Particles;
    [FieldOffset(NativeFields.EnvStateNative_Wind)] public EnvWindNative Wind;
}

// The game's section is 0x44 bytes (patchcheck EnvState.Lighting); a held section
// is copied whole, so the size is the game's.
[StructLayout(LayoutKind.Explicit, Size = 0x44)]
internal struct EnvLightingNative
{
    [FieldOffset(NativeFields.EnvLightingNative_SunlightColor)] public Vector3 SunlightColor;
    [FieldOffset(NativeFields.EnvLightingNative_MoonlightColor)] public Vector3 MoonlightColor;
    [FieldOffset(NativeFields.EnvLightingNative_AmbientColor)] public Vector3 AmbientColor;
    [FieldOffset(NativeFields.EnvLightingNative_Unknown1)] public float Unknown1;
    [FieldOffset(NativeFields.EnvLightingNative_AmbientSaturation)] public float AmbientSaturation;
    [FieldOffset(NativeFields.EnvLightingNative_AmbientTemperature)] public float AmbientTemperature;
    [FieldOffset(NativeFields.EnvLightingNative_Unknown2)] public float Unknown2;      // shadow colour, unconfirmed
    [FieldOffset(NativeFields.EnvLightingNative_LightDistance)] public float LightDistance; // world vignette off the camera
    [FieldOffset(NativeFields.EnvLightingNative_Unknown4)] public float Unknown4;
}

[StructLayout(LayoutKind.Explicit, Size = 0x28)]
internal struct EnvStarsNative
{
    [FieldOffset(NativeFields.EnvStarsNative_ConstellationIntensity)] public float ConstellationIntensity;
    [FieldOffset(NativeFields.EnvStarsNative_ConstellationCount)] public float ConstellationCount;
    [FieldOffset(NativeFields.EnvStarsNative_StarCount)] public float StarCount;
    [FieldOffset(NativeFields.EnvStarsNative_GalaxyIntensity)] public float GalaxyIntensity;
    [FieldOffset(NativeFields.EnvStarsNative_StarIntensity)] public float StarIntensity;
    [FieldOffset(NativeFields.EnvStarsNative_MoonColor)] public Vector4 MoonColor;
    [FieldOffset(NativeFields.EnvStarsNative_MoonBrightness)] public float MoonBrightness;
}

// The game's section is 0x68 bytes (patchcheck EnvState.Fog); the unmapped tail is
// held with it.
[StructLayout(LayoutKind.Explicit, Size = 0x68)]
internal struct EnvFogNative
{
    [FieldOffset(NativeFields.EnvFogNative_Color)] public Vector4 Color;
    [FieldOffset(NativeFields.EnvFogNative_Distance)] public float Distance;
    [FieldOffset(NativeFields.EnvFogNative_Thickness)] public float Thickness;
    [FieldOffset(NativeFields.EnvFogNative_SkySmoothness)] public float SkySmoothness;
    [FieldOffset(NativeFields.EnvFogNative_SkyOpacity)] public float SkyOpacity;
    [FieldOffset(NativeFields.EnvFogNative_FogOpacity)] public float FogOpacity;
    [FieldOffset(NativeFields.EnvFogNative_SunVisibility)] public float SunVisibility;
}

[StructLayout(LayoutKind.Explicit, Size = 0x28)]
internal struct EnvCloudsNative
{
    [FieldOffset(NativeFields.EnvCloudsNative_CloudColor1)] public Vector3 CloudColor1;
    [FieldOffset(NativeFields.EnvCloudsNative_CloudColor2)] public Vector3 CloudColor2;
    [FieldOffset(NativeFields.EnvCloudsNative_ShadowStop)] public float ShadowStop;
    [FieldOffset(NativeFields.EnvCloudsNative_CloudHeight)] public float CloudHeight;
    [FieldOffset(NativeFields.EnvCloudsNative_CloudTexture)] public uint CloudTexture;
    [FieldOffset(NativeFields.EnvCloudsNative_CloudSideTexture)] public uint CloudSideTexture;
}

[StructLayout(LayoutKind.Explicit, Size = 0x34)]
internal struct EnvRainNative
{
    [FieldOffset(NativeFields.EnvRainNative_Raindrops)] public float Raindrops;
    [FieldOffset(NativeFields.EnvRainNative_Intensity)] public float Intensity;
    [FieldOffset(NativeFields.EnvRainNative_Weight)] public float Weight;
    [FieldOffset(NativeFields.EnvRainNative_Scatter)] public float Scatter;
    [FieldOffset(NativeFields.EnvRainNative_Unknown1)] public float Unknown1;
    [FieldOffset(NativeFields.EnvRainNative_Size)] public float Size;
    [FieldOffset(NativeFields.EnvRainNative_Color)] public Vector4 Color;
    [FieldOffset(NativeFields.EnvRainNative_Unknown2)] public float Unknown2;
    [FieldOffset(NativeFields.EnvRainNative_Unknown3)] public float Unknown3;
    [FieldOffset(NativeFields.EnvRainNative_Unknown4)] public uint Unknown4;
}

// Snow and leaves run through this block as well; Ktisis calls it Dust after
// its texture path.
[StructLayout(LayoutKind.Explicit, Size = 0x34)]
internal struct EnvParticlesNative
{
    [FieldOffset(NativeFields.EnvParticlesNative_Unknown1)] public float Unknown1;
    [FieldOffset(NativeFields.EnvParticlesNative_Intensity)] public float Intensity;
    [FieldOffset(NativeFields.EnvParticlesNative_Weight)] public float Weight;
    [FieldOffset(NativeFields.EnvParticlesNative_Spread)] public float Spread;
    [FieldOffset(NativeFields.EnvParticlesNative_Speed)] public float Speed;
    [FieldOffset(NativeFields.EnvParticlesNative_Size)] public float Size;
    [FieldOffset(NativeFields.EnvParticlesNative_Color)] public Vector4 Color;
    [FieldOffset(NativeFields.EnvParticlesNative_Glow)] public float Glow;
    [FieldOffset(NativeFields.EnvParticlesNative_Spin)] public float Spin;
    [FieldOffset(NativeFields.EnvParticlesNative_TextureId)] public uint TextureId;
}

[StructLayout(LayoutKind.Explicit, Size = 0x0C)]
internal struct EnvWindNative
{
    [FieldOffset(NativeFields.EnvWindNative_Direction)] public float Direction;
    [FieldOffset(NativeFields.EnvWindNative_Angle)] public float Angle;
    [FieldOffset(NativeFields.EnvWindNative_Speed)] public float Speed;
}
