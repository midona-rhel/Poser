using System;

namespace Poser.Services;

public interface IGPoseService : IDisposable
{
    /// <summary>
    /// Gets whether the client is currently in GPose mode.
    /// </summary>
    bool IsGPosing { get; }

    /// <summary>
    /// Closes an active GPose session during plugin unload. The implementation
    /// requires the framework update thread and shares the normal exit edge.
    /// </summary>
    void ExitForUnload();
}
