using System;
using Dalamud.Game;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.System.Framework;
using Poser.Core;
using Poser.Services;

namespace Poser.Game;

/// <summary>
/// Service for controlling in-game time (Eorzea Time).
/// Uses hooks to freeze time advancement.
/// </summary>
public unsafe class TimeService : ITimeService
{
    private readonly IPluginLog _log;
    private readonly IClientState _clientState;
    private readonly IEventBus _eventBus;

    private delegate void UpdateEorzeaTimeDelegate(nint a1, nint a2);
    private readonly Hook<UpdateEorzeaTimeDelegate>? _updateEorzeaTimeHook;
    private bool _resetOnGPoseExit = true;

    public TimeService(
        IPluginLog log,
        IClientState clientState,
        IEventBus eventBus,
        ISigScanner scanner,
        IGameInteropProvider hooking)
    {
        _log = log;
        _clientState = clientState;
        _eventBus = eventBus;

        try
        {
            var etAddress = scanner.ScanText("48 89 5C 24 ?? 57 48 83 EC ?? 48 8B F9 48 8B DA 48 81 C1 ?? ?? ?? ?? E8 ?? ?? ?? ?? 4C");
            _updateEorzeaTimeHook = hooking.HookFromAddress<UpdateEorzeaTimeDelegate>(etAddress, UpdateEorzeaTimeDetour);
            _log.Debug("TimeService: Hook initialized");
        }
        catch (Exception ex)
        {
            _log.Warning($"TimeService: Failed to hook UpdateEorzeaTime: {ex.Message}");
        }

        _clientState.TerritoryChanged += OnTerritoryChanged;
        _clientState.Logout += OnLogout;
        _eventBus.Subscribe<GPoseStateChangedEvent>(OnGPoseStateChanged);
    }

    public bool IsTimeFrozen
    {
        get => _updateEorzeaTimeHook?.IsEnabled ?? false;
        set
        {
            if (_updateEorzeaTimeHook == null)
                return;

            if (value != IsTimeFrozen)
            {
                if (value)
                    _updateEorzeaTimeHook.Enable();
                else
                    _updateEorzeaTimeHook.Disable();
            }
        }
    }

    public bool ResetOnGPoseExit
    {
        get => _resetOnGPoseExit;
        set => _resetOnGPoseExit = value;
    }

    public long EorzeaTime
    {
        get
        {
            var framework = Framework.Instance();
            if (framework == null)
                return 0;

            return framework->ClientTime.IsEorzeaTimeOverridden
                ? framework->ClientTime.EorzeaTimeOverride
                : framework->ClientTime.EorzeaTime;
        }
        set
        {
            var framework = Framework.Instance();
            if (framework == null)
                return;

            framework->ClientTime.EorzeaTime = value;
            if (framework->ClientTime.IsEorzeaTimeOverridden)
                framework->ClientTime.EorzeaTimeOverride = value;
        }
    }

    public int MinuteOfDay
    {
        get
        {
            long currentTime = EorzeaTime;
            long timeVal = currentTime % 2764800;
            long secondInDay = timeVal % 86400;
            int minuteOfDay = (int)(secondInDay / 60f);
            return minuteOfDay;
        }
        set
        {
            EorzeaTime = value * 60 + 86400 * ((byte)DayOfMonth - 1);
        }
    }

    public int DayOfMonth
    {
        get
        {
            long currentTime = EorzeaTime;
            long timeVal = currentTime % 2764800;
            int dayOfMonth = (int)(MathF.Floor(timeVal / 86400f) + 1);
            return dayOfMonth;
        }
        set
        {
            EorzeaTime = MinuteOfDay * 60 + 86400 * ((byte)value - 1);
        }
    }

    public (int hours, int minutes) GetTimeOfDay()
    {
        int minuteOfDay = MinuteOfDay;
        return (minuteOfDay / 60, minuteOfDay % 60);
    }

    public void SetTimeOfDay(int hours, int minutes)
    {
        MinuteOfDay = hours * 60 + minutes;
    }

    private void UpdateEorzeaTimeDetour(nint a1, nint a2)
    {
        // Do nothing - prevents time from advancing
    }

    private void OnTerritoryChanged(ushort territoryId)
    {
        IsTimeFrozen = false;
    }

    private void OnLogout(int type, int code)
    {
        IsTimeFrozen = false;
    }

    private void OnGPoseStateChanged(GPoseStateChangedEvent e)
    {
        if (!e.IsGPosing && _resetOnGPoseExit)
        {
            IsTimeFrozen = false;
        }
    }

    public void Dispose()
    {
        _clientState.TerritoryChanged -= OnTerritoryChanged;
        _clientState.Logout -= OnLogout;
        _eventBus.Unsubscribe<GPoseStateChangedEvent>(OnGPoseStateChanged);
        _updateEorzeaTimeHook?.Dispose();
        GC.SuppressFinalize(this);
    }
}
