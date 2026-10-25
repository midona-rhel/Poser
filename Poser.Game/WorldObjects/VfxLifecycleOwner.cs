using Poser.Domain.Transforms;

namespace Poser.Game.WorldObjects;

internal enum VfxTransformWriteResult
{
    Written,
    Stale,
    PlaybackUnavailable,
}

/// <summary>
/// Owns VFX-specific capture, transform, and restore behavior for world
/// objects. Transform writes preserve playback state, and capture fails when
/// playback state cannot be read.
/// </summary>
internal sealed class VfxLifecycleOwner
{
    private readonly IVfxObjectPort _port;

    public VfxLifecycleOwner(IVfxObjectPort port) => _port = port;

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
