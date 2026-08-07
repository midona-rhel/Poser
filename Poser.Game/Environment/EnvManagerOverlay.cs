using System.Numerics;
using System.Runtime.InteropServices;

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
    [FieldOffset(0x008)] public uint SkyTextureId;
    [FieldOffset(0x020)] public EnvLightingNative Lighting;
    [FieldOffset(0x098)] public EnvStarsNative Stars;
    [FieldOffset(0x0C0)] public EnvFogNative Fog;
    [FieldOffset(0x148)] public EnvCloudsNative Clouds;
    [FieldOffset(0x170)] public EnvRainNative Rain;
    [FieldOffset(0x1A4)] public EnvParticlesNative Particles;
    [FieldOffset(0x1D8)] public EnvWindNative Wind;
}

[StructLayout(LayoutKind.Explicit, Size = 0x40)]
internal struct EnvLightingNative
{
    [FieldOffset(0x00)] public Vector3 SunlightColor;
    [FieldOffset(0x0C)] public Vector3 MoonlightColor;
    [FieldOffset(0x18)] public Vector3 AmbientColor;
    [FieldOffset(0x24)] public float Unknown1;
    [FieldOffset(0x28)] public float AmbientSaturation;
    [FieldOffset(0x2C)] public float AmbientTemperature;
    [FieldOffset(0x30)] public float Unknown2;      // shadow colour, unconfirmed
    [FieldOffset(0x34)] public float LightDistance; // world vignette off the camera
    [FieldOffset(0x38)] public float Unknown4;
}

[StructLayout(LayoutKind.Explicit, Size = 0x28)]
internal struct EnvStarsNative
{
    [FieldOffset(0x00)] public float ConstellationIntensity;
    [FieldOffset(0x04)] public float ConstellationCount;
    [FieldOffset(0x08)] public float StarCount;
    [FieldOffset(0x0C)] public float GalaxyIntensity;
    [FieldOffset(0x10)] public float StarIntensity;
    [FieldOffset(0x14)] public Vector4 MoonColor;
    [FieldOffset(0x24)] public float MoonBrightness;
}

[StructLayout(LayoutKind.Explicit, Size = 0x28)]
internal struct EnvFogNative
{
    [FieldOffset(0x00)] public Vector4 Color;
    [FieldOffset(0x10)] public float Distance;
    [FieldOffset(0x14)] public float Thickness;
    [FieldOffset(0x18)] public float SkySmoothness;
    [FieldOffset(0x1C)] public float SkyOpacity;
    [FieldOffset(0x20)] public float FogOpacity;
    [FieldOffset(0x24)] public float SunVisibility;
}

[StructLayout(LayoutKind.Explicit, Size = 0x28)]
internal struct EnvCloudsNative
{
    [FieldOffset(0x00)] public Vector3 CloudColor1;
    [FieldOffset(0x0C)] public Vector3 CloudColor2;
    [FieldOffset(0x18)] public float ShadowStop;
    [FieldOffset(0x1C)] public float CloudHeight;
    [FieldOffset(0x20)] public uint CloudTexture;
    [FieldOffset(0x24)] public uint CloudSideTexture;
}

[StructLayout(LayoutKind.Explicit, Size = 0x34)]
internal struct EnvRainNative
{
    [FieldOffset(0x00)] public float Raindrops;
    [FieldOffset(0x04)] public float Intensity;
    [FieldOffset(0x08)] public float Weight;
    [FieldOffset(0x0C)] public float Scatter;
    [FieldOffset(0x10)] public float Unknown1;
    [FieldOffset(0x14)] public float Size;
    [FieldOffset(0x18)] public Vector4 Color;
    [FieldOffset(0x28)] public float Unknown2;
    [FieldOffset(0x2C)] public float Unknown3;
    [FieldOffset(0x30)] public uint Unknown4;
}

// Snow and leaves run through this block as well; Ktisis calls it Dust after
// its texture path.
[StructLayout(LayoutKind.Explicit, Size = 0x34)]
internal struct EnvParticlesNative
{
    [FieldOffset(0x00)] public float Unknown1;
    [FieldOffset(0x04)] public float Intensity;
    [FieldOffset(0x08)] public float Weight;
    [FieldOffset(0x0C)] public float Spread;
    [FieldOffset(0x10)] public float Speed;
    [FieldOffset(0x14)] public float Size;
    [FieldOffset(0x18)] public Vector4 Color;
    [FieldOffset(0x28)] public float Glow;
    [FieldOffset(0x2C)] public float Spin;
    [FieldOffset(0x30)] public uint TextureId;
}

[StructLayout(LayoutKind.Explicit, Size = 0x0C)]
internal struct EnvWindNative
{
    [FieldOffset(0x00)] public float Direction;
    [FieldOffset(0x04)] public float Angle;
    [FieldOffset(0x08)] public float Speed;
}
