using System.Numerics;
using System.Runtime.InteropServices;
using Dalamud.Game.ClientState.Keys;

using GameCamera = FFXIVClientStructs.FFXIV.Client.Game.Camera;
using RenderCamera = FFXIVClientStructs.FFXIV.Client.Graphics.Render.Camera;
using Poser.Game.Natives;

namespace Poser.Game.Cameras;

/// <summary>
/// The game's orbit camera with the fields FFXIVClientStructs does not map.
/// Offsets are Brio's BrioCamera (main, 2026-08) plus Ktisis's two vertical
/// clamp fields; both projects run these against the live client.
/// </summary>
[StructLayout(LayoutKind.Explicit, Size = 0x2B0)]
public struct NativeCamera
{
    [FieldOffset(NativeFields.NativeCamera_Camera)] public GameCamera Camera;
    [FieldOffset(NativeFields.NativeCamera_Position)] public Vector3 Position;

    [FieldOffset(NativeFields.NativeCamera_Distance)] public float Distance;     // default 2.5 in GPose
    [FieldOffset(NativeFields.NativeCamera_MinDistance)] public float MinDistance;  // 1.5
    [FieldOffset(NativeFields.NativeCamera_MaxDistance)] public float MaxDistance;  // 20

    [FieldOffset(NativeFields.NativeCamera_FoV)] public float FoV;          // default 0.78
    [FieldOffset(NativeFields.NativeCamera_MinFoV)] public float MinFoV;       // 0.69
    [FieldOffset(NativeFields.NativeCamera_MaxFoV)] public float MaxFoV;       // 0.78
    [FieldOffset(NativeFields.NativeCamera_Zoom)] public float Zoom;         // FoV offset, -0.5..0.5

    [FieldOffset(NativeFields.NativeCamera_Angle)] public Vector2 Angle;

    // The vertical orbit clamp, loosened while delimited: UpdateRotation clamps
    // the pitch to [+0x158, +0x15C] (patchcheck Camera.DirVMin / DirVMax).
    [FieldOffset(NativeFields.NativeCamera_PitchMin)] public float PitchMin;     // -1.4 stock
    [FieldOffset(NativeFields.NativeCamera_PitchMax)] public float PitchMax;     // 1.25 stock

    [FieldOffset(NativeFields.NativeCamera_Pan)] public Vector2 Pan;        // pan, tilt
    [FieldOffset(NativeFields.NativeCamera_Roll)] public float Roll;

    [FieldOffset(NativeFields.NativeCamera_Mode)] public int Mode;           // 0 first person, 1 third

    [FieldOffset(NativeFields.NativeCamera_Collide)] public Vector2 Collide;

    // The native orbit-position builder compares yaw with the previous orbit
    // position (0x270) and clamps its correction to +/-4.5 degrees. It checks
    // 0x245 to bypass that correction for a discontinuous write, then clears
    // the byte at the end of the same update. Verified in the 2026-09 client.
    [FieldOffset(NativeFields.NativeCamera__skipOrbitCorrection)] private byte _skipOrbitCorrection;

    internal void SetOrbitAngle(Vector2 value)
    {
        Angle = value;
        _skipOrbitCorrection = 1;
    }

    /// <summary>Brio's view rotation for seeding a free cam from the orbit
    /// state: yaw from angle minus pan, pitch negated.</summary>
    public readonly Vector3 RotationAsVector3 =>
        new(Angle.X - Pan.X, -Angle.Y - Pan.Y, 0f);

    internal readonly bool TryGetRenderedPose(out Vector3 position, out Vector3 rotation, out float roll)
    {
        position = rotation = default;
        roll = 0;
        var view = Camera.CameraBase.SceneCamera.ViewMatrix;
        view.M44 = 1f;
        if (!Matrix4x4.Invert(view, out var inverse))
            return false;
        var look = Vector3.Normalize(new Vector3(view.M13, view.M23, view.M33));
        var right = Vector3.Normalize(new Vector3(view.M11, view.M21, view.M31));
        float flat = MathF.Sqrt(look.X * look.X + look.Z * look.Z);
        float yaw = flat > 1e-6f ? MathF.Atan2(look.X, look.Z) : RotationAsVector3.X;
        float pitch = MathF.Atan2(look.Y, flat);
        var unrolledRight = new Vector3(MathF.Cos(yaw), 0, -MathF.Sin(yaw));
        var unrolledUp = Vector3.Cross(look, unrolledRight);
        // Free-camera view axes are columns. Its post-multiplied roll turns
        // the right column toward -up; use that convention when extracting it.
        float renderedRoll = MathF.Atan2(-Vector3.Dot(right, unrolledUp), Vector3.Dot(right, unrolledRight));
        if (!float.IsFinite(inverse.M41) || !float.IsFinite(inverse.M42) || !float.IsFinite(inverse.M43)
            || !float.IsFinite(yaw) || !float.IsFinite(pitch) || !float.IsFinite(renderedRoll))
            return false;
        position = inverse.Translation;
        rotation = new Vector3(yaw, pitch, 0);
        roll = renderedRoll;
        return true;
    }

    /// <summary>Brio's RealPosition (CameraExtensions.GetPosition): the eye
    /// the frame is RENDERED from, inverted out of the view matrix. The
    /// scene camera's Position field is not it — a free camera replaces the
    /// view matrix and never writes that field, which the game goes on
    /// orbiting under the native input a free camera leaves to it.</summary>
    public readonly Vector3 GetPosition()
    {
        var view = Camera.CameraBase.SceneCamera.ViewMatrix;
        view.M44 = 1f;
        return Matrix4x4.Invert(view, out var inverted)
            ? inverted.Translation
            : Camera.CameraBase.SceneCamera.Position;
    }
}

/// <summary>
/// The render camera with Ktisis's orthographic fields mapped past the
/// FFXIVClientStructs coverage.
/// </summary>
[StructLayout(LayoutKind.Explicit)]
public struct RenderCameraEx
{
    [FieldOffset(NativeFields.RenderCameraEx_RenderCamera)] public RenderCamera RenderCamera;

    // The field of view SetMatrices reads is +0x1E8 (ClientStructs FoV); +0x1EC
    // is only ever written by the game.
    [FieldOffset(NativeFields.RenderCameraEx_FoV)] public float FoV;
    [FieldOffset(NativeFields.RenderCameraEx_AspectRatio)] public float AspectRatio;

    [FieldOffset(NativeFields.RenderCameraEx_OrthographicZoom)] public float OrthographicZoom;
    [FieldOffset(NativeFields.RenderCameraEx_OrthographicEnabled)] public bool OrthographicEnabled;
}

/// <summary>One frame of mouse state as the game's input handler sees it.
/// Brio's layout; the delta fields are writable so a consumer can eat the
/// look-drag before the game orbits with it.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct MouseFrame
{
    public int PositionX;
    public int PositionY;
    public int ScrollValue;
    public MouseState ButtonsPressed;
    public MouseState ButtonsClicked;
    public ulong Unknown1;
    public int DeltaX;
    public int DeltaY;

    public readonly bool IsButtonDown(MouseState button) =>
        (ButtonsPressed & button) != 0;

    public readonly Vector2 Delta => new(DeltaX, DeltaY);

    /// <summary>Consumes the delta so the game does not also act on it.</summary>
    public void HandleDelta()
    {
        DeltaX = 0;
        DeltaY = 0;
    }
}

[System.Flags]
public enum MouseState
{
    None = 0,
    Left = 1,
    Middle = 2,
    Right = 4,
}

/// <summary>One frame of keyboard state as the game's input handler sees it.
/// Brio's layout: one uint per virtual key, non-zero while held; zeroing an
/// entry consumes the key for the rest of the frame.</summary>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct KeyboardFrame
{
    public const int KeyStateLength = 254;

    public byte Unknown1;
    public fixed uint KeyState[KeyStateLength];

    public bool KeyDown(VirtualKey key) => KeyState[(int)key] != 0;

    public void HandleKey(VirtualKey key) => KeyState[(int)key] = 0;
}
