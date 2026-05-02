using System.Numerics;
using System.Runtime.InteropServices;
using GameCamera = FFXIVClientStructs.FFXIV.Client.Game.Camera;

namespace Poser.Game.Structs;

/// <summary>
/// Native camera memory layout for FFXIV's GPose camera.
/// Based on Brio's camera struct (0x2B0 bytes).
/// Cast from CameraManager.Instance()->GetActiveCamera().
/// </summary>
[StructLayout(LayoutKind.Explicit, Size = 0x2B0)]
public unsafe struct BrioCamera
{
    /// <summary>
    /// The underlying game camera struct. Provides access to:
    /// Camera.CameraBase.SceneCamera.Object.Position
    /// Camera.CameraBase.SceneCamera.LookAtVector
    /// </summary>
    [FieldOffset(0x000)]
    public GameCamera Camera;

    /// <summary>
    /// Camera world position (read-only, use Camera.CameraBase.SceneCamera.Object.Position for writes).
    /// </summary>
    [FieldOffset(0x060)]
    public Vector3 Position;

    /// <summary>
    /// Current zoom distance from target (default: 6.0).
    /// </summary>
    [FieldOffset(0x124)]
    public float Distance;

    /// <summary>
    /// Minimum zoom distance (default: 1.5).
    /// </summary>
    [FieldOffset(0x128)]
    public float MinDistance;

    /// <summary>
    /// Maximum zoom distance (default: 20.0).
    /// </summary>
    [FieldOffset(0x12C)]
    public float MaxDistance;

    /// <summary>
    /// Field of view in radians (default: 0.78).
    /// </summary>
    [FieldOffset(0x130)]
    public float FoV;

    /// <summary>
    /// Minimum field of view (default: 0.69).
    /// </summary>
    [FieldOffset(0x134)]
    public float MinFoV;

    /// <summary>
    /// Maximum field of view (default: 0.78).
    /// </summary>
    [FieldOffset(0x138)]
    public float MaxFoV;

    /// <summary>
    /// Fine zoom adjustment (-0.5 to 0.5, default: 0).
    /// </summary>
    [FieldOffset(0x13C)]
    public float Zoom;

    /// <summary>
    /// Camera angle (X = Pitch, Y = Yaw).
    /// </summary>
    [FieldOffset(0x140)]
    public Vector2 Angle;

    /// <summary>
    /// Pan offset (X = horizontal pan, Y = vertical tilt).
    /// </summary>
    [FieldOffset(0x160)]
    public Vector2 Pan;

    /// <summary>
    /// Roll rotation around Z-axis.
    /// </summary>
    [FieldOffset(0x170)]
    public float Roll;

    /// <summary>
    /// Camera mode (0 = first person, 1 = third person, 2+ = restrictive).
    /// </summary>
    [FieldOffset(0x180)]
    public int Mode;

    /// <summary>
    /// Collision parameters.
    /// </summary>
    [FieldOffset(0x218)]
    public Vector2 Collide;
}
