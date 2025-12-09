using System.Numerics;

namespace Poser.Entities;

/// <summary>
/// Represents a camera entity.
/// </summary>
public interface ICamera : IEntity
{
    /// <summary>
    /// The camera's position in world space.
    /// </summary>
    Vector3 Position { get; }
}
