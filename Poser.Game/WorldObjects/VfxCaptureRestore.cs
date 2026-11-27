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
internal sealed class VfxCaptureRestore
{
    private readonly IWorldEffectAccess _world;

    public VfxCaptureRestore(IWorldEffectAccess world) => _world = world;

    public bool TryCapture(
        nint address,
        out WorldObjectIncarnation identity,
        out VfxStateSnapshot snapshot)
    {
        identity = default;
        snapshot = default;
        return _world.TryReadIncarnation(address, out identity)
            && (!identity.IsVfx
                || identity.ResourceIdentity != nint.Zero)
            && _world.TryReadVfxSnapshot(address, out snapshot)
            && snapshot.Playback != VfxPlaybackState.Unavailable;
    }

    public bool IsCurrent(WorldObjectIncarnation identity)
    {
        return _world.TryReadIncarnation(identity.Address, out var current)
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
        if (!_world.TryReadVfxPlayback(identity.Address, out actualPlayback))
            return VfxTransformWriteResult.PlaybackUnavailable;
        if (actualPlayback == VfxPlaybackState.Playing)
        {
            if (!_world.TryWriteVfxTransform(identity.Address, placement))
                return VfxTransformWriteResult.PlaybackUnavailable;
        }
        else if (actualPlayback is VfxPlaybackState.Paused
            or VfxPlaybackState.Inactive)
            _world.Write(identity.Address, placement);
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
        return _world.TryRestoreVfxState(identity.Address, snapshot);
    }
}
