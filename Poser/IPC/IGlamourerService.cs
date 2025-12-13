using System;
using System.Collections.Generic;
using Poser.Entities;

namespace Poser.IPC;

/// <summary>
/// Service for integrating with Glamourer appearance management.
/// </summary>
public interface IGlamourerService : IDisposable
{
    /// <summary>
    /// Whether Glamourer is available.
    /// </summary>
    bool IsAvailable { get; }

    /// <summary>
    /// Current status of the Glamourer integration.
    /// </summary>
    IPCStatus Status { get; }

    /// <summary>
    /// Gets all available designs.
    /// </summary>
    /// <returns>Dictionary of design ID to name.</returns>
    Dictionary<Guid, string> GetDesigns();

    /// <summary>
    /// Applies a design to an actor.
    /// </summary>
    /// <param name="actor">The actor to modify.</param>
    /// <param name="designId">The design ID to apply.</param>
    void ApplyDesign(IActor actor, Guid designId);

    /// <summary>
    /// Reverts an actor's appearance to original state.
    /// </summary>
    /// <param name="actor">The actor to revert.</param>
    void RevertAppearance(IActor actor);
}
