#if DEBUG
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Game.ClientState.Keys;

namespace Poser.Game.Cameras;

/// <summary>
/// Debug builds only. Lets the debug bridge tell the game that keys are held,
/// as if they were pressed on the keyboard, and records the game camera on
/// each of those frames. It exists to reproduce camera problems that only show
/// while a key is held.
/// </summary>
internal sealed unsafe partial class NativeCameraHooks
{
    /// <summary>What <see cref="DebugKeySamples"/> holds for each frame, in order.</summary>
    public const string DebugKeyColumns =
        "angleX angleY panX panY roll distance positionX positionY positionZ lookAtX lookAtY lookAtZ";

    private readonly object _debugKeyLock = new();
    private readonly List<float[]> _debugKeySamples = new();
    private VirtualKey[] _debugKeys = [];
    private int _debugKeyFramesLeft;
    private int _debugKeyFrame;

    /// <summary>Holds the keys for the next <paramref name="frames"/> frames and
    /// starts a new recording. After that many frames, the same number of frames
    /// is recorded with nothing held.</summary>
    public void DebugHoldKeys(IReadOnlyList<VirtualKey> keys, int frames)
    {
        lock (_debugKeyLock)
        {
            _debugKeys = keys.ToArray();
            _debugKeyFramesLeft = frames * 2;
            _debugKeyFrame = 0;
            _debugKeySamples.Clear();
        }
    }

    public int DebugKeyFramesLeft
    {
        get { lock (_debugKeyLock) return _debugKeyFramesLeft; }
    }

    public float[][] DebugKeySamples
    {
        get { lock (_debugKeyLock) return _debugKeySamples.ToArray(); }
    }

    /// <summary>What <see cref="DebugRollingSamples"/> holds for each frame: the
    /// camera values, then whether the game saw each of these keys held.</summary>
    public const string DebugRollingColumns = DebugKeyColumns + " A D W S SHIFT CONTROL";

    private const int DebugRollingFrames = 3600;
    private readonly Queue<float[]> _debugRolling = new();

    /// <summary>While on, the last minute or so of frames is kept, so a problem
    /// someone makes happen by hand can be read afterwards.</summary>
    public bool DebugRollingOn { get; set; }

    public float[][] DebugRollingSamples
    {
        get { lock (_debugKeyLock) return _debugRolling.ToArray(); }
    }

    private void RecordRolling(KeyboardFrame* keyboard)
    {
        var camera = _owner.Native;
        if (!DebugRollingOn || keyboard == null || camera == null)
            return;
        var scene = &camera->Camera.CameraBase.SceneCamera;
        Vector3 position = scene->Position;
        Vector3 lookAt = scene->LookAtVector;
        float Key(VirtualKey key) => keyboard->KeyState[(int)key];
        lock (_debugKeyLock)
        {
            _debugRolling.Enqueue(
            [
                camera->Angle.X, camera->Angle.Y, camera->Pan.X, camera->Pan.Y, camera->Roll, camera->Distance,
                position.X, position.Y, position.Z, lookAt.X, lookAt.Y, lookAt.Z,
                Key(VirtualKey.A), Key(VirtualKey.D), Key(VirtualKey.W), Key(VirtualKey.S),
                Key(VirtualKey.SHIFT), Key(VirtualKey.CONTROL),
            ]);
            while (_debugRolling.Count > DebugRollingFrames)
                _debugRolling.Dequeue();
        }
    }

    private void ApplyDebugKeys(KeyboardFrame* keyboard)
    {
        RecordRolling(keyboard);
        lock (_debugKeyLock)
        {
            if (_debugKeyFramesLeft <= 0 || keyboard == null)
                return;
            int held = (_debugKeyFramesLeft + _debugKeyFrame) / 2;
            if (_debugKeyFrame < held)
            {
                // The game marks a key that is down with 1, and adds 2 on the
                // frame it went down.
                uint state = _debugKeyFrame == 0 ? 3u : 1u;
                foreach (var key in _debugKeys)
                    keyboard->KeyState[(int)key] = state;
            }

            var camera = _owner.Native;
            if (camera != null)
            {
                var scene = &camera->Camera.CameraBase.SceneCamera;
                Vector3 position = scene->Position;
                Vector3 lookAt = scene->LookAtVector;
                _debugKeySamples.Add(
                [
                    camera->Angle.X, camera->Angle.Y, camera->Pan.X, camera->Pan.Y, camera->Roll, camera->Distance,
                    position.X, position.Y, position.Z, lookAt.X, lookAt.Y, lookAt.Z,
                ]);
            }
            _debugKeyFrame++;
            _debugKeyFramesLeft--;
        }
    }
}
#endif
