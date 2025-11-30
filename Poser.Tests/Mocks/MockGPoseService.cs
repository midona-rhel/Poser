using System;
using Poser.Services;

namespace Poser.Tests.Mocks;

public class MockGPoseService : IGPoseService
{
    private bool _isGPosing;

    public bool IsGPosing
    {
        get => _isGPosing;
        set
        {
            if (_isGPosing != value)
            {
                _isGPosing = value;
                OnGPoseStateChanged?.Invoke(value);
            }
        }
    }

    public event Action<bool>? OnGPoseStateChanged;

    public void Dispose()
    {
        // No-op for mock
    }

    /// <summary>
    /// Simulates entering GPose.
    /// </summary>
    public void EnterGPose() => IsGPosing = true;

    /// <summary>
    /// Simulates exiting GPose.
    /// </summary>
    public void ExitGPose() => IsGPosing = false;
}
