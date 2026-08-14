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

    /// <summary>Destroys a spawned light and frees its native object. For a
    /// captured light this routes to <see cref="ReleaseLight"/> instead —
    /// borrowed natives are never destructed.</summary>
    void DestroyLight(ILight light);

    void DestroyAllLights();

    bool IsSpawnedLight(ILight light);

    /// <summary>Releases a captured light: delists a GPose light, restores
    /// and un-suppresses a world light's original. No-op for spawned.</summary>
    void ReleaseLight(ILight light);

    /// <summary>The embedded gobo library (88 housing-window textures).</summary>
    IReadOnlyList<GoboEntry> Gobos { get; }

    /// <summary>Projects a gobo texture through the light. Spot and Area
    /// lights only; returns false when unsupported or the native texture
    /// call fails.</summary>
    bool ApplyGobo(ILight light, GoboEntry gobo);

    void ClearGobo(ILight light);

    /// <summary>Overworld lights available for copy-and-suppress capture,
    /// nearest first. GPose only; empty when the ctor hook is unavailable.</summary>
    IReadOnlyList<WorldLightCandidate> GetWorldLightCandidates();

    /// <summary>Captures an overworld light: spawns an owned copy of it and
    /// suppresses the original until release. Framework thread only.</summary>
    ILight? CaptureWorldLight(WorldLightCandidate candidate);
}

/// <summary>One entry of the embedded gobo library.</summary>
public sealed record GoboEntry(string Path, string Name);

/// <summary>One capturable overworld light. The handle is only valid on the
/// framework thread and only until the light list next changes;
/// <paramref name="Position"/> is where the light stood when it was listed —
/// the world point an adoption handle projects from.</summary>
public readonly record struct WorldLightCandidate(
    nint Handle,
    float DistanceFromPlayer,
    System.Numerics.Vector3 Position = default);
