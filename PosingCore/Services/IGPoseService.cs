using System;

namespace Poser.Services;

public interface IGPoseService : IDisposable
{
    /// <summary>
    /// Gets whether the client is currently in GPose mode.
    /// </summary>
    bool IsGPosing { get; }
}
