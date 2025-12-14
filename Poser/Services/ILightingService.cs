using System;
using System.Collections.Generic;
using System.Numerics;
using Poser.Entities;
using Poser.Game.Structs;

namespace Poser.Services;

/// <summary>
/// Service for spawning and controlling lights in the scene.
/// Lights appear as entities in the scene hierarchy.
/// </summary>
public interface ILightingService : IDisposable
{
    /// <summary>
    /// Whether the service is available (signatures found).
    /// </summary>
    bool IsAvailable { get; }

    /// <summary>
    /// All spawned light entities.
    /// </summary>
    IReadOnlyList<LightEntity> SpawnedLights { get; }

    /// <summary>
    /// Whether a light is currently being placed interactively.
    /// </summary>
    bool IsPlacing { get; }

    /// <summary>
    /// The light currently being placed, if any.
    /// </summary>
    LightEntity? PlacingLight { get; }

    /// <summary>
    /// Event fired when lights are added or removed.
    /// </summary>
    event Action? OnLightsChanged;

    /// <summary>
    /// Begins interactive light placement. The light spawns at cursor position
    /// and follows the cursor until confirmed or cancelled.
    /// </summary>
    void BeginPlacement(LightType type);

    /// <summary>
    /// Confirms the current placement, finalizing the light's position.
    /// </summary>
    void ConfirmPlacement();

    /// <summary>
    /// Cancels the current placement, destroying the light.
    /// </summary>
    void CancelPlacement();

    /// <summary>
    /// Spawns a new light at the specified position.
    /// Light is created asynchronously on framework thread.
    /// </summary>
    void SpawnLight(LightType type, Vector3 position);

    /// <summary>
    /// Destroys a spawned light.
    /// </summary>
    void DestroyLight(LightEntity light);

    /// <summary>
    /// Destroys all spawned lights.
    /// </summary>
    void DestroyAllLights();

    /// <summary>
    /// Duplicates an existing light asynchronously.
    /// Listen to OnLightsChanged for the new light.
    /// </summary>
    void CloneLight(LightEntity source);

    /// <summary>
    /// Checks if an entity is a spawned light managed by this service.
    /// </summary>
    bool IsSpawnedLight(IEntity entity);
}
