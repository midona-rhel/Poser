using System;
using System.Collections.Generic;
using System.Linq;

namespace Poser.Domain.Scene;

/// <summary>Detached environment values for presentation. Contains no live setters or native objects.</summary>
public sealed record EnvironmentReading
{
    public int MinuteOfDay { get; init; }
    public int DayOfMonth { get; init; }
    public bool IsTimeFrozen { get; init; }
    public bool IsTimeFreezeAvailable { get; init; }
    public bool ResetTimeOnGPoseExit { get; init; }
    public bool IsWeatherOverrideEnabled { get; init; }
    public bool IsWeatherOverrideAvailable { get; init; }
    public bool ResetWeatherOnGPoseExit { get; init; }
    public bool IsSectionHoldAvailable { get; init; }
    public bool ResetSectionsOnGPoseExit { get; init; }
    public uint CurrentWeatherId { get; init; }
    public float TransitionTime { get; init; }
    public EnvSkyValues Sky { get; init; }
    public EnvCloudsValues Clouds { get; init; }
    public EnvLightingValues Lighting { get; init; }
    public EnvFogValues Fog { get; init; }
    public EnvRainValues Rain { get; init; }
    public EnvParticlesValues Particles { get; init; }
    public EnvStarsValues Stars { get; init; }
    public EnvWindValues Wind { get; init; }
    public bool IsWaterFrozen { get; init; }
    public bool IsWaterFreezeAvailable { get; init; }
    public bool ResetWaterOnGPoseExit { get; init; }
    public bool CanModify { get; init; }
    public bool HasFreeSlot { get; init; }
    public bool HasOverride { get; init; }
    public IReadOnlyList<EnvSection> HeldSections { get; init; } = [];
    public IReadOnlyList<WeatherInfo> AllWeathers { get; init; } = [];
    public IReadOnlyList<WeatherInfo> TerritoryWeathers { get; init; } = [];
    public IReadOnlyList<ActiveFestival> ActiveFestivals { get; init; } = [];
    public IReadOnlyDictionary<uint, FestivalEntry> FestivalList { get; init; } = new Dictionary<uint, FestivalEntry>();
    public bool IsSectionHeld(EnvSection section) => HeldSections.Contains(section);
    public WeatherInfo? GetWeatherInfo(uint id)
    {
        foreach (var weather in AllWeathers) if (weather.Id == id) return weather;
        return null;
    }
}
