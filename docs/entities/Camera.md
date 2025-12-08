# Camera Entity

**Interface:** `ICamera`
**Implementation:** `Camera` (to be created/updated)
**Location:** `Poser/Entities/Camera.cs`

## Purpose

Represents a camera in the scene that can be positioned and oriented.

## Interface (Proposed)

```csharp
public interface ICamera : IEntity, ITransformable
{
    /// <summary>
    /// Whether this camera is currently the active render camera.
    /// </summary>
    bool IsActiveCamera { get; }

    /// <summary>
    /// Camera type (Main, Free, Virtual, etc.)
    /// </summary>
    CameraType CameraType { get; }

    /// <summary>
    /// Field of view in degrees.
    /// </summary>
    float FieldOfView { get; set; }

    /// <summary>
    /// Near clip plane distance.
    /// </summary>
    float NearClip { get; set; }

    /// <summary>
    /// Far clip plane distance.
    /// </summary>
    float FarClip { get; set; }
}

public enum CameraType
{
    Main,       // Game's main camera
    Free,       // Free-floating camera
    Virtual     // User-created virtual camera
}
```

## Capabilities

ICamera implements `ITransformable`:
- Position in world space
- Rotation (orientation)
- No scale (cameras don't scale)

## Gizmo Behavior

**Critical rule:** Don't show gizmo on the camera we're looking through.

```csharp
// In Camera implementation
public bool ShowGizmo => !IsActiveCamera;
```

This prevents the confusing situation where:
1. User selects the active camera
2. Gizmo appears at camera position
3. But user can't see it because they're looking through that camera

## Properties Panel Display

When a Camera is the primary selection:
- **Transform**: Position, rotation (no scale)
- **Camera settings**: FOV, near/far clip
- **Make Active button**: Switch to this camera

## Brio Reference

Brio has extensive camera support:
- `CameraEntity` with `CameraLifetimeCapability` and `BrioCameraCapability`
- Virtual camera system for multiple viewpoints
- Camera animation/keyframes

From `Brio/Entities/Camera/CameraEntity.cs`:
```csharp
public class CameraEntity : Entity
{
    public VirtualCamera VirtualCamera { get; }
    public int CameraID { get; }
    public CameraType CameraType { get; }
}
```

## Future Features

- Multiple virtual cameras
- Camera keyframes/animation
- Camera presets (save/load positions)
- Picture-in-picture preview
