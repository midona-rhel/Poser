using Poser.Services;

namespace Poser.Tests.Mocks;

public class MockGPoseService : IGPoseService
{
    public bool IsGPosing { get; set; }

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
