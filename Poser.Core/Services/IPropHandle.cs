using System.Numerics;
using Poser.Domain.Presentation;

namespace Poser.Services;

/// <summary>A prop the scene holds: a spawned weapon model with a placement, a dye pair and a visibility.</summary>
public interface IPropHandle
{
    int Id { get; }
    string Name { get; set; }
    PropModel Model { get; }
    bool Respawn(PropModel model, out string? detail);
    nint Address { get; }
    bool IsValid { get; }
    Vector3 Position { get; set; }
    Quaternion Rotation { get; set; }
    Vector3 Scale { get; set; }
    bool Visible { get; set; }
    Transform Transform { get; set; }
    void Destroy();
}
