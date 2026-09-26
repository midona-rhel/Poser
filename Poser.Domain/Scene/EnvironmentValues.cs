using System.Numerics;

namespace Poser.Domain.Scene;

/// <summary>One weather the game can run, joined from the Weather sheet.</summary>
public readonly record struct WeatherInfo(uint Id, string Name, uint IconId);

/// <summary>
/// The eight independently holdable parts of the game's environment state.
/// Names follow the reference implementation's EnvState layout; Particles is
/// the same block Ktisis calls Dust (snow and leaves run through it too).
/// </summary>
public enum EnvSection
{
    Sky,
    Clouds,
    Lighting,
    Fog,
    Rain,
    Particles,
    Stars,
    Wind,
}

// Section values cross this boundary as plain values. The native layouts are
// EnvState sub-structs; fields the references never identified keep their
// Unknown names so a future identification is a rename, not a re-layout.

/// <summary>Sky texture plus the fog member the reference stamps with it.</summary>
public readonly record struct EnvSkyValues(uint SkyTextureId, float SunVisibility);

public readonly record struct EnvLightingValues(
    Vector3 SunlightColor,
    Vector3 MoonlightColor,
    Vector3 AmbientColor,
    float Unknown1,
    float AmbientSaturation,
    float AmbientTemperature,
    float Unknown2,
    float LightDistance,
    float Unknown4);

public readonly record struct EnvStarsValues(
    float ConstellationIntensity,
    float ConstellationCount,
    float StarCount,
    float GalaxyIntensity,
    float StarIntensity,
    Vector4 MoonColor,
    float MoonBrightness);

public readonly record struct EnvFogValues(
    Vector4 Color,
    float Distance,
    float Thickness,
    float SkySmoothness,
    float SkyOpacity,
    float FogOpacity,
    float SunVisibility);

public readonly record struct EnvCloudsValues(
    Vector3 CloudColor1,
    Vector3 CloudColor2,
    float ShadowStop,
    float CloudHeight,
    uint CloudTexture,
    uint CloudSideTexture);

public readonly record struct EnvRainValues(
    float Raindrops,
    float Intensity,
    float Weight,
    float Scatter,
    float Unknown1,
    float Size,
    Vector4 Color,
    float Unknown2,
    float Unknown3,
    uint Unknown4);

public readonly record struct EnvParticlesValues(
    float Unknown1,
    float Intensity,
    float Weight,
    float Spread,
    float Speed,
    float Size,
    Vector4 Color,
    float Glow,
    float Spin,
    uint TextureId);

public readonly record struct EnvWindValues(float Direction, float Angle, float Speed);
