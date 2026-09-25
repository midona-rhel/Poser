using Poser.Domain.Scene;
using Poser.Files;

namespace Poser.Application.World;

public interface IEnvironmentControl
{
    EnvironmentReading Read();
    WeatherInfo? GetWeatherInfo(uint id);
    SceneEnvironment Capture();
    void Apply(SceneEnvironment target, bool recordHistory = true);
    void Seal();
    void SetMinuteOfDay(int v);
    void SetDayOfMonth(int v);
    void SetTimeFrozen(bool v);
    void SetResetTimeOnGPoseExit(bool v);
    void SetWeather(uint id, float transitionTime = 0.5f);
    void SetTransitionTime(float v);
    void SetWeatherOverrideEnabled(bool v);
    void SetResetWeatherOnGPoseExit(bool v);
    void SetSectionHeld(EnvSection section, bool held);
    void ReleaseAllSections();
    void SetResetSectionsOnGPoseExit(bool v);
    void SetSky(EnvSkyValues v);
    void SetClouds(EnvCloudsValues v);
    void SetLighting(EnvLightingValues v);
    void SetFog(EnvFogValues v);
    void SetRain(EnvRainValues v);
    void SetParticles(EnvParticlesValues v);
    void SetStars(EnvStarsValues v);
    void SetWind(EnvWindValues v);
    void SetWaterFrozen(bool v);
    void SetResetWaterOnGPoseExit(bool v);
    bool AddFestival(uint id, ushort phase = 1);
    bool RemoveFestival(uint id);
    bool ChangeFestivalPhase(uint id, ushort phase);
    void ResetFestivals();
}
