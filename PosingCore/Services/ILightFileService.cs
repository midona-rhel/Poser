using Poser.Entities;

namespace Poser.Services;

/// <summary>
/// .xivl import/export for a single scene light. Import always spawns a
/// NEW light — a light file describes a light, not an edit to one.
/// </summary>
public interface ILightFileService
{
    /// <summary>
    /// Writes every property of the light to a file. False when the file
    /// could not be written.
    /// </summary>
    bool ExportLight(ILight light, string path);

    /// <summary>
    /// Loads a light file, spawns a light of its kind and applies every
    /// property including the absolute transform. Framework thread only —
    /// spawning and the property setters both are. Returns null when the file
    /// cannot be read or the light cannot be spawned.
    /// </summary>
    ILight? ImportLight(string path);
}
