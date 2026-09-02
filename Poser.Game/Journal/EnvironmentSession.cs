using Poser.Application.Transforms;
using Poser.Services;

namespace Poser.Game.Journal;

/// <summary>Every value a surface sets on the environment — time, weather,
/// the held sections and their values, water, festivals — as a journal
/// step.</summary>
public sealed class EnvironmentSession
{
    private readonly ValueJournal _journal;
    private readonly IEnvironmentService _environment;
    private readonly IWorldRenderingService _rendering;
    private readonly IFestivalService _festivals;

    public EnvironmentSession(
        ValueJournal journal,
        IEnvironmentService environment,
        IWorldRenderingService rendering,
        IFestivalService festivals)
    {
        _journal = journal;
        _environment = environment;
        _rendering = rendering;
        _festivals = festivals;
    }

    public void Seal() => _journal.Seal();

    private void Set<T>(string property, string description, Func<T> read, Action<T> write, T value) =>
        _journal.Set((_environment, property), description, read, write, value);

    // ── time ────────────────────────────────────────────────────────────
    public void SetMinuteOfDay(int v) => Set("MinuteOfDay", "Set time of day", () => _environment.MinuteOfDay, x => _environment.MinuteOfDay = x, v);
    public void SetDayOfMonth(int v) => Set("DayOfMonth", "Set day of month", () => _environment.DayOfMonth, x => _environment.DayOfMonth = x, v);
    public void SetTimeFrozen(bool v) => Set("IsTimeFrozen", v ? "Freeze time" : "Release time", () => _environment.IsTimeFrozen, x => _environment.IsTimeFrozen = x, v);
    public void SetResetTimeOnGPoseExit(bool v) => Set("ResetTimeOnGPoseExit", "Set time restore", () => _environment.ResetTimeOnGPoseExit, x => _environment.ResetTimeOnGPoseExit = x, v);

    // ── weather ─────────────────────────────────────────────────────────
    public void SetWeather(uint id, float transitionTime = 0.5f)
    {
        var before = _environment.CurrentWeatherId;
        if (before == id)
            return;
        _environment.SetWeather(id, transitionTime);
        _journal.Record("Set weather", before, id, next => _environment.SetWeather(next, transitionTime));
    }

    public void SetTransitionTime(float v) => Set("TransitionTime", "Set weather transition", () => _environment.TransitionTime, x => _environment.TransitionTime = x, v);
    public void SetWeatherOverrideEnabled(bool v) => Set("IsWeatherOverrideEnabled", v ? "Hold weather" : "Release weather", () => _environment.IsWeatherOverrideEnabled, x => _environment.IsWeatherOverrideEnabled = x, v);
    public void SetResetWeatherOnGPoseExit(bool v) => Set("ResetWeatherOnGPoseExit", "Set weather restore", () => _environment.ResetWeatherOnGPoseExit, x => _environment.ResetWeatherOnGPoseExit = x, v);

    // ── sections ────────────────────────────────────────────────────────
    public void SetSectionHeld(EnvSection section, bool held) =>
        _journal.Set((_environment, section), held ? $"Hold {section}" : $"Release {section}",
            () => _environment.IsSectionHeld(section), x => _environment.SetSectionHeld(section, x), held);

    /// <summary>Releases every held section as one step.</summary>
    public void ReleaseAllSections()
    {
        var sections = Enum.GetValues<EnvSection>();
        var before = sections.Where(_environment.IsSectionHeld).ToArray();
        if (before.Length == 0)
            return;
        _environment.ReleaseAllSections();
        _journal.Record("Release all sections", before, Array.Empty<EnvSection>(), held =>
        {
            _environment.ReleaseAllSections();
            foreach (var section in held)
                _environment.SetSectionHeld(section, true);
        });
    }

    public void SetResetSectionsOnGPoseExit(bool v) => Set("ResetSectionsOnGPoseExit", "Set section restore", () => _environment.ResetSectionsOnGPoseExit, x => _environment.ResetSectionsOnGPoseExit = x, v);

    public void SetSky(EnvSkyValues v) => Set("Sky", "Set sky", () => _environment.Sky, x => _environment.Sky = x, v);
    public void SetClouds(EnvCloudsValues v) => Set("Clouds", "Set clouds", () => _environment.Clouds, x => _environment.Clouds = x, v);
    public void SetLighting(EnvLightingValues v) => Set("Lighting", "Set lighting", () => _environment.Lighting, x => _environment.Lighting = x, v);
    public void SetFog(EnvFogValues v) => Set("Fog", "Set fog", () => _environment.Fog, x => _environment.Fog = x, v);
    public void SetRain(EnvRainValues v) => Set("Rain", "Set rain", () => _environment.Rain, x => _environment.Rain = x, v);
    public void SetParticles(EnvParticlesValues v) => Set("Particles", "Set particles", () => _environment.Particles, x => _environment.Particles = x, v);
    public void SetStars(EnvStarsValues v) => Set("Stars", "Set stars", () => _environment.Stars, x => _environment.Stars = x, v);
    public void SetWind(EnvWindValues v) => Set("Wind", "Set wind", () => _environment.Wind, x => _environment.Wind = x, v);

    // ── water ───────────────────────────────────────────────────────────
    public void SetWaterFrozen(bool v) =>
        _journal.Set((_rendering, "IsWaterFrozen"), v ? "Freeze water" : "Release water", () => _rendering.IsWaterFrozen, x => _rendering.IsWaterFrozen = x, v);

    public void SetResetWaterOnGPoseExit(bool v) =>
        _journal.Set((_rendering, "ResetWaterOnGPoseExit"), "Set water restore", () => _rendering.ResetWaterOnGPoseExit, x => _rendering.ResetWaterOnGPoseExit = x, v);

    // ── festivals ───────────────────────────────────────────────────────
    public bool AddFestival(uint id, ushort phase = 1)
    {
        if (!_festivals.Add(id, phase))
            return false;
        _journal.Record($"Add festival {id}", false, true, on => { if (on) _festivals.Add(id, phase); else _festivals.Remove(id); });
        return true;
    }

    public bool RemoveFestival(uint id)
    {
        var slot = _festivals.ActiveFestivals.FirstOrDefault(f => f.Id == id);
        if (!_festivals.Remove(id))
            return false;
        ushort phase = slot.Phase == 0 ? (ushort)1 : slot.Phase;
        _journal.Record($"Remove festival {id}", true, false, on => { if (on) _festivals.Add(id, phase); else _festivals.Remove(id); });
        return true;
    }

    public bool ChangeFestivalPhase(uint id, ushort phase)
    {
        var slot = _festivals.ActiveFestivals.FirstOrDefault(f => f.Id == id);
        if (!_festivals.ChangePhase(id, phase))
            return false;
        _journal.Record($"Set festival {id} phase", slot.Phase, phase, next => _festivals.ChangePhase(id, next));
        return true;
    }

    /// <summary>Clears every festival slot as one step.</summary>
    public void ResetFestivals()
    {
        var before = _festivals.ActiveFestivals.ToArray();
        _festivals.Reset();
        _journal.Record("Reset festivals", before, Array.Empty<ActiveFestival>(), slots =>
        {
            _festivals.Reset();
            foreach (var slot in slots)
                _festivals.Add(slot.Id, slot.Phase);
        });
    }
}
