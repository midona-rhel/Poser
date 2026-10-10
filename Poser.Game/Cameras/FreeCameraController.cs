using System;
using System.Numerics;
using Dalamud.Game.ClientState.Keys;
using Dalamud.Plugin.Services;
using Poser.Domain.Cameras;
using Poser.Domain.Scene;
using Poser.Services;

namespace Poser.Game.Cameras;

/// <summary>
/// The free camera's flight: the frame inputs the game's input handler
/// writes (look drag, movement, wheel speed) and the view matrix the scene
/// phase builds from them — Brio's free camera, plus the fly-speed readout.
/// </summary>
internal sealed unsafe class FreeCameraController
{
    private readonly VirtualCameraService _owner;
    private readonly IKeyState? _keyState;

    // Free-cam frame inputs, written by the input detour, consumed by the
    // scene-update detour (Brio's _forward/_lastMousePosition pair).
    private Vector3 _freeForward;
    /// <summary>Where the tracked pivot was last frame: a free camera in
    /// Follow moves by the pivot's motion, not to the pivot.</summary>
    private Vector3? _freeFollowPivot;
    private Vector2 _freeMouseDelta;
    private float _freeMoveSpeed = VirtualCameraService.DefaultMovementSpeed;

    /// <summary>Set per UI frame: while typing or an active ImGui item
    /// owns the keyboard, flight keys stand down (the modifier contract's
    /// focus rule). Written from the draw side, read on the camera's
    /// update — a one-frame lag is invisible.</summary>
    private readonly Application.Input.CameraInputState _input;
    public bool SuppressFlightKeys { get => _input.TextInputActive; set => _input.TextInputActive = value; }
    public bool FlightActive { get => _input.FlightActive; set => _input.FlightActive = value; }

    // The last fly-speed change the wheel made, for the overlay's readout.
    // Two scalar fields rather than one notice struct because the writer is
    // the game's input handler and the reader is the draw pass: each field is
    // atomically read on its own, so the worst a reader can see is a speed one
    // notch behind its timestamp. A struct pair could tear. A zero stamp is
    // 'no notch yet'.
    private float _speedNoticeValue = VirtualCameraService.DefaultMovementSpeed;
    private long _speedNoticeAtMs;

    public FreeCameraController(
        VirtualCameraService owner,
        Application.Input.CameraInputState input,
        IKeyState? keyState)
    {
        _owner = owner;
        _input = input;
        _keyState = keyState;
    }

    private Documents.Config.CameraConfiguration CameraSettings => _owner.CameraSettings;

    private Vector3? TrackedPivot => _owner.TrackedPivot;

    /// <summary>The wheel's last speed change, or null before the first notch.</summary>
    public FreeCameraSpeedNotice? SpeedNotice =>
        _speedNoticeAtMs != 0L
            ? new FreeCameraSpeedNotice(_speedNoticeValue, _speedNoticeAtMs)
            : null;

    /// <summary>The readout belongs to the camera whose wheel made it; a
    /// switch retires it rather than letting the incoming camera inherit a
    /// speed it was never set to.</summary>
    public void ClearSpeedNotice() => _speedNoticeAtMs = 0L;

    public void HandleInput(
        VirtualCamera live, MouseFrame* mouse, KeyboardFrame* keyboard)
    {
        // A locked camera holds its shot: the look-drag stops accumulating
        // (the lock block below eats the delta itself).
        // Free-camera look uses right-drag, as in Brio. Left-drag remains
        // available for selection and manipulation without changing the shot.
        if (!live.IsLocked && mouse != null
            && mouse->IsButtonDown(MouseState.Right))
        {
            if (mouse->Delta != Vector2.Zero)
                FlightActive = true;
            _freeMouseDelta += mouse->Delta;
            mouse->HandleDelta();
        }

        // The wheel is the fly speed, and the game never gets to see it. A
        // free camera replaces the view matrix and leaves the game orbiting
        // its own camera underneath, so an unconsumed scroll zooms a camera
        // nobody is looking through and desyncs the orbit state every reader
        // of the native camera still trusts. Consumed for a live free camera
        // whatever the wheel then does — locked, or movement switched off, it
        // is eaten and dropped.
        if (mouse != null)
        {
            if (!live.IsLocked && live.MovementEnabled)
            {
                int notches = FreeCameraSpeed.Notches(mouse->ScrollValue);
                if (notches != 0)
                {
                    live.MovementSpeed =
                        FreeCameraSpeed.Step(live.MovementSpeed, notches);
                    _speedNoticeValue = live.MovementSpeed;
                    // Fully qualified: Poser.Game.Environment is a namespace
                    // of this assembly and wins the plain name.
                    _speedNoticeAtMs = System.Environment.TickCount64;
                }
            }
            mouse->ScrollValue = 0;
        }

        // The UI owns the keyboard while anything is typing or an ImGui
        // item is active: flight keys stand down entirely — pressed keys
        // are neither moved on nor consumed (the modifier contract's
        // focus rule).
        if (keyboard == null || !live.MovementEnabled || SuppressFlightKeys)
            return;

        // Brio samples IKeyState for continuous movement. KeyboardFrame is
        // the consumable native input buffer, not the held-key authority.
        bool Held(VirtualKey key) => _keyState?[key] == true;
        int forwardBack = 0;
        if (Held(VirtualKey.W)) forwardBack -= 1;
        if (Held(VirtualKey.S)) forwardBack += 1;

        int leftRight = 0;
        if (Held(VirtualKey.A)) leftRight -= 1;
        if (Held(VirtualKey.D)) leftRight += 1;

        // The modifier contract's vertical map: Q or Space rises, E or C
        // falls. Shift-descend (Brio's map) is DEAD — Shift is a speed
        // modifier now, and a modifier must never be a motion key. The
        // axis travels with the input vector below, so it is the camera's
        // up rather than the world's — pitched down, rising also carries
        // you forward. Move2D is the switch that pins it to world vertical.
        int upDown = 0;
        if (Held(VirtualKey.Q) ||
            Held(VirtualKey.SPACE))
            upDown += 1;
        if (Held(VirtualKey.E) ||
            Held(VirtualKey.C))
            upDown -= 1;

        // Shift = faster, Ctrl = slower — increase and decrease, in that
        // order. Alt carries NO camera role: it is the visibility peek,
        // everywhere and exclusively.
        if (forwardBack != 0 || leftRight != 0 || upDown != 0)
            FlightActive = true;

        var settings = CameraSettings;
        _freeMoveSpeed = live.MovementSpeed;
        if (Held(VirtualKey.SHIFT))
            _freeMoveSpeed = live.MovementSpeed * settings.FastMultiplier;
        else if (Held(VirtualKey.CONTROL))
            _freeMoveSpeed = live.MovementSpeed * settings.SlowMultiplier;

        keyboard->HandleKey(VirtualKey.W);
        keyboard->HandleKey(VirtualKey.A);
        keyboard->HandleKey(VirtualKey.S);
        keyboard->HandleKey(VirtualKey.D);
        keyboard->HandleKey(VirtualKey.Q);
        keyboard->HandleKey(VirtualKey.E);
        keyboard->HandleKey(VirtualKey.C);
        // The modifiers this path reads are consumed with the letters
        // (Brio's EnableKeyHandlingOnKeyMod block, on by default): Shift
        // and Ctrl are the speed modifiers, so leaving them in the frame
        // hands the game a held modifier for every second the camera
        // flies fast. Turning the setting off gives the game those back —
        // Space included, since Space is half the rise/fall pair. Alt is
        // NOT consumed: the camera no longer reads it.
        if (settings.ConsumeModifiersWhileFlying)
        {
            keyboard->HandleKey(VirtualKey.SPACE);
            keyboard->HandleKey(VirtualKey.SHIFT);
            keyboard->HandleKey(VirtualKey.CONTROL);
        }

        if (live.IsLocked)
        {
            _freeForward = Vector3.Zero;
            return;
        }

        // Brio's FlipKeyBindsPastNinety, applied to the same two axes it
        // does: rolled past a quarter turn the screen's left is the world's
        // right, so the key that moved you screen-left keeps doing so.
        if (settings.FlipBindsPastNinety &&
            MathF.Abs(live.Roll) > MathF.PI / 2f)
        {
            leftRight = -leftRight;
            upDown = -upDown;
        }

        var input = new Vector3(leftRight, upDown, forwardBack);
        if (live.IsPortraitMode)
            input = Vector3.Transform(
                input,
                Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2f));

        // Brio's frame vector: the input rotated by the camera's yaw (and
        // pitch, unless lateral movement pins travel to the horizontal
        // plane).
        _freeForward = Vector3.Transform(
            input,
            Quaternion.CreateFromYawPitchRoll(
                live.Rotation.X,
                live.Move2D ? 0f : -live.Rotation.Y,
                live.Rotation.Z));
    }

    /// <summary>Brio's UpdateMatrix, whole: integrates the frame inputs into
    /// position/rotation and builds the fly-cam view matrix, roll applied as
    /// a Z-axis transform at the end.</summary>
    public Matrix4x4 UpdateMatrix(VirtualCamera live)
    {
        var mouse = _freeMouseDelta * live.MouseSensitivity * (MathF.PI / 180f);
        if (live.IsPortraitMode)
            mouse = new Vector2(-mouse.Y, mouse.X);

        var position = live.Position + _freeForward * _freeMoveSpeed;
        var rotation = live.Rotation;
        rotation.X -= mouse.X;
        rotation.Y = live.DelimitAngle
            ? rotation.Y + mouse.Y
            : Math.Clamp(rotation.Y + mouse.Y, -1.5f, 1.5f);
        // Tracking on a free camera: Follow carries the camera with the
        // pivot's motion, Pan turns it onto the pivot, both do both.
        if (live.IsTracking && TrackedPivot is { } pivot)
        {
            var mode = live.TrackingMode;
            if (mode is CameraTrackingMode.Follow or CameraTrackingMode.FollowAndPan
                && _freeFollowPivot is { } last)
                position += pivot - last;
            if (mode is CameraTrackingMode.Pan or CameraTrackingMode.FollowAndPan)
            {
                var toPivot = pivot - position;
                if (toPivot.LengthSquared() > 1e-6f)
                {
                    float flat = MathF.Sqrt(
                        toPivot.X * toPivot.X + toPivot.Z * toPivot.Z);
                    rotation.X = MathF.Atan2(toPivot.X, toPivot.Z);
                    rotation.Y = MathF.Atan2(toPivot.Y, flat);
                }
            }
            _freeFollowPivot = pivot;
        }
        else
            _freeFollowPivot = null;
        live.Position = position;
        live.Rotation = rotation;

        _freeMouseDelta = Vector2.Zero;
        _freeForward = Vector3.Zero;
        _freeMoveSpeed = live.MovementSpeed;

        var look = Vector3.Normalize(new Vector3(
            MathF.Sin(rotation.X) * MathF.Cos(rotation.Y),
            MathF.Sin(rotation.Y),
            MathF.Cos(rotation.X) * MathF.Cos(rotation.Y)));
        var right = Vector3.Normalize(Vector3.Cross(Vector3.UnitY, look));
        var up = Vector3.Cross(look, right);

        var matrix = new Matrix4x4(
            right.X, up.X, look.X, 0f,
            right.Y, up.Y, look.Y, 0f,
            right.Z, up.Z, look.Z, 0f,
            -position.X * right.X - position.Y * right.Y - position.Z * right.Z,
            -position.X * up.X - position.Y * up.Y - position.Z * up.Z,
            -position.X * look.X - position.Y * look.Y - position.Z * look.Z,
            1f);

        return Matrix4x4.Transform(
            matrix,
            Quaternion.CreateFromAxisAngle(Vector3.UnitZ, live.Roll));
    }
}
