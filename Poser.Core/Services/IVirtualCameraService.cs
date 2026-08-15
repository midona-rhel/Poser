using System;
using System.Collections.Generic;
using Poser.Domain.Scene;
using Poser.Entities;

namespace Poser.Services;

/// <summary>
/// Owns the virtual cameras over the game's one orbit camera (Brio's overlay
/// model). GPose-scoped: entering GPose mints the default camera, leaving
/// destroys every camera and restores the native state. Publishes
/// <c>CameraListChangedEvent</c> on any list change.
/// </summary>
public interface IVirtualCameraService : IDisposable
{
    /// <summary>False when the native camera-update signature was not found;
    /// every operation is a silent no-op in that state.</summary>
    bool IsAvailable { get; }

    /// <summary>Default camera first, then creation order. Empty outside
    /// GPose.</summary>
    IReadOnlyList<IVirtualCamera> Cameras { get; }

    /// <summary>The camera driving the game's view; null outside GPose.</summary>
    IVirtualCamera? LiveCamera { get; }

    /// <summary>The fly speed the wheel last set on the live free camera, and
    /// when. Null whenever no free camera is live — the overlay's readout
    /// answers to this and to nothing else, so a free camera that is not
    /// flying puts nothing on screen.</summary>
    FreeCameraSpeedNotice? SpeedNotice { get; }

    /// <summary>Reported by the UI's draw pass each frame: an ImGui text
    /// field holds the keyboard, so the free camera must not fly on those
    /// keys (user 2026-08-15: "if there is focus in the UI, we do not
    /// listen"). The report lapses on its own when the draw stops, so a
    /// hidden HUD can never leave the camera stuck deaf.</summary>
    void ReportUiTextFocus(bool focused);

    /// <summary>Creates a camera seeded from the current view and makes it
    /// live. Framework thread only. Returns null on failure.</summary>
    IVirtualCamera? CreateCamera(CameraKind kind);

    /// <summary>Creates a copy of an existing camera, every setting included,
    /// and makes it live. Framework thread only.</summary>
    IVirtualCamera? CloneCamera(IVirtualCamera source);

    /// <summary>Destroys a created camera. The default camera cannot be
    /// destroyed; destroying the live camera falls back to the default.</summary>
    void DestroyCamera(IVirtualCamera camera);

    void DestroyAllCameras();

    /// <summary>Switches the live camera: the outgoing camera's state is
    /// saved, the incoming camera's state is written to the native camera.
    /// Framework thread only.</summary>
    void SetLive(IVirtualCamera camera);

    /// <summary>Brio's target select: the camera's target offset becomes the
    /// actor's draw-object offset so the orbit pivot sits on the drawn body.
    /// Framework thread only; false when the actor has no draw object.</summary>
    bool SetTargetActor(IVirtualCamera camera, IActor actor, string displayName);

    void ClearTargetActor(IVirtualCamera camera);
}
