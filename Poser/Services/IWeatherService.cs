using System;
using System.Collections.Generic;

namespace Poser.Services;

/// <summary>
/// Represents a weather type with ID and name.
/// </summary>
public readonly struct WeatherInfo
{
    public uint Id { get; init; }
    public string Name { get; init; }
    public string Icon { get; init; }

    public WeatherInfo(uint id, string name, string icon = "")
    {
        Id = id;
        Name = name;
        Icon = icon;
    }

    public static WeatherInfo None => new(0, "None");
}

/// <summary>
/// Service for controlling in-game weather.
/// </summary>
public interface IWeatherService : IDisposable
{
    /// <summary>
    /// Whether weather override is active.
    /// </summary>
    bool IsWeatherOverrideEnabled { get; set; }

    /// <summary>
    /// Whether to reset weather override when exiting GPose.
    /// </summary>
    bool ResetOnGPoseExit { get; set; }

    /// <summary>
    /// Current weather ID.
    /// </summary>
    uint CurrentWeatherId { get; set; }

    /// <summary>
    /// Gets the available weathers for the current territory.
    /// </summary>
    IReadOnlyList<WeatherInfo> TerritoryWeathers { get; }

    /// <summary>
    /// Gets all available weathers in the game.
    /// </summary>
    IReadOnlyList<WeatherInfo> AllWeathers { get; }

    /// <summary>
    /// Gets info for a specific weather ID.
    /// </summary>
    WeatherInfo? GetWeatherInfo(uint weatherId);

    /// <summary>
    /// Sets the weather with a smooth transition.
    /// </summary>
    void SetWeather(uint weatherId, float transitionTime = 0.5f);
}
