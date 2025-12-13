using System;

namespace Poser.Services;

/// <summary>
/// Service for controlling in-game time (Eorzea Time).
/// </summary>
public interface ITimeService : IDisposable
{
    /// <summary>
    /// Whether time is frozen (not advancing).
    /// </summary>
    bool IsTimeFrozen { get; set; }

    /// <summary>
    /// Whether to reset time freeze when exiting GPose.
    /// </summary>
    bool ResetOnGPoseExit { get; set; }

    /// <summary>
    /// Current Eorzea time in seconds since epoch.
    /// </summary>
    long EorzeaTime { get; set; }

    /// <summary>
    /// Current minute of the day (0-1439).
    /// </summary>
    int MinuteOfDay { get; set; }

    /// <summary>
    /// Current day of the month (1-32).
    /// </summary>
    int DayOfMonth { get; set; }

    /// <summary>
    /// Gets the time as hours and minutes (for UI display).
    /// </summary>
    (int hours, int minutes) GetTimeOfDay();

    /// <summary>
    /// Sets the time from hours and minutes.
    /// </summary>
    void SetTimeOfDay(int hours, int minutes);
}
