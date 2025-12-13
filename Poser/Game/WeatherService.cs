using System;
using System.Collections.Generic;
using Dalamud.Game;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Graphics.Environment;
using Lumina.Excel.Sheets;
using Poser.Core;
using Poser.Services;

namespace Poser.Game;

/// <summary>
/// Service for controlling in-game weather.
/// Uses hooks to freeze weather changes and provides access to territory weather lists.
/// </summary>
public unsafe class WeatherService : IWeatherService
{
    private readonly IPluginLog _log;
    private readonly IClientState _clientState;
    private readonly IDataManager _dataManager;
    private readonly IEventBus _eventBus;

    private delegate void UpdateTerritoryWeatherDelegate(nint a1, nint a2);
    private readonly Hook<UpdateTerritoryWeatherDelegate>? _updateTerritoryWeatherHook;

    private readonly List<WeatherInfo> _territoryWeathers = new();
    private readonly List<WeatherInfo> _allWeathers = new();
    private ushort? _cachedTerritoryId;
    private bool _resetOnGPoseExit = true;

    private const float DefaultTransitionTime = 0.5f;

    public WeatherService(
        IPluginLog log,
        IClientState clientState,
        IDataManager dataManager,
        IEventBus eventBus,
        ISigScanner scanner,
        IGameInteropProvider hooking)
    {
        _log = log;
        _clientState = clientState;
        _dataManager = dataManager;
        _eventBus = eventBus;

        // Load all weathers from game data
        LoadAllWeathers();

        try
        {
            var twAddress = scanner.ScanText("48 89 5C 24 ?? 55 56 57 48 83 EC ?? 48 8B F9 48 8D 0D");
            _updateTerritoryWeatherHook = hooking.HookFromAddress<UpdateTerritoryWeatherDelegate>(twAddress, UpdateTerritoryWeatherDetour);
            _log.Debug("WeatherService: Hook initialized");
        }
        catch (Exception ex)
        {
            _log.Warning($"WeatherService: Failed to hook UpdateTerritoryWeather: {ex.Message}");
        }

        UpdateTerritoryWeathers();

        _clientState.TerritoryChanged += OnTerritoryChanged;
        _clientState.Logout += OnLogout;
        _eventBus.Subscribe<GPoseStateChangedEvent>(OnGPoseStateChanged);
    }

    public bool IsWeatherOverrideEnabled
    {
        get => _updateTerritoryWeatherHook?.IsEnabled ?? false;
        set
        {
            if (_updateTerritoryWeatherHook == null)
                return;

            if (value != IsWeatherOverrideEnabled)
            {
                if (value)
                    _updateTerritoryWeatherHook.Enable();
                else
                    _updateTerritoryWeatherHook.Disable();
            }
        }
    }

    public bool ResetOnGPoseExit
    {
        get => _resetOnGPoseExit;
        set => _resetOnGPoseExit = value;
    }

    public uint CurrentWeatherId
    {
        get
        {
            var envManager = EnvManager.Instance();
            if (envManager == null)
                return 0;
            return envManager->ActiveWeather;
        }
        set
        {
            var envManager = EnvManager.Instance();
            if (envManager != null)
            {
                envManager->ActiveWeather = (byte)value;
                envManager->TransitionTime = DefaultTransitionTime;
            }
        }
    }

    public IReadOnlyList<WeatherInfo> TerritoryWeathers
    {
        get
        {
            // Refresh cache if territory changed
            if (_cachedTerritoryId != _clientState.TerritoryType)
            {
                _cachedTerritoryId = null;
                UpdateTerritoryWeathers();
                if (_territoryWeathers.Count > 0)
                    _cachedTerritoryId = _clientState.TerritoryType;
            }
            return _territoryWeathers;
        }
    }

    public IReadOnlyList<WeatherInfo> AllWeathers => _allWeathers;

    public WeatherInfo? GetWeatherInfo(uint weatherId)
    {
        foreach (var weather in _allWeathers)
        {
            if (weather.Id == weatherId)
                return weather;
        }
        return null;
    }

    public void SetWeather(uint weatherId, float transitionTime = 0.5f)
    {
        var envManager = EnvManager.Instance();
        if (envManager != null)
        {
            envManager->ActiveWeather = (byte)weatherId;
            envManager->TransitionTime = transitionTime;
        }
    }

    private void LoadAllWeathers()
    {
        _allWeathers.Clear();

        var weatherSheet = _dataManager.GetExcelSheet<Weather>();
        if (weatherSheet == null)
        {
            _log.Warning("WeatherService: Failed to get Weather excel sheet");
            return;
        }

        foreach (var weather in weatherSheet)
        {
            var name = weather.Name.ExtractText();
            if (string.IsNullOrWhiteSpace(name))
                continue;

            var iconId = weather.Icon;
            _allWeathers.Add(new WeatherInfo(weather.RowId, name, iconId.ToString()));
        }

        _log.Debug($"WeatherService: Loaded {_allWeathers.Count} weathers");
    }

    private void UpdateTerritoryWeathers()
    {
        _territoryWeathers.Clear();

        var envManager = EnvManager.Instance();
        if (envManager == null)
            return;

        var scenePtr = (nint)envManager->EnvScene;
        if (scenePtr == 0)
            return;

        // Weather IDs are stored at offset 0x2C in EnvScene
        byte* weatherIds = (byte*)(scenePtr + 0x2C);

        for (int i = 0; i < 32; i++)
        {
            var weatherId = weatherIds[i];
            if (weatherId == 0)
                continue;

            var info = GetWeatherInfo(weatherId);
            if (info == null)
                continue;

            // Avoid duplicates
            bool exists = false;
            foreach (var existing in _territoryWeathers)
            {
                if (existing.Id == weatherId)
                {
                    exists = true;
                    break;
                }
            }

            if (!exists)
                _territoryWeathers.Add(info.Value);
        }

        // Sort by ID for consistent ordering
        _territoryWeathers.Sort((a, b) => a.Id.CompareTo(b.Id));
    }

    private void UpdateTerritoryWeatherDetour(nint a1, nint a2)
    {
        // Do nothing - prevents weather from changing
    }

    private void OnTerritoryChanged(ushort territoryId)
    {
        UpdateTerritoryWeathers();
        IsWeatherOverrideEnabled = false;
    }

    private void OnLogout(int type, int code)
    {
        IsWeatherOverrideEnabled = false;
    }

    private void OnGPoseStateChanged(GPoseStateChangedEvent e)
    {
        if (!e.IsGPosing && _resetOnGPoseExit)
        {
            IsWeatherOverrideEnabled = false;
        }
    }

    public void Dispose()
    {
        _clientState.TerritoryChanged -= OnTerritoryChanged;
        _clientState.Logout -= OnLogout;
        _eventBus.Unsubscribe<GPoseStateChangedEvent>(OnGPoseStateChanged);
        _updateTerritoryWeatherHook?.Dispose();
        _territoryWeathers.Clear();
        _allWeathers.Clear();
        GC.SuppressFinalize(this);
    }
}
