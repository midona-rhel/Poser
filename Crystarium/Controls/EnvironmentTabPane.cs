using System;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Poser.Services;

namespace Poser.UI.Controls;

/// <summary>
/// Tab pane for controlling in-game time and weather.
/// Uses the typed Crystarium element system.
/// </summary>
public class EnvironmentTabPane : ITabPane
{
    private readonly ITimeService? _timeService;
    private readonly IWeatherService? _weatherService;

    private int _selectedWeatherIndex = 0;
    private string[]? _weatherNames;
    private uint[]? _weatherIds;
    private bool _showAllWeathers = false;
    private bool _weatherListDirty = true;

    public string Name => "Environment";
    public FontAwesomeIcon? Icon => FontAwesomeIcon.Sun;
    public bool IsEnabled => _timeService != null || _weatherService != null;

    public EnvironmentTabPane(ITimeService? timeService, IWeatherService? weatherService)
    {
        _timeService = timeService;
        _weatherService = weatherService;
    }

    public void Draw()
    {
        DrawTimeControls();
        Crystarium.Separator();
        DrawWeatherControls();
    }

    private void DrawTimeControls()
    {
        Crystarium.Text("Time", Cls.Heading);

        if (_timeService == null)
        {
            Crystarium.Text("Time service unavailable", Cls.DisabledText);
            return;
        }

        var (hours, minutes) = _timeService.GetTimeOfDay();
        float minuteOfDay = hours * 60 + minutes;
        LabelRow("Time", () =>
        {
            if (DrawTimeScrubber("time_scrubber", ref minuteOfDay, Crystarium.AvailableWidth))
            {
                _timeService.SetTimeOfDay((int)(minuteOfDay / 60), (int)(minuteOfDay % 60));
            }
        });

        int dayOfMonth = _timeService.DayOfMonth;
        LabelRow("Day", () =>
        {
            float dayFloat = dayOfMonth;
            if (Crystarium.Scrubber("day_scrubber", ref dayFloat, 1, 32, new ScrubberProps
            {
                Step = 1,
                DisplayFormat = "F0",
            }))
                _timeService.DayOfMonth = (int)dayFloat;
        });
    }

    private void DrawWeatherControls()
    {
        Crystarium.Text("Weather", Cls.Heading);

        if (_weatherService == null)
        {
            Crystarium.Text("Weather service unavailable", Cls.DisabledText);
            return;
        }

        RefreshWeatherList();

        if (_weatherNames == null || _weatherNames.Length == 0)
        {
            Crystarium.Text("No weathers available", Cls.DisabledText);
            return;
        }

        uint currentWeatherId = _weatherService.CurrentWeatherId;
        for (int i = 0; i < _weatherIds!.Length; i++)
            if (_weatherIds[i] == currentWeatherId) { _selectedWeatherIndex = i; break; }

        LabelRow("Weather", () =>
        {
            if (Crystarium.Dropdown("weather_dropdown", _weatherNames, ref _selectedWeatherIndex))
            {
                if (_selectedWeatherIndex >= 0 && _selectedWeatherIndex < _weatherIds.Length)
                {
                    _weatherService.IsWeatherOverrideEnabled = true;
                    _weatherService.SetWeather(_weatherIds[_selectedWeatherIndex]);
                }
            }
        });

        // 3-cell row: empty label gutter + checkbox + label text
        Crystarium.Element(new ElementProps { Classes = Cls.Row }, () =>
        {
            Crystarium.Element(new ElementProps { Style = new ElementStyle { Width = Sizing.Fixed(70) } });
            Crystarium.Element(new ElementProps { Style = new ElementStyle { Width = Sizing.Fixed(Crystarium.CheckboxSize / PoserUI.Scale) } }, () =>
            {
                if (Crystarium.Checkbox("show_all_weathers", ref _showAllWeathers))
                    _weatherListDirty = true;
            });
            Crystarium.Element(new ElementProps { Style = new ElementStyle { Width = Sizing.Fill } }, () =>
                Crystarium.Text("Show all weathers"));
        });
    }

    private static void LabelRow(string label, Action input)
    {
        Crystarium.Element(new ElementProps { Classes = Cls.Row }, () =>
        {
            Crystarium.Text(label, Cls.Label);
            Crystarium.Element(new ElementProps { Style = new ElementStyle { Width = Sizing.Fill } }, input);
        });
    }

    private void RefreshWeatherList()
    {
        if (_weatherService == null) return;

        var weathers = _showAllWeathers ? _weatherService.AllWeathers : _weatherService.TerritoryWeathers;
        if (!_weatherListDirty && _weatherNames != null && _weatherNames.Length == weathers.Count) return;

        _weatherListDirty = false;
        _weatherNames = new string[weathers.Count];
        _weatherIds = new uint[weathers.Count];

        for (int i = 0; i < weathers.Count; i++)
        {
            _weatherNames[i] = weathers[i].Name;
            _weatherIds[i] = weathers[i].Id;
        }
    }

    private bool DrawTimeScrubber(string id, ref float minuteOfDay, float width)
    {
        int hours = (int)(minuteOfDay / 60) % 24;
        int minutes = (int)(minuteOfDay % 60);
        string timeText = $"{hours:D2}:{minutes:D2}";

        float scale = PoserUI.Scale;
        float textWidth = ImGui.CalcTextSize("00:00").X;
        float gap = Flex.ItemGap * scale;
        float trackWidth = width - textWidth - gap;

        var cursorPos = ImGui.GetCursorPos();
        bool changed = Crystarium.Scrubber(id, ref minuteOfDay, 0, 1439, new ScrubberProps
        {
            Step = 0,
            DisplayFormat = "",
            HideValue = true,
            Style = new ScrubberStyle { Width = Sizing.Fixed(trackWidth / scale) },
        });

        ImGui.SetCursorPos(cursorPos + new System.Numerics.Vector2(trackWidth + gap, (Flex.RowHeight * scale - ImGui.GetTextLineHeight()) / 2f));
        ImGui.Text(timeText);

        return changed;
    }
}
