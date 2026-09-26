using System.Numerics;
using System.Runtime.InteropServices;
using Dalamud.Game.ClientState.Keys;

using GameCamera = FFXIVClientStructs.FFXIV.Client.Game.Camera;
using RenderCamera = FFXIVClientStructs.FFXIV.Client.Graphics.Render.Camera;

namespace Poser.Game.Cameras;

/// <summary>
/// The game's orbit camera with the fields FFXIVClientStructs does not map.
/// Offsets are Brio's BrioCamera (main, 2026-08) plus Ktisis's two vertical
/// clamp fields; both projects run these against the live client.
/// </summary>
[StructLayout(LayoutKind.Explicit, Size = 0x2B0)]
public struct NativeCamera
{
    [FieldOffset(0x000)] public GameCamera Camera;
    [FieldOffset(0x060)] public Vector3 Position;

    [FieldOffset(0x124)] public float Distance;     // default 2.5 in GPose
    [FieldOffset(0x128)] public float MinDistance;  // 1.5
    [FieldOffset(0x12C)] public float MaxDistance;  // 20

    [FieldOffset(0x130)] public float FoV;          // default 0.78
    [FieldOffset(0x134)] public float MinFoV;       // 0.69
    [FieldOffset(0x138)] public float MaxFoV;       // 0.78
    [FieldOffset(0x13C)] public float Zoom;         // FoV offset, -0.5..0.5

    [FieldOffset(0x140)] public Vector2 Angle;

    // Ktisis: the vertical orbit clamp, loosened while delimited.
    [FieldOffset(0x158)] public float YMax;         // -1.4 stock
    [FieldOffset(0x15C)] public float YMin;         // 1.25 stock

    [FieldOffset(0x160)] public Vector2 Pan;        // pan, tilt
    [FieldOffset(0x170)] public float Roll;

    [FieldOffset(0x180)] public int Mode;           // 0 first person, 1 third

    [FieldOffset(0x218)] public Vector2 Collide;

    /// <summary>Brio's view rotation for seeding a free cam from the orbit
    /// state: yaw from angle minus pan, pitch negated.</summary>
    public readonly Vector3 RotationAsVector3 =>
        new(Angle.X - Pan.X, -Angle.Y - Pan.Y, 0f);

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
    [FieldOffset(0x000)] public RenderCamera RenderCamera;

    [FieldOffset(0x1EC)] public float FoV;
    [FieldOffset(0x1F0)] public float AspectRatio;

    [FieldOffset(0x1FC)] public float OrthographicZoom;
    [FieldOffset(0x200)] public bool OrthographicEnabled;
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
