using System.Collections.Generic;
using Poser.Domain.Scene;

namespace Poser.Application.World;

/// <summary>
/// Time, weather and per-section environment holds (Brio's EnvironmentService
/// and TimeService, merged; the section hold is Ktisis' EnvState re-stamp).
///
/// Every hold is expressed positively: a HELD section is the one Poser keeps
/// stamping, a released section is the one the game runs naturally. A UI that
/// presents the switch the other way round ("on" = natural) inverts at its own
/// boundary — never here.
///
/// Values read live from the game and write straight back, so a section that is
/// not held is simply overwritten by the game on its next update.
/// </summary>
public interface IEnvironmentRuntimePort
{
    // ── Time ──────────────────────────────────────────────────────────

    /// <summary>Eorzean minute of day, 0..1439. Writing forces the freeze on:
    /// an unfrozen clock would discard the write within a frame.</summary>
    int MinuteOfDay { get; set; }

    /// <summary>Eorzean day of the (32-day) month, 1..31. Writing forces the
    /// freeze on, exactly as <see cref="MinuteOfDay"/> does.</summary>
    int DayOfMonth { get; set; }

    bool IsTimeFrozen { get; set; }

    /// <summary>False when the time hook is unavailable; the clock can still be
    /// read, but neither writing nor freezing it will do anything.</summary>
    bool IsTimeFreezeAvailable { get; }

    bool ResetTimeOnGPoseExit { get; set; }

    // ── Weather ───────────────────────────────────────────────────────

    /// <summary>Holds the current weather by suppressing the game's own
    /// territory weather update.</summary>
    bool IsWeatherOverrideEnabled { get; set; }

    bool IsWeatherOverrideAvailable { get; }

    uint CurrentWeatherId { get; }

    /// <summary>Applies a weather and its transition, and turns the hold on —
    /// without it the game reverts the pick on its next weather update.</summary>
    void SetWeather(uint id, float transitionTime = 0.5f);

    float TransitionTime { get; set; }

    /// <summary>The weathers this territory runs naturally, id-sorted and
    /// deduplicated. Empty until the game has populated the zone's env scene,
    /// which happens some frames after a zone change.</summary>
    IReadOnlyList<WeatherInfo> TerritoryWeathers { get; }

    /// <summary>Every named weather in the sheet.</summary>
    IReadOnlyList<WeatherInfo> AllWeathers { get; }

    WeatherInfo? GetWeatherInfo(uint id);

    bool ResetWeatherOnGPoseExit { get; set; }

    // ── Environment sections ──────────────────────────────────────────

    /// <summary>False when the env-state hook is unavailable; sections can
    /// still be read and written, but the game reclaims them immediately.</summary>
    bool IsSectionHoldAvailable { get; }

    bool IsSectionHeld(EnvSection section);

    /// <summary>Holds or releases one section. Releasing needs no restore: the
    /// game's next env-state copy writes the vanilla values back itself.</summary>
    void SetSectionHeld(EnvSection section, bool held);

    void ReleaseAllSections();

    bool ResetSectionsOnGPoseExit { get; set; }

    // Setting any of these implies holding that section.
    EnvSkyValues Sky { get; set; }
    EnvCloudsValues Clouds { get; set; }
    EnvLightingValues Lighting { get; set; }
    EnvFogValues Fog { get; set; }
    EnvRainValues Rain { get; set; }
    EnvParticlesValues Particles { get; set; }
    EnvStarsValues Stars { get; set; }
    EnvWindValues Wind { get; set; }
}
