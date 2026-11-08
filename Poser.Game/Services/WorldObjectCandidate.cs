using System.Numerics;

namespace Poser.Game.Services;

/// <summary>
/// A background object available for adoption, with the native address used
/// to acquire it and the world position used to draw its handle. Distance is
/// omitted because the overlay measures every candidate from the camera.
/// </summary>
public readonly record struct WorldObjectCandidate(
    nint Address,
    string Path,
    string Name,
    Vector3 Position = default);
