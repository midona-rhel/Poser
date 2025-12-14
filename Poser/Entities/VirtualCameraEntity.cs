using System;
using System.Numerics;
using Poser.Core;
using Poser.Entities.Capabilities;

namespace Poser.Entities;

/// <summary>
/// Entity representing a virtual camera preset.
/// Stores camera state that can be captured from and applied to the game camera.
/// </summary>
public class VirtualCameraEntity : EntityBase, ITransformable
{
    private static int _nextId = 1;

    /// <summary>
    /// Camera world position offset.
    /// </summary>
    public Vector3 PositionOffset { get; set; } = Vector3.Zero;

    /// <summary>
    /// Camera angle (X = Pitch, Y = Yaw).
    /// </summary>
    public Vector2 Angle { get; set; } = Vector2.Zero;

    /// <summary>
    /// Zoom distance from target.
    /// </summary>
    public float Distance { get; set; } = 6f;

    /// <summary>
    /// Field of view in radians.
    /// </summary>
    public float FoV { get; set; } = 0.78f;

    /// <summary>
    /// Roll rotation around Z-axis.
    /// </summary>
    public float Roll { get; set; } = 0f;

    /// <summary>
    /// Pan offset (X = horizontal, Y = vertical).
    /// </summary>
    public Vector2 Pan { get; set; } = Vector2.Zero;

    /// <summary>
    /// Whether this camera is currently active.
    /// </summary>
    public bool IsActive { get; internal set; }

    /// <summary>
    /// Whether to disable camera collision.
    /// </summary>
    public bool DisableCollision { get; set; }

    /// <summary>
    /// Whether to extend zoom limits beyond default (0-500 instead of 1.5-20).
    /// </summary>
    public bool DelimitCamera { get; set; }

    /// <summary>
    /// Whether position offset is locked (prevents editing).
    /// </summary>
    public bool PositionLocked { get; set; }

    public override EntityType EntityType => EntityType.Camera;

    public override bool IsCollapsible => false;

    /// <summary>
    /// Cameras don't show gizmos - they use dedicated controls instead.
    /// </summary>
    public bool ShowGizmo => false;

    /// <summary>
    /// Transform derived from camera angles.
    /// Position = PositionOffset, Rotation = derived from Angle (pitch/yaw) and Roll.
    /// </summary>
    public override Transform Transform
    {
        get
        {
            // Build rotation from yaw (Angle.Y), pitch (Angle.X), and roll
            var yaw = Quaternion.CreateFromAxisAngle(Vector3.UnitY, Angle.Y);
            var pitch = Quaternion.CreateFromAxisAngle(Vector3.UnitX, Angle.X);
            var roll = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, Roll);
            var rotation = yaw * pitch * roll;

            return new Transform(PositionOffset, rotation, Vector3.One);
        }
        set
        {
            // Extract angles from quaternion
            PositionOffset = value.Position;

            // Convert quaternion to euler angles (YXZ order for yaw-pitch-roll)
            var q = value.Rotation;
            var sinPitch = 2f * (q.W * q.X - q.Y * q.Z);
            var pitch = MathF.Abs(sinPitch) >= 1f
                ? MathF.CopySign(MathF.PI / 2f, sinPitch)
                : MathF.Asin(sinPitch);

            var sinYaw = 2f * (q.W * q.Y + q.X * q.Z);
            var cosYaw = 1f - 2f * (q.X * q.X + q.Y * q.Y);
            var yaw = MathF.Atan2(sinYaw, cosYaw);

            var sinRoll = 2f * (q.W * q.Z + q.X * q.Y);
            var cosRoll = 1f - 2f * (q.X * q.X + q.Z * q.Z);
            var roll = MathF.Atan2(sinRoll, cosRoll);

            Angle = new Vector2(pitch, yaw);
            Roll = roll;
        }
    }

    public VirtualCameraEntity(string? name = null)
        : base(EntityId.New(), name ?? $"Camera {_nextId++}")
    {
    }

    /// <summary>
    /// Creates a camera state snapshot for serialization.
    /// </summary>
    public CameraState CaptureState()
    {
        return new CameraState
        {
            Name = Name,
            PositionOffset = PositionOffset,
            Angle = Angle,
            Distance = Distance,
            FoV = FoV,
            Roll = Roll,
            Pan = Pan,
            DisableCollision = DisableCollision,
            DelimitCamera = DelimitCamera,
            PositionLocked = PositionLocked
        };
    }

    /// <summary>
    /// Restores camera state from a snapshot.
    /// </summary>
    public void RestoreState(CameraState state)
    {
        Name = state.Name ?? Name;
        PositionOffset = state.PositionOffset;
        Angle = state.Angle;
        Distance = state.Distance;
        FoV = state.FoV;
        Roll = state.Roll;
        Pan = state.Pan;
        DisableCollision = state.DisableCollision;
        DelimitCamera = state.DelimitCamera;
        PositionLocked = state.PositionLocked;
    }

    /// <summary>
    /// Clones this camera entity.
    /// </summary>
    public VirtualCameraEntity Clone()
    {
        var clone = new VirtualCameraEntity($"{Name} (Copy)");
        clone.PositionOffset = PositionOffset;
        clone.Angle = Angle;
        clone.Distance = Distance;
        clone.FoV = FoV;
        clone.Roll = Roll;
        clone.Pan = Pan;
        clone.DisableCollision = DisableCollision;
        clone.DelimitCamera = DelimitCamera;
        clone.PositionLocked = PositionLocked;
        return clone;
    }
}

/// <summary>
/// Serializable camera state for scene files.
/// </summary>
public class CameraState
{
    public string? Name { get; set; }
    public Vector3 PositionOffset { get; set; }
    public Vector2 Angle { get; set; }
    public float Distance { get; set; }
    public float FoV { get; set; }
    public float Roll { get; set; }
    public Vector2 Pan { get; set; }
    public bool DisableCollision { get; set; }
    public bool DelimitCamera { get; set; }
    public bool PositionLocked { get; set; }
}
