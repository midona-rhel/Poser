using System;
using System.Collections.Generic;
using Poser.Domain.Scene;
using Poser.Entities;

namespace Poser.Services;

/// <summary>
/// Spawns and owns plugin-created scene lights. GPose-scoped: every
/// spawned light is destroyed on GPose exit. Publishes
/// <c>LightListChangedEvent</c> on any list change.
/// </summary>
public interface ILightingService : IDisposable
{
    /// <summary>False when the native signatures were not found; every
    /// operation is a silent no-op in that state.</summary>
    bool IsAvailable { get; }

    IReadOnlyList<ILight> Lights { get; }

    /// <summary>Spawns a light of the given kind in front of the current
    /// camera. Framework thread only. Returns null on failure.</summary>
    ILight? SpawnLight(LightKind kind);

    /// <summary>Spawns a copy of an existing light, all properties
    /// included. Framework thread only.</summary>
    ILight? CloneLight(ILight source);

    /// <summary>Destroys a spawned light and frees its native object.</summary>
    void DestroyLight(ILight light);

    void DestroyAllLights();

    bool IsSpawnedLight(ILight light);
}
