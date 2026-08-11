using System;
using System.Collections.Generic;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using Lumina.Excel;
using Poser.Core;
using Poser.Services;
using CSEnvManager = FFXIVClientStructs.FFXIV.Client.Graphics.Environment.EnvManager;
using CSFramework = FFXIVClientStructs.FFXIV.Client.System.Framework.Framework;
using WeatherRow = Lumina.Excel.Sheets.Weather;

namespace Poser.Game.Environment;

/// <summary>
/// Time, weather and per-section environment holds. Brio's TimeService and
/// EnvironmentService merged into one native surface, with Brio's mediator
/// replaced by the event bus and its configuration flags by plain properties.
///
/// Three independent mechanisms, each degrading on its own:
///  · Time freeze — the Eorzean time update is hooked to a no-op. The clock
///    value itself is written directly; nothing is restored on release,
///    because the game reclaims the clock on its next update.
///  · Weather hold — the territory weather update is hooked to a no-op, so the
///    weather byte written into EnvManager stays put.
///  · Section hold — the env-state copy is hooked; the destination is
///    snapshotted BEFORE the original runs and the held sections are stamped
///    back after (Ktisis' EnvModule mechanism, Brio's signature and hook
///    shape). The live EnvState therefore IS the held state: a write into it
///    perpetuates itself, and releasing a section needs no restore at all.
///
/// Every write is small and direct, on the caller's (main game) thread — the
/// pattern both references validate. No address, pointer or native layout
/// leaves this assembly.
/// </summary>
public sealed unsafe class EnvironmentService : IEnvironmentService, IDisposable
{
    private const float DefaultTransitionTime = 0.5f;
    // The Eorzean month is 32 days; only 31 are addressable as a day of month,
    // matching both references.
    private const long MonthSeconds = 2764800;
    private const long DaySeconds = 86400;
    private const int MaxTerritoryWeatherSlots = 32;

    [Flags]
    private enum SectionFlags
    {
        None = 0,
        Sky = 1 << 0,
        Clouds = 1 << 1,
        Lighting = 1 << 2,
        Fog = 1 << 3,
        Rain = 1 << 4,
        Particles = 1 << 5,
        Stars = 1 << 6,
        Wind = 1 << 7,
    }

    private readonly IClientState _clientState;
    private readonly IPluginLog _log;
    private readonly IEventBus _events;
    private readonly Action<GPoseStateChangedEvent> _onGPoseStateChanged;

    private delegate void UpdateEorzeaTimeDelegate(nint a1, nint a2);
    private readonly Hook<UpdateEorzeaTimeDelegate>? _timeHook;

    // The manager argument is untouched — the detour exists only to stop the
    // original from running — so it stays an opaque pointer.
    private delegate void UpdateTerritoryWeatherDelegate(nint weatherManager);
    private readonly Hook<UpdateTerritoryWeatherDelegate>? _weatherHook;

    private delegate nint EnvStateCopyDelegate(EnvStateNative* dest, EnvStateNative* src);
    private readonly Hook<EnvStateCopyDelegate>? _envStateHook;
    private readonly bool _envStateHookEnabled;

    private readonly ExcelSheet<WeatherRow>? _weatherSheet;
    private readonly List<WeatherInfo> _allWeathers = new();
    private readonly Dictionary<uint, WeatherInfo> _weatherById = new();
    private readonly List<WeatherInfo> _territoryWeathers = new();
    private uint? _cachedTerritory;

    private SectionFlags _held = SectionFlags.None;

    public bool ResetTimeOnGPoseExit { get; set; } = true;
    public bool ResetWeatherOnGPoseExit { get; set; } = true;
    public bool ResetSectionsOnGPoseExit { get; set; } = true;

    public bool IsTimeFreezeAvailable => _timeHook != null;
    public bool IsWeatherOverrideAvailable => _weatherHook != null;
    public bool IsSectionHoldAvailable => _envStateHookEnabled;

    public EnvironmentService(
        IClientState clientState,
        ISigScanner sigScanner,
        IGameInteropProvider hooking,
        IDataManager data,
        IPluginLog log,
        IEventBus events)
    {
        _clientState = clientState;
        _log = log;
        _events = events;

        _timeHook = CreateHook<UpdateEorzeaTimeDelegate>(
            sigScanner, hooking,
            "48 89 5C 24 ?? 57 48 83 EC ?? 48 8B F9 48 8B DA 48 81 C1 ?? ?? ?? ?? E8 ?? ?? ?? ?? 4C",
            UpdateEorzeaTimeDetour, "time freeze");

        _weatherHook = CreateHook<UpdateTerritoryWeatherDelegate>(
            sigScanner, hooking,
            "48 89 5C 24 ?? 55 56 57 48 83 EC ?? 48 8B F9 48 8D 0D",
            UpdateTerritoryWeatherDetour, "weather hold");

        // Brio scans the copy routine directly; Ktisis scans a call site to the
        // same routine. Both are kept: a broken direct pattern still leaves the
        // section holds working through the call site.
        _envStateHook = CreateHook<EnvStateCopyDelegate>(
            sigScanner, hooking,
            "0F 10 42 08 0F 11 41 08 F2 0F 10 4A 18",
            EnvStateCopyDetour, "environment section hold")
            ?? CreateHook<EnvStateCopyDelegate>(
                sigScanner, hooking,
                "E8 ?? ?? ?? ?? 49 3B F5 75 0D",
                EnvStateCopyDetour, "environment section hold (call site)");

        // Unlike the other two, this hook stays enabled: the detour is what
        // reads the hold flags, and it is a plain pass-through while none are
        // set. Enabling it per-section would rebind memory on every toggle.
        if (_envStateHook != null)
        {
            try
            {
                _envStateHook.Enable();
                _envStateHookEnabled = true;
            }
            catch (Exception ex)
            {
                _log.Warning($"Environment: section hold hook could not be enabled ({ex.Message}); sections cannot be held.");
            }
        }

        try
        {
            _weatherSheet = data.GetExcelSheet<WeatherRow>();
            BuildAllWeathers();
        }
        catch (Exception ex)
        {
            _log.Warning($"Environment: weather sheet unavailable ({ex.Message}); weather names and icons will be missing.");
        }

        _clientState.TerritoryChanged += OnTerritoryChanged;
        _clientState.Logout += OnLogout;
        _onGPoseStateChanged = OnGPoseStateChanged;
        _events.Subscribe(_onGPoseStateChanged);
    }

    private Hook<T>? CreateHook<T>(
        ISigScanner sigScanner, IGameInteropProvider hooking,
        string signature, T detour, string capability) where T : Delegate
    {
        try
        {
            var address = sigScanner.ScanText(signature);
            return hooking.HookFromAddress(address, detour);
        }
        catch (Exception ex)
        {
            _log.Warning($"Environment: {capability} signature not found ({ex.Message}); that control is unavailable.");
            return null;
        }
    }

    // ── Time ──────────────────────────────────────────────────────────

    public bool IsTimeFrozen
    {
        get => _timeHook?.IsEnabled == true;
        set
        {
            if (_timeHook == null || value == IsTimeFrozen)
                return;
            if (value)
                _timeHook.Enable();
            else
                _timeHook.Disable();
        }
    }

    /// <summary>The override value when the game is running one, otherwise the
    /// live clock — reading the wrong one of the two reports a time the world
    /// is not actually at.</summary>
    private long EorzeaTime
    {
        get
        {
            var framework = CSFramework.Instance();
            if (framework == null)
                return 0;
            return framework->ClientTime.IsEorzeaTimeOverridden
                ? framework->ClientTime.EorzeaTimeOverride
                : framework->ClientTime.EorzeaTime;
        }
        set
        {
            var framework = CSFramework.Instance();
            if (framework == null)
                return;
            framework->ClientTime.EorzeaTime = value;
            if (framework->ClientTime.IsEorzeaTimeOverridden)
                framework->ClientTime.EorzeaTimeOverride = value;
        }
    }

    public int MinuteOfDay
    {
        get => (int)(EorzeaTime % MonthSeconds % DaySeconds / 60);
        set
        {
            IsTimeFrozen = true;
            EorzeaTime = Math.Clamp(value, 0, 1439) * 60 + DaySeconds * (DayOfMonth - 1);
        }
    }

    public int DayOfMonth
    {
        get => (int)(EorzeaTime % MonthSeconds / DaySeconds) + 1;
        set
        {
            IsTimeFrozen = true;
            EorzeaTime = MinuteOfDay * 60 + DaySeconds * (Math.Clamp(value, 1, 31) - 1);
        }
    }

    private void UpdateEorzeaTimeDetour(nint a1, nint a2)
    {
        // The original is deliberately not called: that is the freeze.
    }

    // ── Weather ───────────────────────────────────────────────────────

    public bool IsWeatherOverrideEnabled
    {
        get => _weatherHook?.IsEnabled == true;
        set
        {
            if (_weatherHook == null || value == IsWeatherOverrideEnabled)
                return;
            if (value)
                _weatherHook.Enable();
            else
                _weatherHook.Disable();
        }
    }

    public uint CurrentWeatherId
    {
        get
        {
            var manager = CSEnvManager.Instance();
            return manager == null ? 0u : manager->ActiveWeather;
        }
    }

    public float TransitionTime
    {
        get
        {
            var manager = CSEnvManager.Instance();
            return manager == null ? DefaultTransitionTime : manager->TransitionTime;
        }
        set
        {
            var manager = CSEnvManager.Instance();
            if (manager != null)
                manager->TransitionTime = value;
        }
    }

    public void SetWeather(uint id, float transitionTime = DefaultTransitionTime)
    {
        var manager = CSEnvManager.Instance();
        if (manager == null)
            return;
        // The hold goes on first: the game's weather update runs between here
        // and the next frame and would otherwise revert the pick.
        IsWeatherOverrideEnabled = true;
        manager->ActiveWeather = (byte)id;
        manager->TransitionTime = transitionTime;
    }

    public IReadOnlyList<WeatherInfo> AllWeathers => _allWeathers;

    public WeatherInfo? GetWeatherInfo(uint id)
        => _weatherById.TryGetValue(id, out var info) ? info : null;

    public IReadOnlyList<WeatherInfo> TerritoryWeathers
    {
        get
        {
            // The env scene is populated some frames after a zone change, so an
            // empty read is retried rather than cached as the answer.
            if (_cachedTerritory != _clientState.TerritoryType)
            {
                UpdateTerritoryWeathers();
                if (_territoryWeathers.Count > 0)
                    _cachedTerritory = _clientState.TerritoryType;
            }
            return _territoryWeathers;
        }
    }

    private void BuildAllWeathers()
    {
        if (_weatherSheet == null)
            return;
        foreach (var row in _weatherSheet)
        {
            var name = row.Name.ExtractText();
            if (string.IsNullOrEmpty(name))
                continue;
            var info = new WeatherInfo(row.RowId, name, (uint)row.Icon);
            _allWeathers.Add(info);
            _weatherById[row.RowId] = info;
        }
    }

    private void UpdateTerritoryWeathers()
    {
        _territoryWeathers.Clear();

        var manager = CSEnvManager.Instance();
        if (manager == null)
            return;
        var scene = manager->EnvScene;
        if (scene == null)
            return;

        // WeatherIds is the ClientStructs-named accessor for the zone's weather
        // table; the references' hand-counted offset for it is one revision out
        // of date (0x2C against the mapped 0x30).
        var ids = scene->WeatherIds;
        var count = Math.Min(ids.Length, MaxTerritoryWeatherSlots);
        var seen = new HashSet<uint>();
        for (var i = 0; i < count; i++)
        {
            uint id = ids[i];
            if (id == 0 || !seen.Add(id))
                continue;
            if (_weatherById.TryGetValue(id, out var info))
                _territoryWeathers.Add(info);
        }

        _territoryWeathers.Sort((a, b) => a.Id.CompareTo(b.Id));
    }

    private void UpdateTerritoryWeatherDetour(nint weatherManager)
    {
        // The original is deliberately not called: that is the hold.
    }

    // ── Environment sections ──────────────────────────────────────────

    public bool IsSectionHeld(EnvSection section) => (_held & Flag(section)) != 0;

    public void SetSectionHeld(EnvSection section, bool held)
    {
        if (held)
            _held |= Flag(section);
        else
            _held &= ~Flag(section);
    }

    public void ReleaseAllSections() => _held = SectionFlags.None;

    private static SectionFlags Flag(EnvSection section) => section switch
    {
        EnvSection.Sky => SectionFlags.Sky,
        EnvSection.Clouds => SectionFlags.Clouds,
        EnvSection.Lighting => SectionFlags.Lighting,
        EnvSection.Fog => SectionFlags.Fog,
        EnvSection.Rain => SectionFlags.Rain,
        EnvSection.Particles => SectionFlags.Particles,
        EnvSection.Stars => SectionFlags.Stars,
        EnvSection.Wind => SectionFlags.Wind,
        _ => SectionFlags.None,
    };

    /// <summary>The live env state, reached through the ClientStructs-named
    /// container field. Null when the manager is not up yet.</summary>
    private static EnvStateNative* LiveState()
    {
        var manager = CSEnvManager.Instance();
        return manager == null ? null : (EnvStateNative*)&manager->EnvState;
    }

    public EnvSkyValues Sky
    {
        get
        {
            var state = LiveState();
            return state == null
                ? default
                : new EnvSkyValues(state->SkyTextureId, state->Fog.SunVisibility);
        }
        set
        {
            var state = LiveState();
            if (state == null)
                return;
            state->SkyTextureId = value.SkyTextureId;
            state->Fog.SunVisibility = value.SunVisibility;
            SetSectionHeld(EnvSection.Sky, true);
        }
    }

    public EnvCloudsValues Clouds
    {
        get
        {
            var state = LiveState();
            if (state == null)
                return default;
            ref var clouds = ref state->Clouds;
            return new EnvCloudsValues(
                clouds.CloudColor1, clouds.CloudColor2, clouds.ShadowStop,
                clouds.CloudHeight, clouds.CloudTexture, clouds.CloudSideTexture);
        }
        set
        {
            var state = LiveState();
            if (state == null)
                return;
            ref var clouds = ref state->Clouds;
            clouds.CloudColor1 = value.CloudColor1;
            clouds.CloudColor2 = value.CloudColor2;
            clouds.ShadowStop = value.ShadowStop;
            clouds.CloudHeight = value.CloudHeight;
            clouds.CloudTexture = value.CloudTexture;
            clouds.CloudSideTexture = value.CloudSideTexture;
            SetSectionHeld(EnvSection.Clouds, true);
        }
    }

    public EnvLightingValues Lighting
    {
        get
        {
            var state = LiveState();
            if (state == null)
                return default;
            ref var lighting = ref state->Lighting;
            return new EnvLightingValues(
                lighting.SunlightColor, lighting.MoonlightColor, lighting.AmbientColor,
                lighting.Unknown1, lighting.AmbientSaturation, lighting.AmbientTemperature,
                lighting.Unknown2, lighting.LightDistance, lighting.Unknown4);
        }
        set
        {
            var state = LiveState();
            if (state == null)
                return;
            ref var lighting = ref state->Lighting;
            lighting.SunlightColor = value.SunlightColor;
            lighting.MoonlightColor = value.MoonlightColor;
            lighting.AmbientColor = value.AmbientColor;
            lighting.Unknown1 = value.Unknown1;
            lighting.AmbientSaturation = value.AmbientSaturation;
            lighting.AmbientTemperature = value.AmbientTemperature;
            lighting.Unknown2 = value.Unknown2;
            lighting.LightDistance = value.LightDistance;
            lighting.Unknown4 = value.Unknown4;
            SetSectionHeld(EnvSection.Lighting, true);
        }
    }

    public EnvFogValues Fog
    {
        get
        {
            var state = LiveState();
            if (state == null)
                return default;
            ref var fog = ref state->Fog;
            return new EnvFogValues(
                fog.Color, fog.Distance, fog.Thickness, fog.SkySmoothness,
                fog.SkyOpacity, fog.FogOpacity, fog.SunVisibility);
        }
        set
        {
            var state = LiveState();
            if (state == null)
                return;
            ref var fog = ref state->Fog;
            fog.Color = value.Color;
            fog.Distance = value.Distance;
            fog.Thickness = value.Thickness;
            fog.SkySmoothness = value.SkySmoothness;
            fog.SkyOpacity = value.SkyOpacity;
            fog.FogOpacity = value.FogOpacity;
            fog.SunVisibility = value.SunVisibility;
            SetSectionHeld(EnvSection.Fog, true);
        }
    }

    public EnvRainValues Rain
    {
        get
        {
            var state = LiveState();
            if (state == null)
                return default;
            ref var rain = ref state->Rain;
            return new EnvRainValues(
                rain.Raindrops, rain.Intensity, rain.Weight, rain.Scatter, rain.Unknown1,
                rain.Size, rain.Color, rain.Unknown2, rain.Unknown3, rain.Unknown4);
        }
        set
        {
            var state = LiveState();
            if (state == null)
                return;
            ref var rain = ref state->Rain;
            rain.Raindrops = value.Raindrops;
            rain.Intensity = value.Intensity;
            rain.Weight = value.Weight;
            rain.Scatter = value.Scatter;
            rain.Unknown1 = value.Unknown1;
            rain.Size = value.Size;
            rain.Color = value.Color;
            rain.Unknown2 = value.Unknown2;
            rain.Unknown3 = value.Unknown3;
            rain.Unknown4 = value.Unknown4;
            SetSectionHeld(EnvSection.Rain, true);
        }
    }

    public EnvParticlesValues Particles
    {
        get
        {
            var state = LiveState();
            if (state == null)
                return default;
            ref var particles = ref state->Particles;
            return new EnvParticlesValues(
                particles.Unknown1, particles.Intensity, particles.Weight, particles.Spread,
                particles.Speed, particles.Size, particles.Color, particles.Glow,
                particles.Spin, particles.TextureId);
        }
        set
        {
            var state = LiveState();
            if (state == null)
                return;
            ref var particles = ref state->Particles;
            particles.Unknown1 = value.Unknown1;
            particles.Intensity = value.Intensity;
            particles.Weight = value.Weight;
            particles.Spread = value.Spread;
            particles.Speed = value.Speed;
            particles.Size = value.Size;
            particles.Color = value.Color;
            particles.Glow = value.Glow;
            particles.Spin = value.Spin;
            particles.TextureId = value.TextureId;
            SetSectionHeld(EnvSection.Particles, true);
        }
    }

    public EnvStarsValues Stars
    {
        get
        {
            var state = LiveState();
            if (state == null)
                return default;
            ref var stars = ref state->Stars;
            return new EnvStarsValues(
                stars.ConstellationIntensity, stars.ConstellationCount, stars.StarCount,
                stars.GalaxyIntensity, stars.StarIntensity, stars.MoonColor, stars.MoonBrightness);
        }
        set
        {
            var state = LiveState();
            if (state == null)
                return;
            ref var stars = ref state->Stars;
            stars.ConstellationIntensity = value.ConstellationIntensity;
            stars.ConstellationCount = value.ConstellationCount;
            stars.StarCount = value.StarCount;
            stars.GalaxyIntensity = value.GalaxyIntensity;
            stars.StarIntensity = value.StarIntensity;
            stars.MoonColor = value.MoonColor;
            stars.MoonBrightness = value.MoonBrightness;
            SetSectionHeld(EnvSection.Stars, true);
        }
    }

    public EnvWindValues Wind
    {
        get
        {
            var state = LiveState();
            if (state == null)
                return default;
            ref var wind = ref state->Wind;
            return new EnvWindValues(wind.Direction, wind.Angle, wind.Speed);
        }
        set
        {
            var state = LiveState();
            if (state == null)
                return;
            ref var wind = ref state->Wind;
            wind.Direction = value.Direction;
            wind.Angle = value.Angle;
            wind.Speed = value.Speed;
            SetSectionHeld(EnvSection.Wind, true);
        }
    }

    /// <summary>
    /// The destination's pre-copy values are the held state. Snapshotting them
    /// before the original runs and stamping the held sections back afterwards
    /// is what makes a section survive; a released section is simply left as
    /// the game just wrote it.
    /// </summary>
    private nint EnvStateCopyDetour(EnvStateNative* dest, EnvStateNative* src)
    {
        EnvStateNative? previous = _held != SectionFlags.None && dest != null ? *dest : null;
        var result = _envStateHook!.Original(dest, src);
        if (previous is { } state)
            ReStamp(dest, state);
        return result;
    }

    private void ReStamp(EnvStateNative* dest, EnvStateNative state)
    {
        if ((_held & SectionFlags.Lighting) != 0)
            dest->Lighting = state.Lighting;
        if ((_held & SectionFlags.Stars) != 0)
            dest->Stars = state.Stars;
        if ((_held & SectionFlags.Fog) != 0)
            dest->Fog = state.Fog;
        if ((_held & SectionFlags.Clouds) != 0)
            dest->Clouds = state.Clouds;
        if ((_held & SectionFlags.Rain) != 0)
            dest->Rain = state.Rain;
        if ((_held & SectionFlags.Particles) != 0)
            dest->Particles = state.Particles;
        if ((_held & SectionFlags.Wind) != 0)
            dest->Wind = state.Wind;
        // Sky carries the fog member the sky editor drives, exactly as the
        // reference stamps it; it runs last so a held Fog does not lose it.
        if ((_held & SectionFlags.Sky) != 0)
        {
            dest->SkyTextureId = state.SkyTextureId;
            dest->Fog.SunVisibility = state.Fog.SunVisibility;
        }
    }

    // ── Lifetime ──────────────────────────────────────────────────────

    private void OnGPoseStateChanged(GPoseStateChangedEvent evt)
    {
        if (evt.IsGPosing)
            return;
        if (ResetTimeOnGPoseExit)
            IsTimeFrozen = false;
        if (ResetWeatherOnGPoseExit)
            IsWeatherOverrideEnabled = false;
        if (ResetSectionsOnGPoseExit)
            ReleaseAllSections();
    }

    private void OnTerritoryChanged(uint territory)
    {
        // The zone's weather table is gone; every hold is released so the new
        // zone starts on its own weather and clock.
        _cachedTerritory = null;
        _territoryWeathers.Clear();
        IsWeatherOverrideEnabled = false;
        IsTimeFrozen = false;
    }

    private void OnLogout(int type, int code)
    {
        IsWeatherOverrideEnabled = false;
        IsTimeFrozen = false;
    }

    public void Dispose()
    {
        _clientState.TerritoryChanged -= OnTerritoryChanged;
        _clientState.Logout -= OnLogout;
        _events.Unsubscribe(_onGPoseStateChanged);

        _held = SectionFlags.None;
        _timeHook?.Dispose();
        _weatherHook?.Dispose();
        _envStateHook?.Dispose();

        _territoryWeathers.Clear();
        _allWeathers.Clear();
        _weatherById.Clear();

        GC.SuppressFinalize(this);
    }
}
