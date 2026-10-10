using Poser.Application.Presentation;
using Poser.Domain.Identity;
using Poser.Entities;
using Poser.Game.Presentation;

namespace Poser.Game.Cameras;

/// <summary>How each declared camera property reads and writes a live camera.</summary>
public static class CameraAccessors
{
    public static EntityAccessors<CameraId, IVirtualCamera> Create() =>
        new EntityAccessors<CameraId, IVirtualCamera>("The camera did not accept the value.")
            .Assign(CameraProperties.IsLocked, c => c.IsLocked, (c, v) => c.IsLocked = v)
            .Assign(CameraProperties.Name, c => c.Name, (c, v) => c.Name = v)
            .Assign(CameraProperties.Zoom, c => c.Zoom, (c, v) => c.Zoom = v)
            .Assign(CameraProperties.FoV, c => c.FoV, (c, v) => c.FoV = v)
            .Assign(CameraProperties.Roll, c => c.Roll, (c, v) => c.Roll = v)
            .Assign(CameraProperties.Angle, c => c.Angle, (c, v) => c.Angle = v)
            .Assign(CameraProperties.Pan, c => c.Pan, (c, v) => c.Pan = v)
            .Assign(CameraProperties.PositionOffset, c => c.PositionOffset, (c, v) => c.PositionOffset = v)
            .Assign(CameraProperties.FixedPosition, c => c.FixedPosition, (c, v) => c.FixedPosition = v)
            .Assign(CameraProperties.Position, c => c.Position, (c, v) => c.Position = v)
            .Assign(CameraProperties.Rotation, c => c.Rotation, (c, v) => c.Rotation = v)
            .Assign(CameraProperties.DisableCollision, c => c.DisableCollision, (c, v) => c.DisableCollision = v)
            .Assign(CameraProperties.DelimitCamera, c => c.DelimitCamera, (c, v) => c.DelimitCamera = v)
            .Assign(CameraProperties.MovementEnabled, c => c.MovementEnabled, (c, v) => c.MovementEnabled = v)
            .Assign(CameraProperties.Move2D, c => c.Move2D, (c, v) => c.Move2D = v)
            .Assign(CameraProperties.MovementSpeed, c => c.MovementSpeed, (c, v) => c.MovementSpeed = v)
            .Assign(CameraProperties.MouseSensitivity, c => c.MouseSensitivity, (c, v) => c.MouseSensitivity = v)
            .Assign(CameraProperties.DelimitAngle, c => c.DelimitAngle, (c, v) => c.DelimitAngle = v)
            .Assign(CameraProperties.Orthographic, c => c.Orthographic, (c, v) => c.Orthographic = v)
            // Re-asserting the projection makes the new width take effect at once.
            .Assign(CameraProperties.OrthographicZoom, c => c.OrthographicZoom, (c, v) =>
            {
                c.OrthographicZoom = v;
                if (c.Orthographic)
                    c.Orthographic = true;
            })
            // The native toggle turns the roll itself; the authored roll is
            // written after it so undo restores both exactly.
            .Assign(CameraProperties.Portrait, c => (Portrait: c.IsPortraitMode, c.Roll), (c, v) =>
            {
                if (c.IsPortraitMode != v.Portrait) c.TogglePortraitMode();
                c.Roll = v.Roll;
            })
            .Assign(CameraProperties.IsTracking, c => c.IsTracking, (c, v) => c.IsTracking = v)
            .Assign(CameraProperties.TrackingMode, c => c.TrackingMode, (c, v) => c.TrackingMode = v)
            .Assign(CameraProperties.IsTargetLocked, c => c.IsTargetLocked, (c, v) => c.IsTargetLocked = v)
            .Complete(CameraProperties.All);
}
