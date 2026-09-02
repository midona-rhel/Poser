using System.Numerics;
using Poser.Domain.Presentation;

namespace Poser.Services;

/// <summary>A world object the scene holds: a spawned or adopted map object or effect with its placement, look and animation state.</summary>
public interface IWorldObject
{
    bool Spawned { get; }
    int Id { get; }
    string Name { get; set; }
    string Path { get; }
    nint Address { get; }
    bool IsVfx { get; }
    bool LoopVfx { get; set; }
    float VfxSpeed { get; set; }
    float VfxIntensity { get; set; }
    bool VfxPaused { get; set; }
    float Opacity { get; set; }
    Vector3? Tint { get; set; }
    bool? Dyeable { get; }
    bool NightState { get; set; }
    bool AnimationPaused { get; set; }
    ulong? DebugObjectFlags { get; set; }
    byte? DebugByte(int offset);
    void SetDebugByte(int offset, byte value);
    bool Respawn(string path, out string? detail);
    Transform InitialPlacement { get; }
    byte InitialFlags { get; }
    bool InitialVisible { get; }
    bool IsValid { get; }
    bool Visible { get; set; }
    Transform Transform { get; set; }
}
