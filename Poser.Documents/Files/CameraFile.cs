using System;
using System.Numerics;
using Poser.Domain.Scene;

namespace Poser.Documents.Files;

/// <summary>
/// One virtual camera's document, embedded in a scene container (a .xivc
/// entry is that container holding one camera). Carries every virtual camera property
/// except the live flag and
/// the tracked bones — liveness belongs to the session and bone references
/// belong to the scene they were picked in. Angular values are stored in the
/// native radians the entity itself carries.
/// </summary>
[Serializable]
public class CameraFile
{
    public const int CurrentVersion = 1;

    public string TypeName { get; set; } = "Poser Camera";
    public int FileVersion { get; set; } = CurrentVersion;

    public string Name { get; set; } = "Camera";
    public CameraKind Kind { get; set; }

    // Orbit state.
    /// <summary>The placement anchors a bare-JSON .xivc carried before
    /// entries became containers; read only to wrap such a file.</summary>
    public PlacementAnchorData? CameraAnchor { get; set; }
    public PlacementAnchorData? ActorAnchor { get; set; }

    public Vector2 Angle { get; set; }
    public Vector2 Pan { get; set; }
    public float Roll { get; set; }
    public float Zoom { get; set; } = 2.5f;
    public float FoV { get; set; }
    public Vector3 PositionOffset { get; set; }

    /// <summary>The world point the camera is pinned to, or null when it is
    /// free to follow the game's update (Ktisis carries the same field in its
    /// scene file). Null and "0, 0, 0" are different answers here, which is
    /// why it is nullable rather than a sentinel.</summary>
    public Vector3? FixedPosition { get; set; }

    public bool DisableCollision { get; set; }
    public bool DelimitCamera { get; set; }

    // Free-cam state.
    public Vector3 Position { get; set; }
    public Vector3 Rotation { get; set; }
    public bool MovementEnabled { get; set; } = true;
    public bool Move2D { get; set; }
    public float MovementSpeed { get; set; } = 0.03f;
    public float MouseSensitivity { get; set; } = 0.1f;
    public bool DelimitAngle { get; set; }

    // Projection.
    public bool Orthographic { get; set; }
    public float OrthographicZoom { get; set; } = 10f;
}
