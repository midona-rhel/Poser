using System;
using System.Collections.Generic;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using Lumina.Excel;
using Poser.Domain.Scene;
using Poser.Application.Events;
using CSEnvManager = FFXIVClientStructs.FFXIV.Client.Graphics.Environment.EnvManager;
using CSFramework = FFXIVClientStructs.FFXIV.Client.System.Framework.Framework;
using CSHousingManager = FFXIVClientStructs.FFXIV.Client.Game.HousingManager;
using WeatherRow = Lumina.Excel.Sheets.Weather;

namespace Poser.Game.Environment;

internal delegate void UpdateEorzeaTimeDelegate(nint a1, nint a2);

internal unsafe delegate nint UpdateEnvironmentDelegate(CSEnvManager* manager, float a2, float a3);

internal unsafe delegate nint EnvStateCopyDelegate(EnvStateNative* dest, EnvStateNative* src);

/// <summary>A game hook the environment turns on and off. The tests in
/// Poser.Game.Tests replace the hooks with stand-ins.</summary>
internal interface IEnvHook : IDisposable
{
    bool IsEnabled { get; }
    void Enable();
    void Disable();
}

internal unsafe interface IEnvStateCopyHook : IEnvHook
{
    nint Original(EnvStateNative* dest, EnvStateNative* src);
}

internal unsafe interface IEnvWeatherHook : IEnvHook
{
    nint Original(CSEnvManager* manager, float a2, float a3);
}

internal unsafe interface IEnvironmentNativeFactory
{
    IEnvHook CreateTimeHook(
        ISigScanner scanner, IGameInteropProvider hooking, UpdateEorzeaTimeDelegate detour);
    IEnvWeatherHook CreateWeatherHook(
        ISigScanner scanner, IGameInteropProvider hooking, UpdateEnvironmentDelegate detour);
    IEnvStateCopyHook CreateEnvStateCopyHook(
        ISigScanner scanner, IGameInteropProvider hooking, EnvStateCopyDelegate detour);
}

internal readonly record struct HousingBrightnessState(float Current, float Target, float Saved);

internal interface IHousingBrightnessNative
{
    bool TryRead(out HousingBrightnessState state);
    bool TryWrite(float target);
}

internal sealed unsafe class HousingBrightnessNative : IHousingBrightnessNative
{
    public bool TryRead(out HousingBrightnessState state)
    {
        var manager = CSHousingManager.Instance();
        var indoor = manager == null || !manager->IsInside() ? null : manager->IndoorTerritory;
        if (indoor == null)
        {
            state = default;
            return false;
        }
        state = new(indoor->BrightnessCurrent, indoor->BrightnessTarget,
            1f - indoor->SavedInvertedBrightness * 0.2f);
        return true;
    }

    public bool TryWrite(float target)
    {
        var manager = CSHousingManager.Instance();
        var indoor = manager == null || !manager->IsInside() ? null : manager->IndoorTerritory;
        if (indoor == null)
            return false;
        // Brio follows the same transition fields. Never write the player's
        // SavedInvertedBrightness preference.
        indoor->BrightnessTarget = target;
        indoor->BrightnessTransitionSpeed = target - indoor->BrightnessCurrent;
        indoor->IsBrightnessTransitioning = true;
        return true;
    }
}

internal sealed unsafe class EnvironmentNativeFactory : IEnvironmentNativeFactory
{
    public IEnvHook CreateTimeHook(
        ISigScanner scanner, IGameInteropProvider hooking, UpdateEorzeaTimeDelegate detour) =>
        // Framework.TaskBeginFn by its lea in Framework::Setup: the body has a
        // byte-identical dead twin, so a body signature matches twice.
        new DalamudEnvHook<UpdateEorzeaTimeDelegate>(hooking.HookFromAddress(
            Poser.Game.Natives.NativeData.TryResolve(scanner, Poser.Game.Natives.NativeData.Framework_TaskBeginFn, out var address)
                && address != 0
                ? address
                : throw new InvalidOperationException("Framework.TaskBeginFn not found"),
            detour));

    public IEnvWeatherHook CreateWeatherHook(
        ISigScanner scanner, IGameInteropProvider hooking, UpdateEnvironmentDelegate detour) =>
        new DalamudEnvWeatherHook(hooking.HookFromAddress(
            scanner.ScanText(Poser.Game.Natives.NativeSignatures.EnvManager_Update),
            detour));

    public IEnvStateCopyHook CreateEnvStateCopyHook(
        ISigScanner scanner, IGameInteropProvider hooking, EnvStateCopyDelegate detour) =>
        new DalamudEnvCopyHook(hooking.HookFromAddress(
            scanner.ScanText(Poser.Game.Natives.NativeSignatures.EnvState_CopyFrom),
            detour));

    private class DalamudEnvHook<T>(Hook<T> hook) : IEnvHook where T : Delegate
    {
        protected readonly Hook<T> Hook = hook;
        public bool IsEnabled => Hook.IsEnabled;
        public void Enable() => Hook.Enable();
        public void Disable() => Hook.Disable();
        public void Dispose() => Hook.Dispose();
    }

    private sealed class DalamudEnvCopyHook(Hook<EnvStateCopyDelegate> hook)
        : DalamudEnvHook<EnvStateCopyDelegate>(hook), IEnvStateCopyHook
    {
        public nint Original(EnvStateNative* dest, EnvStateNative* src) =>
            Hook.Original(dest, src);
    }

    private sealed class DalamudEnvWeatherHook(Hook<UpdateEnvironmentDelegate> hook)
        : DalamudEnvHook<UpdateEnvironmentDelegate>(hook), IEnvWeatherHook
    {
        public nint Original(CSEnvManager* manager, float a2, float a3) =>
            Hook.Original(manager, a2, a3);
    }
}

/// <summary>
/// Reads and writes the time, weather, sky sections and housing interior
/// brightness. Freezing time stops the game's clock update. A held weather is
/// written again before every game environment update. A held section keeps
/// Poser's values: after the game copies its own values in, the held ones are
/// written back. Releasing a section lets the game's next copy replace it.
/// Values are read from the game each time and written straight to it, so a
/// section that is not held is overwritten by the game on its next update.
/// Call from the game thread.
/// </summary>
public sealed unsafe class EnvironmentService : IDisposable
{
    private const float DefaultTransitionTime = 0.5f;
    // The Eorzean month has 32 days of 86400 Eorzean seconds.
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
    private readonly IHousingBrightnessNative _housingBrightness;
    private readonly Action<GPoseStateChangedEvent> _onGPoseStateChanged;

    private readonly IEnvHook? _timeHook;
    private readonly IEnvWeatherHook? _weatherHook;
    private byte? _heldWeather;
#if DEBUG
    public uint? WeatherRequested { get; private set; }
    public byte WeatherBeforeUpdate { get; private set; }
    public byte WeatherAfterUpdate { get; private set; }
    public int WeatherUpdateCount { get; private set; }
#endif
    private readonly IEnvStateCopyHook? _envStateHook;

    private readonly List<WeatherInfo> _allWeathers = new();
    private readonly Dictionary<uint, WeatherInfo> _weatherById = new();
    private readonly List<WeatherInfo> _territoryWeathers = new();
    private uint? _cachedTerritory;

    private SectionFlags _held = SectionFlags.None;
    private ulong _housingBinding = 1;
    private float? _housingBaseline;

    public bool ResetTimeOnGPoseExit { get; set; } = true;
    public bool ResetWeatherOnGPoseExit { get; set; } = true;
    public bool ResetSectionsOnGPoseExit { get; set; } = true;

    /// <summary>False when the clock hook was not found: the clock can be
    /// read, but not set or frozen.</summary>
    public bool IsTimeFreezeAvailable => _timeHook != null;
    public bool IsWeatherOverrideAvailable => _weatherHook != null;

    /// <summary>False when the section hook was not found or not started:
    /// sections can be written, but the game takes them back at once.</summary>
    public bool IsSectionHoldAvailable => _envStateHook?.IsEnabled == true;

    public EnvironmentService(
        IClientState clientState,
        ISigScanner sigScanner,
        IGameInteropProvider hooking,
        IDataManager data,
        IPluginLog log,
        IEventBus events)
        : this(clientState, sigScanner, hooking, data, log, events,
            new EnvironmentNativeFactory(), new HousingBrightnessNative())
    {
    }

    internal EnvironmentService(
        IClientState clientState,
        ISigScanner sigScanner,
        IGameInteropProvider hooking,
        IDataManager data,
        IPluginLog log,
        IEventBus events,
        IEnvironmentNativeFactory nativeFactory,
        IHousingBrightnessNative housingBrightness)
    {
        _clientState = clientState;
        _log = log;
        _events = events;
        _housingBrightness = housingBrightness;

        _timeHook = TryCreate(
            () => nativeFactory.CreateTimeHook(sigScanner, hooking, UpdateEorzeaTimeDetour),
            "time freeze");

        _weatherHook = TryCreate(
            () => nativeFactory.CreateWeatherHook(sigScanner, hooking, UpdateEnvironmentDetour),
            "weather hold");

        _envStateHook = TryCreate(
            () => nativeFactory.CreateEnvStateCopyHook(sigScanner, hooking, EnvStateCopyDetour),
            "environment section hold");

        // Unlike the other two, this hook stays on the whole time: while no
        // section is held it only calls the game's own copy.
        try
        {
            _envStateHook?.Enable();
        }
        catch (Exception ex)
        {
            _log.Warning($"Environment: section hold hook could not be enabled ({ex.Message}); sections cannot be held.");
        }

        var none = new WeatherInfo(0, "None", 0);
        _allWeathers.Add(none);
        _weatherById.Add(0, none);
        try
        {
            BuildAllWeathers(data.GetExcelSheet<WeatherRow>());
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

    private THook? TryCreate<THook>(Func<THook> create, string capability)
        where THook : class, IEnvHook
    {
        try
        {
            return create();
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

    /// <summary>The game's override clock while it runs one, otherwise its
    /// normal clock. The world shows the override when there is one.</summary>
    private long EorzeaTime
    {
        get
        {
            var framework = CSFramework.Instance();
            return framework->ClientTime.IsEorzeaTimeOverridden
                ? framework->ClientTime.EorzeaTimeOverride
                : framework->ClientTime.EorzeaTime;
        }
        set
        {
            var framework = CSFramework.Instance();
            framework->ClientTime.EorzeaTime = value;
            if (framework->ClientTime.IsEorzeaTimeOverridden)
                framework->ClientTime.EorzeaTimeOverride = value;
        }
    }

    /// <summary>Eorzean minute of the day, 0 to 1439. Setting it freezes the
    /// clock, because a running clock would overwrite the value in the next
    /// frame.</summary>
    public int MinuteOfDay
    {
        get => (int)(EorzeaTime % MonthSeconds % DaySeconds / 60);
        set
        {
            IsTimeFrozen = true;
            EorzeaTime = Math.Clamp(value, 0, 1439) * 60 + DaySeconds * (DayOfMonth - 1);
        }
    }

    /// <summary>Eorzean day of the month. The game's month has 32 days; only
    /// 1 to 31 can be set, as in Brio and Ktisis. Setting it freezes the clock.</summary>
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
        // Not calling the game's clock update is what freezes the clock.
    }

    // ── Weather ───────────────────────────────────────────────────────

    /// <summary>Holds the weather: the game's own weather change for the area
    /// is replaced by the held weather on every update.</summary>
    public bool IsWeatherOverrideEnabled
    {
        get => _weatherHook?.IsEnabled == true;
        set
        {
            if (_weatherHook == null || value == IsWeatherOverrideEnabled)
                return;
            if (value)
            {
                // Holding without picking a weather keeps the weather the game
                // shows at its next update. SetWeather picks one right after.
                _heldWeather = null;
                _weatherHook.Enable();
            }
            else
            {
                _weatherHook.Disable();
                _heldWeather = null;
            }
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

    /// <summary>Shows a weather, blending into it over
    /// <paramref name="transitionTime"/> seconds, and holds it. Without the
    /// hold the game would change it back at its next weather update.</summary>
    public void SetWeather(uint id, float transitionTime = DefaultTransitionTime) =>
        SetWeather(CSEnvManager.Instance(), id, transitionTime);

    internal void SetWeather(CSEnvManager* manager, uint id, float transitionTime)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(id, byte.MaxValue);
        if (manager == null || !IsWeatherOverrideAvailable)
            return;
        IsWeatherOverrideEnabled = true;
        _heldWeather = (byte)id;
#if DEBUG
        WeatherRequested = id;
        WeatherUpdateCount = 0;
#endif
        manager->ActiveWeather = (byte)id;
        manager->TransitionTime = transitionTime;
    }

    /// <summary>Every named weather in the game data, with None first.</summary>
    public IReadOnlyList<WeatherInfo> AllWeathers => _allWeathers;

    public WeatherInfo? GetWeatherInfo(uint id)
        => _weatherById.TryGetValue(id, out var info) ? info : null;

    /// <summary>The weathers this area has on its own, sorted by id, each once.</summary>
    public IReadOnlyList<WeatherInfo> TerritoryWeathers
    {
        get
        {
            // The game fills in the area's weather table some frames after a
            // zone change, so an empty list is read again next time instead of
            // being kept.
            if (_cachedTerritory != _clientState.TerritoryType)
            {
                UpdateTerritoryWeathers();
                if (_territoryWeathers.Count > 0)
                    _cachedTerritory = _clientState.TerritoryType;
            }
            return _territoryWeathers;
        }
    }

    private void BuildAllWeathers(ExcelSheet<WeatherRow> sheet)
    {
        foreach (var row in sheet)
        {
            // The game stores the active weather in one byte, so rows above
            // 255 can't be shown. Row 0 is the None entry added above.
            if (row.RowId == 0 || row.RowId > byte.MaxValue)
                continue;
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

        // Brio and Ktisis read this table at offset 0x2C; ClientStructs maps
        // it at 0x30, which matches the current game.
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

    private nint UpdateEnvironmentDetour(CSEnvManager* manager, float a2, float a3)
    {
#if DEBUG
        WeatherBeforeUpdate = manager->ActiveWeather;
#endif
        // Like Ktisis, the held weather is written before every update, because
        // the game can change the weather in other places too. The transition
        // time is left alone so the blend into the weather keeps going.
        if (IsWeatherOverrideEnabled)
        {
            _heldWeather ??= manager->ActiveWeather;
            manager->ActiveWeather = _heldWeather.Value;
        }
        var result = _weatherHook!.Original(manager, a2, a3);
#if DEBUG
        WeatherAfterUpdate = manager->ActiveWeather;
        WeatherUpdateCount++;
#endif
        return result;
    }

    // ── Housing interior brightness ───────────────────────────────────
    // The binding is a number that changes every time the player enters or
    // leaves an interior. A write that names an older binding is refused, so
    // an undo step made in one house never writes into another.

    public bool IsHousingInterior => _housingBrightness.TryRead(out _);
    public float? InteriorBrightness =>
        _housingBrightness.TryRead(out var state) ? state.Target : null;

    /// <summary>True while Poser holds the brightness and remembers the value
    /// the interior had before.</summary>
    public bool IsInteriorBrightnessOverridden => _housingBaseline.HasValue;
    public ulong HousingInteriorBinding => _housingBinding;

    /// <summary>Writes a brightness from 0 to 1. The first write remembers the
    /// interior's own value so Release can put it back.</summary>
    public bool TrySetInteriorBrightness(float value, ulong binding)
    {
        if (binding != _housingBinding || !_housingBrightness.TryRead(out var state))
            return false;
        if (!_housingBrightness.TryWrite(value))
            return false;
        _housingBaseline ??= state.Target;
        return true;
    }

    /// <summary>Writes the brightness the house has saved.</summary>

    public bool TryResetInteriorBrightness(ulong binding)
    {
        if (binding != _housingBinding ||
            !_housingBrightness.TryRead(out var state) ||
            !_housingBrightness.TryWrite(state.Saved))
            return false;
        _housingBaseline ??= state.Target;
        return true;
    }

    /// <summary>Writes back the brightness the interior had before Poser's
    /// first write, and stops holding it. The house's saved setting is never
    /// changed.</summary>
    public bool ReleaseInteriorBrightness(ulong binding)
    {
        if (binding != _housingBinding || _housingBaseline is not { } baseline)
            return false;
        if (!_housingBrightness.TryWrite(baseline))
            return false;
        _housingBaseline = null;
        return true;
    }

    private void InvalidateInteriorBrightness()
    {
        _housingBaseline = null;
        _housingBinding++;
    }

    // ── Environment sections ──────────────────────────────────────────
    // Setting any section's values below also holds that section.

    public bool IsSectionHeld(EnvSection section) => (_held & Flag(section)) != 0;

    /// <summary>Releasing needs no restore: the game's next copy writes its own
    /// values back.</summary>
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

    /// <summary>The environment values the game is showing now. Null while the
    /// game's environment manager does not exist.</summary>
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
    /// Runs when the game copies new environment values over the shown ones.
    /// The shown values before the copy are Poser's held values; they are
    /// saved first and the held sections are written back after the copy.
    /// Sections that are not held keep what the game just wrote.
    /// </summary>
    private nint EnvStateCopyDetour(EnvStateNative* dest, EnvStateNative* src)
    {
        EnvStateNative? previous = _held != SectionFlags.None ? *dest : null;
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
        // The sky section includes the sun visibility, which the game keeps
        // inside the fog values, as Ktisis does. It is written last so a held
        // fog section doesn't overwrite it.
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
        ReleaseInteriorBrightness(_housingBinding);
        InvalidateInteriorBrightness();
    }

    private void OnTerritoryChanged(uint territory)
    {
        _cachedTerritory = null;
        _territoryWeathers.Clear();
        // The event comes after the game has already moved to the new area,
        // so the old interior's brightness is not written back: it would land
        // in whatever interior the player is in now.
        InvalidateInteriorBrightness();
        ReleaseAllHolds();
    }

    private void OnLogout(int type, int code)
    {
        ReleaseInteriorBrightness(_housingBinding);
        InvalidateInteriorBrightness();
        ReleaseAllHolds();
    }

    /// <summary>
    /// A new area or a logout starts on the game's own clock, weather and
    /// sections, as Brio does for the clock and weather. Held sections must be
    /// released too, or the old area's values would be written into the new one.
    /// </summary>
    private void ReleaseAllHolds()
    {
        ReleaseAllSections();
        IsWeatherOverrideEnabled = false;
        IsTimeFrozen = false;
    }

    public void Dispose()
    {
        ReleaseInteriorBrightness(_housingBinding);
        InvalidateInteriorBrightness();
        _clientState.TerritoryChanged -= OnTerritoryChanged;
        _clientState.Logout -= OnLogout;
        _events.Unsubscribe(_onGPoseStateChanged);

        _timeHook?.Dispose();
        _weatherHook?.Dispose();
        _envStateHook?.Dispose();
    }
}
