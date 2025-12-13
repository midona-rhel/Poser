using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Poser.Services;

namespace Poser.UI.Controls;

/// <summary>
/// Tab pane for controlling in-game time and weather.
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

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        DrawWeatherControls();
    }

    private void DrawTimeControls()
    {
        ImGui.TextColored(UIColors.Gray, "Time");
        ImGui.Spacing();

        if (_timeService == null)
        {
            ImGui.TextColored(UIColors.TextDisabled, "Time service unavailable");
            return;
        }

        // Time of day scrubber
        var (hours, minutes) = _timeService.GetTimeOfDay();
        float minuteOfDay = hours * 60 + minutes;
        using (var row = Flex.Row(gap: Flex.ItemGap))
        {
            row.Label("Time");
            row.Fill(w =>
            {
                if (DrawTimeScrubber("time_scrubber", ref minuteOfDay, w))
                {
                    int newHours = (int)(minuteOfDay / 60);
                    int newMinutes = (int)(minuteOfDay % 60);
                    _timeService.SetTimeOfDay(newHours, newMinutes);
                }
            });
        }

        // Day of month
        int dayOfMonth = _timeService.DayOfMonth;
        using (var row = Flex.Row(gap: Flex.ItemGap))
        {
            row.Label("Day");
            row.Fill(w =>
            {
                float dayFloat = dayOfMonth;
                if (Scrubber.Draw("day_scrubber", ref dayFloat, 1, 32, 1, w, 1f, "F0", ""))
                {
                    _timeService.DayOfMonth = (int)dayFloat;
                }
            });
        }
    }

    private void DrawWeatherControls()
    {
        ImGui.TextColored(UIColors.Gray, "Weather");
        ImGui.Spacing();

        if (_weatherService == null)
        {
            ImGui.TextColored(UIColors.TextDisabled, "Weather service unavailable");
            return;
        }

        // Weather dropdown
        RefreshWeatherList();

        if (_weatherNames == null || _weatherNames.Length == 0)
        {
            ImGui.TextColored(UIColors.TextDisabled, "No weathers available");
            return;
        }

        // Update selected index based on current weather
        uint currentWeatherId = _weatherService.CurrentWeatherId;
        for (int i = 0; i < _weatherIds!.Length; i++)
        {
            if (_weatherIds[i] == currentWeatherId)
            {
                _selectedWeatherIndex = i;
                break;
            }
        }

        // Weather dropdown - selecting auto-enables override
        using (var row = Flex.Row(gap: Flex.ItemGap))
        {
            row.Label("Weather");
            row.Fill(w =>
            {
                if (PoserDropdown.Draw("weather_dropdown", ref _selectedWeatherIndex, _weatherNames, w))
                {
                    if (_selectedWeatherIndex >= 0 && _selectedWeatherIndex < _weatherIds.Length)
                    {
                        _weatherService.IsWeatherOverrideEnabled = true;
                        _weatherService.SetWeather(_weatherIds[_selectedWeatherIndex]);
                    }
                }
            });
        }

        // Show all weathers checkbox
        using (var row = Flex.Row(gap: Flex.ItemGap))
        {
            row.Label("");
            row.Fixed(PoserCheckbox.Size / PoserUI.Scale, (w, h) =>
            {
                float offsetY = (h - PoserCheckbox.Size) / 2f;
                if (offsetY > 0) ImGui.SetCursorPosY(ImGui.GetCursorPosY() + offsetY);
                if (PoserCheckbox.Draw("show_all_weathers", ref _showAllWeathers))
                {
                    _weatherListDirty = true;
                }
            });
            row.Fill((w, h) =>
            {
                float offsetY = (h - ImGui.GetTextLineHeight()) / 2f;
                if (offsetY > 0) ImGui.SetCursorPosY(ImGui.GetCursorPosY() + offsetY);
                ImGui.Text("Show all weathers");
            });
        }
    }

    private void RefreshWeatherList()
    {
        if (_weatherService == null)
            return;

        var weathers = _showAllWeathers ? _weatherService.AllWeathers : _weatherService.TerritoryWeathers;

        // Only rebuild if dirty or count changed
        if (!_weatherListDirty && _weatherNames != null && _weatherNames.Length == weathers.Count)
            return;

        _weatherListDirty = false;
        _weatherNames = new string[weathers.Count];
        _weatherIds = new uint[weathers.Count];

        for (int i = 0; i < weathers.Count; i++)
        {
            _weatherNames[i] = weathers[i].Name;
            _weatherIds[i] = weathers[i].Id;
        }
    }

    /// <summary>
    /// Draws a time scrubber with formatted HH:MM display.
    /// </summary>
    private bool DrawTimeScrubber(string id, ref float minuteOfDay, float width)
    {
        // Format time as HH:MM
        int hours = (int)(minuteOfDay / 60) % 24;
        int minutes = (int)(minuteOfDay % 60);
        string timeText = $"{hours:D2}:{minutes:D2}";

        float scale = PoserUI.Scale;
        float textWidth = ImGui.CalcTextSize("00:00").X;
        float gap = Flex.ItemGap * scale;
        float trackWidth = width - textWidth - gap;

        bool changed = false;

        var cursorPos = ImGui.GetCursorPos();
        float height = Flex.RowHeight * scale;

        // Draw track portion using Scrubber logic
        if (Scrubber.Draw(id, ref minuteOfDay, 0, 1439, 0, trackWidth, 1f, "", ""))
        {
            changed = true;
        }

        // Draw formatted time text
        ImGui.SetCursorPos(cursorPos + new System.Numerics.Vector2(trackWidth + gap, (Flex.RowHeight * scale - ImGui.GetTextLineHeight()) / 2f));
        ImGui.Text(timeText);

        return changed;
    }
}
