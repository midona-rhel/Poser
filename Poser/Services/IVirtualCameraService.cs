using System;
using System.Collections.Generic;
using Poser.Entities;

namespace Poser.Services;

/// <summary>
/// Service for managing virtual camera presets.
/// Cameras can be created, saved, and applied to the game camera.
/// </summary>
public interface IVirtualCameraService : IDisposable
{
    /// <summary>
    /// Whether the service is available.
    /// </summary>
    bool IsAvailable { get; }

    /// <summary>
    /// The currently active camera, or null if using default game camera.
    /// </summary>
    VirtualCameraEntity? CurrentCamera { get; }

    /// <summary>
    /// All created virtual cameras.
    /// </summary>
    IReadOnlyList<VirtualCameraEntity> Cameras { get; }

    /// <summary>
    /// Event fired when cameras are added, removed, or the active camera changes.
    /// </summary>
    event Action? OnCamerasChanged;

    /// <summary>
    /// Creates a new virtual camera with current game camera settings.
    /// </summary>
    /// <param name="name">Optional name for the camera.</param>
    /// <returns>The created camera entity.</returns>
    VirtualCameraEntity CreateCamera(string? name = null);

    /// <summary>
    /// Deletes a virtual camera.
    /// </summary>
    /// <param name="camera">The camera to delete.</param>
    void DeleteCamera(VirtualCameraEntity camera);

    /// <summary>
    /// Deletes all virtual cameras.
    /// </summary>
    void DeleteAllCameras();

    /// <summary>
    /// Clones an existing camera.
    /// </summary>
    /// <param name="source">The camera to clone.</param>
    /// <returns>The cloned camera entity.</returns>
    VirtualCameraEntity CloneCamera(VirtualCameraEntity source);

    /// <summary>
    /// Selects a camera, making it active and applying its settings to the game camera.
    /// </summary>
    /// <param name="camera">The camera to select, or null to use default game camera.</param>
    void SelectCamera(VirtualCameraEntity? camera);

    /// <summary>
    /// Captures current game camera state into a camera entity.
    /// </summary>
    /// <param name="camera">The camera to update with current game camera state.</param>
    void CaptureFromGame(VirtualCameraEntity camera);

    /// <summary>
    /// Applies a camera entity's settings to the game camera.
    /// </summary>
    /// <param name="camera">The camera to apply.</param>
    void ApplyToGame(VirtualCameraEntity camera);

    /// <summary>
    /// Checks if an entity is a virtual camera managed by this service.
    /// </summary>
    bool IsVirtualCamera(IEntity entity);
}
