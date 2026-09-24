using Poser.Entities;

namespace Poser.Services;

/// <summary>
/// .xivc import/export for a single virtual camera. Import always creates
/// a NEW camera — a camera file describes a camera, not an edit to one.
/// </summary>
public interface ICameraFileService
{
    /// <summary>
    /// Writes every property of the camera to a file. False when the file
    /// could not be written.
    /// </summary>
    bool ExportCamera(IVirtualCamera camera, string path);

    /// <summary>Export carrying the placement anchors a relative load
    /// needs; either may be null when unavailable.</summary>
    bool ExportCamera(
        IVirtualCamera camera,
        string path,
        Files.PlacementAnchorData? cameraAnchor,
        Files.PlacementAnchorData? actorAnchor);

    /// <summary>
    /// Loads a camera file, creates a camera of its kind and applies every
    /// property. Framework thread only. Returns null when the file cannot be
    /// read or the camera cannot be created.
    /// </summary>
    IVirtualCamera? ImportCamera(string path);

    /// <summary>Import placed by <paramref name="mode"/>; refuses by name
    /// when the file records no anchor for it, and for a non-free camera —
    /// an orbit camera follows its target, not a placement.</summary>
    IVirtualCamera? ImportCamera(
        string path,
        Files.ObjectPlacementMode mode,
        System.Numerics.Vector3 currentPosition,
        float currentYaw,
        out string? refusal);
}
