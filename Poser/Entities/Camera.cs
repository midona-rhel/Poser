using System.Numerics;
using Poser.Core;
using Poser.Services;

namespace Poser.Entities;

/// <summary>
/// Represents the game camera as an entity.
/// </summary>
public class Camera : EntityBase, ICamera
{
    private readonly ICameraService _cameraService;

    /// <summary>
    /// The camera's position in world space.
    /// </summary>
    public Vector3 Position => _cameraService.GetCameraPosition();

    /// <summary>
    /// The camera's rotation (not yet implemented).
    /// </summary>
    public Quaternion Rotation => Quaternion.Identity;

    /// <summary>
    /// Cameras are not collapsible.
    /// </summary>
    public override bool IsCollapsible => false;

    /// <summary>
    /// Entity type is Camera.
    /// </summary>
    public override EntityType EntityType => EntityType.Camera;

    public Camera(ICameraService cameraService)
        : base(EntityId.New(), "Camera")
    {
        _cameraService = cameraService;
    }
}
