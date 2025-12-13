using System;
using System.Collections.Generic;
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
    /// Event fired when lights are added or removed.
    /// </summary>
    event Action? OnLightsChanged;

    /// <summary>
    /// Spawns a new light at the camera position.
    /// </summary>
    LightEntity? SpawnLight(LightType type);

    /// <summary>
    /// Destroys a spawned light.
    /// </summary>
    void DestroyLight(LightEntity light);

    /// <summary>
    /// Destroys all spawned lights.
    /// </summary>
    void DestroyAllLights();

    /// <summary>
    /// Duplicates an existing light.
    /// </summary>
    LightEntity? CloneLight(LightEntity source);

    /// <summary>
    /// Checks if an entity is a spawned light managed by this service.
    /// </summary>
    bool IsSpawnedLight(IEntity entity);
}
