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
#pragma warning disable CS0618
                OnGPoseStateChanged?.Invoke(value);
#pragma warning restore CS0618
            }
        }
    }

#pragma warning disable CS0618
    public event Action<bool>? OnGPoseStateChanged;
#pragma warning restore CS0618

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

    /// <summary>
    /// Sets the GPose state directly.
    /// </summary>
    public void SetGPoseState(bool isGPosing) => IsGPosing = isGPosing;
}
