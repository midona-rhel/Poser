using System;
using System.Numerics;
using Dalamud.Game.ClientState.Keys;
using Dalamud.Plugin.Services;
using Poser.Domain.Cameras;
using Poser.Domain.Scene;

namespace Poser.Game.Cameras;

/// <summary>
/// The free camera's flight: the frame inputs the game's input handler
/// writes (look drag, movement, wheel speed) and the view matrix the scene
/// phase builds from them, plus the fly-speed readout.
/// </summary>
internal sealed unsafe class FreeCameraController
{
    private readonly VirtualCameraService _owner;
    private readonly IKeyState? _keyState;

    // Input collected by the input hook and consumed by the scene-update hook.
    private Vector3 _freeForward;
    /// <summary>Where the tracked pivot was last frame: a free camera in
    /// Follow moves by the pivot's motion, not to the pivot.</summary>
    private Vector3? _freeFollowPivot;
    private Vector2 _freeMouseDelta;
    private float _freeMoveSpeed = VirtualCameraService.DefaultMovementSpeed;

    /// <summary>Set per UI frame: while typing or an active ImGui item
    /// owns the keyboard, flight keys stand down (the modifier contract's
    /// focus rule). Written from the draw side, read on the camera's
    /// update.</summary>
    private readonly Application.Input.CameraInputState _input;
    public bool SuppressFlightKeys { get => _input.TextInputActive; set => _input.TextInputActive = value; }
    public bool FlightActive { get => _input.FlightActive; set => _input.FlightActive = value; }

    // Last wheel-selected speed and timestamp. Separate reads can observe different
    // updates; a zero timestamp means no notice.
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
        // Only unlocked right-drag rotates the camera. Locked mouse input stays untouched.
        if (!live.IsLocked && mouse != null
            && mouse->IsButtonDown(MouseState.Right))
        {
            if (mouse->Delta != Vector2.Zero)
                FlightActive = true;
            _freeMouseDelta += mouse->Delta;
            mouse->HandleDelta();
        }

        // Consume the wheel even when locked or movement is disabled, so it cannot
        // change the underlying game camera.
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

        // Leave keyboard input untouched while the UI suppresses flight.
        if (keyboard == null || !live.MovementEnabled || SuppressFlightKeys)
            return;

        // Read held keys from IKeyState; clear consumed keys in KeyboardFrame.
        bool Held(VirtualKey key) => _keyState?[key] == true;
        int forwardBack = 0;
        if (Held(VirtualKey.W)) forwardBack -= 1;
        if (Held(VirtualKey.S)) forwardBack += 1;

        int leftRight = 0;
        if (Held(VirtualKey.A)) leftRight -= 1;
        if (Held(VirtualKey.D)) leftRight += 1;

        // Q/Space rises and E/C descends along camera up. Move2D ignores pitch
        // when rotating the input vector.
        int upDown = 0;
        if (Held(VirtualKey.Q) ||
            Held(VirtualKey.SPACE))
            upDown += 1;
        if (Held(VirtualKey.E) ||
            Held(VirtualKey.C))
            upDown -= 1;

        // Shift selects the fast multiplier; otherwise Ctrl selects the slow one.
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
        // Optionally consume Space and speed modifiers as well as movement letters.
        // Alt is neither read nor consumed here.
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

        // Optionally reverse horizontal and vertical input past a 90-degree roll.
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

        // Rotate movement into the camera frame, ignoring pitch in Move2D mode.
        _freeForward = Vector3.Transform(
            input,
            Quaternion.CreateFromYawPitchRoll(
                live.Rotation.X,
                live.Move2D ? 0f : -live.Rotation.Y,
                live.Rotation.Z));
    }

    /// <summary>Integrates the frame inputs into
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
