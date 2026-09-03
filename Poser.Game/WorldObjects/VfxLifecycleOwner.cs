namespace Poser.Game.WorldObjects;

internal enum VfxTransformWriteResult
{
    Written,
    Stale,
    PlaybackUnavailable,
}

/// <summary>
/// The hidden VFX half of world-object ownership. It keeps native timing and
/// rollback policy out of the inspector: transforms restate placement without
/// replaying the effect, while capture refuses when native playback is
/// unavailable.
/// </summary>
internal sealed class VfxLifecycleOwner
{
    private readonly IWorldObjectPort _port;

    public VfxLifecycleOwner(IWorldObjectPort port) => _port = port;

    public bool TryCapture(
        nint address,
        out WorldObjectIncarnation identity,
        out VfxStateSnapshot snapshot)
    {
        identity = default;
        snapshot = default;
        return _port.TryReadIncarnation(address, out identity)
            && (!identity.IsVfx
                || identity.ResourceIdentity != nint.Zero)
            && _port.TryReadVfxSnapshot(address, out snapshot)
            && snapshot.Playback != VfxPlaybackState.Unavailable;
    }

    public bool IsCurrent(WorldObjectIncarnation identity)
    {
        return _port.TryReadIncarnation(identity.Address, out var current)
            && current.IsVfx
            && current == identity;
    }

    public VfxTransformWriteResult WriteTransform(
        WorldObjectIncarnation identity,
        in Transform placement,
        out VfxPlaybackState actualPlayback)
    {
        actualPlayback = VfxPlaybackState.Unavailable;
        if (!IsCurrent(identity))
            return VfxTransformWriteResult.Stale;
        if (!_port.TryReadVfxPlayback(identity.Address, out actualPlayback))
            return VfxTransformWriteResult.PlaybackUnavailable;
        if (actualPlayback == VfxPlaybackState.Playing)
        {
            if (!_port.TryWriteVfxTransform(identity.Address, placement))
                return VfxTransformWriteResult.PlaybackUnavailable;
        }
        else if (actualPlayback is VfxPlaybackState.Paused
            or VfxPlaybackState.Inactive)
            _port.Write(identity.Address, placement);
        else
            return VfxTransformWriteResult.PlaybackUnavailable;
        return VfxTransformWriteResult.Written;
    }

    public bool Restore(
        WorldObjectIncarnation identity,
        VfxStateSnapshot snapshot)
    {
        if (!IsCurrent(identity))
            return true;
        return _port.TryRestoreVfxState(identity.Address, snapshot);
    }
}
