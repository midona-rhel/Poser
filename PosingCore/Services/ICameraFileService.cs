using Poser.Entities;

namespace Poser.Services;

/// <summary>
/// .posercam import/export for a single virtual camera. Import always creates
/// a NEW camera — a camera file describes a camera, not an edit to one.
/// </summary>
public interface ICameraFileService
{
    /// <summary>
    /// Writes every property of the camera to a file. False when the file
    /// could not be written.
    /// </summary>
    bool ExportCamera(IVirtualCamera camera, string path);

    /// <summary>
    /// Loads a camera file, creates a camera of its kind and applies every
    /// property. Framework thread only. Returns null when the file cannot be
    /// read or the camera cannot be created.
    /// </summary>
    IVirtualCamera? ImportCamera(string path);
}
