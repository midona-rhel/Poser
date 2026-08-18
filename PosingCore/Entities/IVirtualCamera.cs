using System.Collections.Generic;
using System.Numerics;
using Poser.Domain.Identity;
using Poser.Domain.Scene;

namespace Poser.Entities;

/// <summary>
/// One virtual camera. All setters route to the native camera while the
/// camera is LIVE and to retained state otherwise, so switching cameras is a
/// state save/restore over the game's one orbit camera (Brio's model — the
/// game never sees a second camera object). Setters MUST be called on the
/// framework thread while live.
/// </summary>
public interface IVirtualCamera
{
    /// <summary>False once the camera has been destroyed.</summary>
    bool IsValid { get; }

    string Name { get; set; }

    /// <summary>Fixed at creation: a Game camera overrides the orbit camera's
    /// state, a Free camera replaces the view matrix and flies.</summary>
    CameraKind Kind { get; }

    /// <summary>Whether this camera currently drives the game's view. Written
    /// only through the service's select call — exactly one camera is live.
    /// </summary>
    bool IsLive { get; }

    /// <summary>The GPose session's own camera, created at entry and never
    /// destroyable; every other camera is spawned beside it.</summary>
    bool IsDefault { get; }

    /// <summary>A locked camera keeps its framing: every property edit is
    /// disabled and a live free camera stops responding to movement input.
    /// Switching which camera is live stays allowed — the lock protects the
    /// shot, not the session.</summary>
    bool IsLocked { get; set; }

    // ── orbit (Game) state, native units ─────────────────────────────────

    /// <summary>Horizontal/vertical orbit angle in radians.</summary>
    Vector2 Angle { get; set; }

    /// <summary>Pan/tilt offset in radians.</summary>
    Vector2 Pan { get; set; }

    /// <summary>Roll around the view axis in radians.</summary>
    float Roll { get; set; }

    /// <summary>Orbit distance from the pivot ("zoom").</summary>
    float Zoom { get; set; }

    /// <summary>The native distance clamp as it currently stands — moves when
    /// the camera is delimited.</summary>
    Vector2 ZoomLimits { get; }

    /// <summary>FoV offset in radians around the game's base FoV.</summary>
    float FoV { get; set; }

    /// <summary>World-space offset added to the camera position every native
    /// update.</summary>
    Vector3 PositionOffset { get; set; }

    /// <summary>Extra offset aligning the orbit pivot with the followed
    /// actor's draw position (Brio's target select).</summary>
    Vector3 TargetOffset { get; set; }

    /// <summary>Display name for the followed actor; empty when none.</summary>
    string TargetActorName { get; set; }

    /// <summary>The exact actor generation followed by this camera. The name
    /// is presentation only and must never recover this identity.</summary>
    ActorId? TargetActorId { get; set; }

    /// <summary>The camera's real world position this frame — the native
    /// position while live, the retained free-cam position otherwise.</summary>
    Vector3 WorldPosition { get; }

    /// <summary>
    /// Ktisis's <c>FixedPosition</c>: pin an orbit camera to a world point so
    /// the shot does not drift when the subject moves. Null is unpinned — the
    /// camera goes wherever the game's own update puts it.
    /// <see cref="PositionOffset"/> still applies on top, so the pin is the
    /// BASE the offset is measured from rather than the final answer.
    ///
    /// <para>Meaningless on a free camera, which owns its
    /// <see cref="Position"/> outright.</para>
    /// </summary>
    Vector3? FixedPosition { get; set; }

    bool DisableCollision { get; set; }

    /// <summary>Lifts the native zoom and vertical-angle clamps (Brio's
    /// distance range, Ktisis's Y loosening). Restores the game's own limits
    /// when cleared.</summary>
    bool DelimitCamera { get; set; }

    /// <summary>Roll pre-set 90° for portrait framing; toggling back removes
    /// exactly the quarter turn it added.</summary>
    bool IsPortraitMode { get; }

    void TogglePortraitMode();

    // ── free-cam state ───────────────────────────────────────────────────

    /// <summary>Free-cam world position.</summary>
    Vector3 Position { get; set; }

    /// <summary>The position the free cam spawned at, for reset.</summary>
    Vector3 SpawnPosition { get; }

    /// <summary>Free-cam yaw (X) and pitch (Y) in radians.</summary>
    Vector3 Rotation { get; set; }

    bool MovementEnabled { get; set; }

    /// <summary>Lateral movement: WASD moves in the horizontal plane instead
    /// of along the look ray.</summary>
    bool Move2D { get; set; }

    float MovementSpeed { get; set; }
    float MouseSensitivity { get; set; }

    /// <summary>Lets free-cam pitch wrap past straight up/down.</summary>
    bool DelimitAngle { get; set; }

    // ── Ktisis grafts ────────────────────────────────────────────────────

    /// <summary>Orthographic projection on the render camera.</summary>
    bool Orthographic { get; set; }

    float OrthographicZoom { get; set; }

    /// <summary>Bone tracking: the camera pivot follows the averaged world
    /// position of the tracked bones.</summary>
    bool IsTracking { get; set; }

    CameraTrackingMode TrackingMode { get; set; }

    /// <summary>Bones the pivot averages over. Entity references like the
    /// light attach — a bone whose skeleton dies is dropped by the service.
    /// </summary>
    IList<IBone> TrackedBones { get; }

    /// <summary>Brio's reset-to-default: zoom, FoV, roll, pan, angle, offsets
    /// and the collision/delimit overrides back to spawn values.</summary>
    void ResetProperties();
}

/// <summary>Ktisis's tracking modes: Follow keeps the pivot offset on the
/// actor, Pan swings the look-at to the bones, FollowAndPan blends both.
/// </summary>
public enum CameraTrackingMode
{
    Follow,
    Pan,
    FollowAndPan,
    None,
}
