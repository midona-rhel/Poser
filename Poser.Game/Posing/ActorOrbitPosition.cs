using System.Numerics;

namespace Poser.Game.Posing;

/// <summary>Deferred native camera pivot for one held actor transform.</summary>
internal sealed class ActorOrbitPosition(Vector3 position, Vector3 defaultPosition)
{
    public Vector3 OriginalPosition { get; } = position;
    public Vector3 OriginalDefaultPosition { get; } = defaultPosition;
    public bool Applied { get; private set; }
    private Vector3 _drawPosition;
    private bool _pending;

    public void Schedule(Vector3 drawPosition, bool enabled)
    {
        _drawPosition = drawPosition;
        _pending = enabled;
    }

    public bool TryTake(Vector3 drawOffset, bool enabled, bool mouseHeld, bool cameraLocked,
        out Vector3 nativePosition)
    {
        nativePosition = default;
        if (!enabled) _pending = false;
        if (!_pending || mouseHeld || cameraLocked) return false;
        // Ktisis ActorEntity.UpdateGameObjectTransform: the displayed root
        // already includes DrawOffset; subtract it before native SetPosition.
        nativePosition = _drawPosition - drawOffset;
        _pending = false;
        Applied = true;
        return true;
    }
}
