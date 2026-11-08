using System.Numerics;

namespace Poser.Domain.Scene;

/// <summary>One weather the game can run, joined from the Weather sheet.</summary>
public readonly record struct WeatherInfo(uint Id, string Name, uint IconId);

/// <summary>
/// Environment sections that can be held independently of game updates.
/// Particles includes effects such as dust, snow and leaves.
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

// These records contain values, not native memory layouts. Fields named
// Unknown are preserved even though their effect has not been identified.

/// <summary>Sky texture selection and sun visibility value.</summary>
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
