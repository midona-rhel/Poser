using Poser.Domain.Scene;

namespace Poser.Application.World;

/// <summary>
/// What the Environment window and the Library can do to the time, weather,
/// sky, water and festivals of the area the player is in. Every change is one
/// undo step. The game-side class that does the work is
/// <c>Poser.Game.Environment.EnvironmentControl</c>.
/// </summary>
public interface IEnvironmentControl
{
    /// <summary>Ends the current slider drag, so the next change starts a new
    /// undo step instead of joining the last one.</summary>
    void Seal();

    /// <summary>The current values of everything the Environment window shows.</summary>
    EnvironmentReading Read();

    WeatherInfo? GetWeatherInfo(uint id);

    void SetMinuteOfDay(int minute);
    void SetDayOfMonth(int day);
    void SetTimeFrozen(bool frozen);
    void SetResetTimeOnGPoseExit(bool reset);

    void SetWeather(uint id, float transitionTime = 0.5f);
    void SetTransitionTime(float seconds);
    void SetWeatherOverrideEnabled(bool held);
    void SetResetWeatherOnGPoseExit(bool reset);

    /// <summary>A held section keeps Poser's values; a released one is run by
    /// the game again.</summary>
    void SetSectionHeld(EnvSection section, bool held);
    void ReleaseAllSections();
    void SetResetSectionsOnGPoseExit(bool reset);

    void SetSky(EnvSkyValues values);
    void SetClouds(EnvCloudsValues values);
    void SetLighting(EnvLightingValues values);
    void SetFog(EnvFogValues values);
    void SetRain(EnvRainValues values);
    void SetParticles(EnvParticlesValues values);
    void SetStars(EnvStarsValues values);
    void SetWind(EnvWindValues values);

    /// <summary>Brightness of a housing interior, 0 to 1. Does nothing outside
    /// a housing interior.</summary>
    void SetInteriorBrightness(float brightness);

    /// <summary>Sets the interior brightness to the value the house saved.</summary>
    void ResetInteriorBrightness();

    /// <summary>Puts back the brightness the interior had before Poser changed it.</summary>
    void ReleaseInteriorBrightness();

    void SetWaterFrozen(bool frozen);
    void SetResetWaterOnGPoseExit(bool reset);

    /// <summary>False when no festival slot is free, outside GPose, or where
    /// the festival is not allowed.</summary>
    bool AddFestival(uint id, ushort phase = 1);
    bool RemoveFestival(uint id);
    bool ChangeFestivalPhase(uint id, ushort phase);

    /// <summary>Puts back the festivals the area had before Poser changed them.</summary>
    void ResetFestivals();
}
