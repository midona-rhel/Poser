using System;
using System.Collections.Generic;
using Poser.Entities;

namespace Poser.IPC;

/// <summary>
/// Service for integrating with CustomizePlus body scaling.
/// </summary>
public interface ICustomizePlusService : IDisposable
{
    /// <summary>
    /// Whether CustomizePlus is available.
    /// </summary>
    bool IsAvailable { get; }

    /// <summary>
    /// Current status of the CustomizePlus integration.
    /// </summary>
    IPCStatus Status { get; }

    /// <summary>
    /// Gets all available profiles.
    /// </summary>
    /// <returns>List of profile ID and name tuples.</returns>
    IReadOnlyList<(Guid Id, string Name)> GetProfiles();

    /// <summary>
    /// Gets the current active profile for an actor.
    /// </summary>
    /// <param name="actor">The actor to check.</param>
    /// <returns>Profile ID or null if none active.</returns>
    Guid? GetActiveProfile(IActor actor);

    /// <summary>
    /// Sets a profile for an actor.
    /// </summary>
    /// <param name="actor">The actor to modify.</param>
    /// <param name="profileId">The profile ID to apply.</param>
    void SetProfile(IActor actor, Guid profileId);

    /// <summary>
    /// Clears the profile from an actor.
    /// </summary>
    /// <param name="actor">The actor to clear.</param>
    void ClearProfile(IActor actor);
}
