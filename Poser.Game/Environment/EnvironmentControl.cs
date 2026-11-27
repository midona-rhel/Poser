using System;
using System.Linq;
using Poser.Application.Transforms;
using Poser.Application.World;
using Poser.Domain;
using Poser.Domain.Scene;
using Poser.Documents.Files;

namespace Poser.Game.Environment;

/// <summary>
/// Changes the time, weather, sky sections, interior brightness, water and
/// festivals, and records each change as one undo step. The Environment page
/// of the main window and scene loading change the environment through here; the Library reads
/// weather names from it.
/// </summary>
public sealed class EnvironmentControl : IEnvironmentControl
{
    private readonly ValueJournal _journal;
    private readonly EnvironmentService _environment;
    private readonly WorldRenderingService _rendering;
    private readonly FestivalOverrides _festivals;

    public EnvironmentControl(
        ValueJournal journal,
        EnvironmentService environment,
        WorldRenderingService rendering,
        FestivalOverrides festivals)
    {
        _journal = journal;
        _environment = environment;
        _rendering = rendering;
        _festivals = festivals;
    }

    public void Seal() => _journal.Seal();

    private void Set<T>(string property, string description, Func<T> read, Action<T> write, T value) =>
        _journal.Set((_environment, property), description, read, ValueWrites.Unchecked(write), value);

    // ── time ────────────────────────────────────────────────────────────
    public void SetMinuteOfDay(int v) => SetTime("MinuteOfDay", "Set time of day", () => _environment.MinuteOfDay, x => _environment.MinuteOfDay = x, v);
    public void SetDayOfMonth(int v) => SetTime("DayOfMonth", "Set day of month", () => _environment.DayOfMonth, x => _environment.DayOfMonth = x, v);

    /// <summary>Writing the time of day or the day of the month freezes the
    /// clock (EnvironmentService.MinuteOfDay and DayOfMonth). So the undo step
    /// holds the value together with whether the clock was frozen, and writes
    /// the value first and the freeze last: undo starts a clock that was
    /// running before, and redo freezes it again.</summary>
    private void SetTime(string property, string description, Func<int> read, Action<int> write, int value) =>
        Set<(int Value, bool Frozen)>(property, description,
            () => (read(), _environment.IsTimeFrozen),
            x =>
            {
                write(x.Value);
                _environment.IsTimeFrozen = x.Frozen;
            },
            (value, true));

    public void SetTimeFrozen(bool v) => Set("IsTimeFrozen", v ? "Freeze time" : "Release time", () => _environment.IsTimeFrozen, x => _environment.IsTimeFrozen = x, v);
    public void SetResetTimeOnGPoseExit(bool v) => Set("ResetTimeOnGPoseExit", "Set time restore", () => _environment.ResetTimeOnGPoseExit, x => _environment.ResetTimeOnGPoseExit = x, v);

    // ── weather ─────────────────────────────────────────────────────────
    public void SetWeather(uint id, float transitionTime = 0.5f)
    {
        var before = _environment.CurrentWeatherId;
        _environment.SetWeather(id, transitionTime);
        if (before == id)
            return;
        _journal.Record(_environment, "Set weather", before, id, ValueWrites.Unchecked<uint>(next => _environment.SetWeather(next, transitionTime)));
    }

    public void SetTransitionTime(float v) => Set("TransitionTime", "Set weather transition", () => _environment.TransitionTime, x => _environment.TransitionTime = x, v);
    public void SetWeatherOverrideEnabled(bool v) => Set("IsWeatherOverrideEnabled", v ? "Hold weather" : "Release weather", () => _environment.IsWeatherOverrideEnabled, x => _environment.IsWeatherOverrideEnabled = x, v);
    public void SetResetWeatherOnGPoseExit(bool v) => Set("ResetWeatherOnGPoseExit", "Set weather restore", () => _environment.ResetWeatherOnGPoseExit, x => _environment.ResetWeatherOnGPoseExit = x, v);

    // ── sections ────────────────────────────────────────────────────────
    public void SetSectionHeld(EnvSection section, bool held) =>
        _journal.Set((_environment, section), held ? $"Hold {section}" : $"Release {section}",
            () => _environment.IsSectionHeld(section), ValueWrites.Unchecked<bool>(x => _environment.SetSectionHeld(section, x)), held);

    /// <summary>Releases every held section as one step.</summary>
    public void ReleaseAllSections()
    {
        var sections = Enum.GetValues<EnvSection>();
        var before = sections.Where(_environment.IsSectionHeld).ToArray();
        if (before.Length == 0)
            return;
        _environment.ReleaseAllSections();
        _journal.Record(_environment, "Release all sections", before, Array.Empty<EnvSection>(), ValueWrites.Unchecked<EnvSection[]>(held =>
        {
            _environment.ReleaseAllSections();
            foreach (var section in held)
                _environment.SetSectionHeld(section, true);
        }));
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

    // ── housing interior brightness ─────────────────────────────────────
    // Each step remembers which interior it was made in. Undo and redo skip the
    // step once the player has left that interior, so a value is never written
    // into a different house.

    /// <summary>Owned means Poser holds the brightness; not owned means the
    /// house's own value is back.</summary>
    private Outcome RestoreInteriorBrightness(ulong binding, (bool Owned, float Value) state) =>
        new(state.Owned
            ? _environment.TrySetInteriorBrightness(state.Value, binding)
            : _environment.ReleaseInteriorBrightness(binding));

    public void SetInteriorBrightness(float v)
    {
        if (_environment.InteriorBrightness is null)
            return;
        ulong binding = _environment.HousingInteriorBinding;
        float value = Math.Clamp(v, 0f, 1f);
        _journal.Set((_environment, "InteriorBrightness", binding),
            "Set interior brightness",
            () => (Owned: _environment.IsInteriorBrightnessOverridden,
                Value: _environment.InteriorBrightness ?? value),
            next => RestoreInteriorBrightness(binding, next),
            (Owned: true, Value: value),
            () => _environment.HousingInteriorBinding == binding);
    }

    public void ResetInteriorBrightness()
    {
        if (_environment.InteriorBrightness is not { } before)
            return;
        bool owned = _environment.IsInteriorBrightnessOverridden;
        ulong binding = _environment.HousingInteriorBinding;
        if (!_environment.TryResetInteriorBrightness(binding) ||
            _environment.InteriorBrightness is not { } reset)
            return;
        _journal.Record(_environment, "Reset interior brightness",
            (Owned: owned, Value: before), (Owned: true, Value: reset),
            next => RestoreInteriorBrightness(binding, next),
            () => _environment.HousingInteriorBinding == binding);
    }

    public void ReleaseInteriorBrightness()
    {
        if (!_environment.IsInteriorBrightnessOverridden ||
            _environment.InteriorBrightness is not { } before)
            return;
        ulong binding = _environment.HousingInteriorBinding;
        if (!_environment.ReleaseInteriorBrightness(binding) ||
            _environment.InteriorBrightness is not { } restored)
            return;
        // Recorded even when the value did not change: Poser stops holding it.
        _journal.Record(_environment, "Release interior brightness",
            (Owned: true, Value: before), (Owned: false, Value: restored),
            next => RestoreInteriorBrightness(binding, next),
            () => _environment.HousingInteriorBinding == binding);
    }

    // ── water ───────────────────────────────────────────────────────────
    public void SetWaterFrozen(bool v) => Set("IsWaterFrozen", v ? "Freeze water" : "Release water", () => _rendering.IsWaterFrozen, x => _rendering.IsWaterFrozen = x, v);
    public void SetResetWaterOnGPoseExit(bool v) => Set("ResetWaterOnGPoseExit", "Set water restore", () => _rendering.ResetWaterOnGPoseExit, x => _rendering.ResetWaterOnGPoseExit = x, v);

    // ── festivals ───────────────────────────────────────────────────────
    public bool AddFestival(uint id, ushort phase = 1)
    {
        if (!_festivals.Add(id, phase))
            return false;
        _journal.Record(_festivals, $"Add festival {id}", false, true, ValueWrites.Unchecked<bool>(on => { if (on) _festivals.Add(id, phase); else _festivals.Remove(id); }));
        return true;
    }

    public bool RemoveFestival(uint id)
    {
        var slot = _festivals.ActiveFestivals.FirstOrDefault(f => f.Id == id);
        if (!_festivals.Remove(id))
            return false;
        ushort phase = slot.Phase == 0 ? (ushort)1 : slot.Phase;
        _journal.Record(_festivals, $"Remove festival {id}", true, false, ValueWrites.Unchecked<bool>(on => { if (on) _festivals.Add(id, phase); else _festivals.Remove(id); }));
        return true;
    }

    public bool ChangeFestivalPhase(uint id, ushort phase)
    {
        var slot = _festivals.ActiveFestivals.FirstOrDefault(f => f.Id == id);
        if (!_festivals.ChangePhase(id, phase))
            return false;
        _journal.Record(_festivals, $"Set festival {id} phase", slot.Phase, phase, ValueWrites.Unchecked<ushort>(next => _festivals.ChangePhase(id, next)));
        return true;
    }

    /// <summary>Puts back the area's own festivals as one undo step.</summary>
    public void ResetFestivals()
    {
        var before = _festivals.ActiveFestivals.ToArray();
        _festivals.Reset();
        // Redo is Reset again; undo puts all eight slots back as they were.
        _journal.Record(_festivals, "Reset festivals", before, Array.Empty<ActiveFestival>(), ValueWrites.Unchecked<ActiveFestival[]>(slots =>
        {
            if (slots.Length == 0)
                _festivals.Reset();
            else
                _festivals.SetSlots(slots);
        }));
    }

    public EnvironmentReading Read() => new()
    {
        MinuteOfDay = _environment.MinuteOfDay,
        DayOfMonth = _environment.DayOfMonth,
        IsTimeFrozen = _environment.IsTimeFrozen,
        IsTimeFreezeAvailable = _environment.IsTimeFreezeAvailable,
        ResetTimeOnGPoseExit = _environment.ResetTimeOnGPoseExit,
        IsWeatherOverrideEnabled = _environment.IsWeatherOverrideEnabled,
        IsWeatherOverrideAvailable = _environment.IsWeatherOverrideAvailable,
        ResetWeatherOnGPoseExit = _environment.ResetWeatherOnGPoseExit,
        IsSectionHoldAvailable = _environment.IsSectionHoldAvailable,
        ResetSectionsOnGPoseExit = _environment.ResetSectionsOnGPoseExit,
        IsHousingInterior = _environment.IsHousingInterior,
        InteriorBrightness = _environment.InteriorBrightness,
        IsInteriorBrightnessOverridden = _environment.IsInteriorBrightnessOverridden,
        CurrentWeatherId = _environment.CurrentWeatherId,
        TransitionTime = _environment.TransitionTime,
        Sky = _environment.Sky,
        Clouds = _environment.Clouds,
        Lighting = _environment.Lighting,
        Fog = _environment.Fog,
        Rain = _environment.Rain,
        Particles = _environment.Particles,
        Stars = _environment.Stars,
        Wind = _environment.Wind,
        IsWaterFrozen = _rendering.IsWaterFrozen,
        IsWaterFreezeAvailable = _rendering.IsWaterFreezeAvailable,
        ResetWaterOnGPoseExit = _rendering.ResetWaterOnGPoseExit,
        CanModify = _festivals.CanModify,
        HasFreeSlot = _festivals.HasFreeSlot,
        HasOverride = _festivals.HasOverride,
        HeldSections = Enum.GetValues<EnvSection>().Where(_environment.IsSectionHeld).ToArray(),
        AllWeathers = _environment.AllWeathers.ToArray(),
        TerritoryWeathers = _environment.TerritoryWeathers.ToArray(),
        ActiveFestivals = _festivals.ActiveFestivals.ToArray(),
        FestivalList = _festivals.FestivalList.ToDictionary(pair => pair.Key,
            pair => pair.Value with { KnownPhases = pair.Value.KnownPhases.ToArray() }),
    };

    public WeatherInfo? GetWeatherInfo(uint id) => _environment.GetWeatherInfo(id);

    /// <summary>Applies a saved scene's environment. Records no undo step:
    /// the scene load is one undo step of its own.</summary>
    public void Apply(SceneEnvironment target)
    {
        Seal();
        ApplyCore(target);
    }

    /// <summary>The environment as a scene file saves it: the clock, the
    /// weather, and the values of the held sections only.</summary>
    public SceneEnvironment Capture()
    {
        var environment = new SceneEnvironment
        {
            MinuteOfDay = _environment.MinuteOfDay,
            // The game's month has 32 days, but only 31 can be set.
            DayOfMonth = Math.Min(_environment.DayOfMonth, 31),
            IsTimeFrozen = _environment.IsTimeFrozen,
            WeatherId = _environment.CurrentWeatherId,
            WeatherName = _environment.GetWeatherInfo(
                _environment.CurrentWeatherId)?.Name ?? string.Empty,
            IsWeatherOverrideEnabled = _environment.IsWeatherOverrideEnabled,
            // The game's own value; a scene file needs a usable one.
            TransitionTime = float.IsFinite(_environment.TransitionTime) && _environment.TransitionTime >= 0
                ? _environment.TransitionTime
                : 0.5f,
        };

        foreach (var section in Enum.GetValues<EnvSection>())
        {
            if (!_environment.IsSectionHeld(section))
                continue;
            environment.HeldSections.Add(section);
            switch (section)
            {
                case EnvSection.Sky:
                    environment.Sky = _environment.Sky;
                    break;
                case EnvSection.Clouds:
                    environment.Clouds = _environment.Clouds;
                    break;
                case EnvSection.Lighting:
                    environment.Lighting = _environment.Lighting;
                    break;
                case EnvSection.Fog:
                    environment.Fog = _environment.Fog;
                    break;
                case EnvSection.Rain:
                    environment.Rain = _environment.Rain;
                    break;
                case EnvSection.Particles:
                    environment.Particles = _environment.Particles;
                    break;
                case EnvSection.Stars:
                    environment.Stars = _environment.Stars;
                    break;
                case EnvSection.Wind:
                    environment.Wind = _environment.Wind;
                    break;
            }
        }

        return environment;
    }

    private void ApplyCore(SceneEnvironment target)
    {
        // Setting the clock freezes it. The saved freeze is written last, so a
        // scene saved with a running clock starts running again.
        _environment.MinuteOfDay = target.MinuteOfDay;
        _environment.DayOfMonth = target.DayOfMonth;
        _environment.IsTimeFrozen = target.IsTimeFrozen;

        _environment.TransitionTime = target.TransitionTime;
        if (target.IsWeatherOverrideEnabled)
            _environment.SetWeather(target.WeatherId, target.TransitionTime);
        else
            _environment.IsWeatherOverrideEnabled = false;

        // Every section is written: a held section gets its saved values
        // (setting a value holds the section), an unheld one goes back to the game.
        foreach (var section in Enum.GetValues<EnvSection>())
        {
            bool held = target.HeldSections.Contains(section);
            if (!held)
            {
                _environment.SetSectionHeld(section, false);
                continue;
            }
            switch (section)
            {
                case EnvSection.Sky when target.Sky is { } sky:
                    _environment.Sky = sky;
                    break;
                case EnvSection.Clouds when target.Clouds is { } clouds:
                    _environment.Clouds = clouds;
                    break;
                case EnvSection.Lighting when target.Lighting is { } lighting:
                    _environment.Lighting = lighting;
                    break;
                case EnvSection.Fog when target.Fog is { } fog:
                    _environment.Fog = fog;
                    break;
                case EnvSection.Rain when target.Rain is { } rain:
                    _environment.Rain = rain;
                    break;
                case EnvSection.Particles when target.Particles is { } particles:
                    _environment.Particles = particles;
                    break;
                case EnvSection.Stars when target.Stars is { } stars:
                    _environment.Stars = stars;
                    break;
                case EnvSection.Wind when target.Wind is { } wind:
                    _environment.Wind = wind;
                    break;
            }
        }
    }
}
