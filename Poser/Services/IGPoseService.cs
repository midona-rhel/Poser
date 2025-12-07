using System;

namespace Poser.Services;

public interface IGPoseService : IDisposable
{
    /// <summary>
    /// Gets whether the client is currently in GPose mode.
    /// </summary>
    bool IsGPosing { get; }

    /// <summary>
    /// Event fired when GPose state changes.
    /// </summary>
    /// <remarks>
    /// Prefer using <see cref="Core.IEventBus"/> and subscribing to <see cref="Core.GPoseStateChangedEvent"/> instead.
    /// </remarks>
    [Obsolete("Use IEventBus.Subscribe<GPoseStateChangedEvent> instead")]
    event Action<bool>? OnGPoseStateChanged;
}
