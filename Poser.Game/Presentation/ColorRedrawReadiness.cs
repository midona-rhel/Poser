namespace Poser.Game.Presentation;

/// <summary>Readiness starts at the matching redraw event, never at the request.</summary>
internal sealed class ColorRedrawReadiness(Func<long>? clock = null)
{
    private readonly Func<long> _clock = clock ?? (() => System.Environment.TickCount64);
    private readonly long _started = (clock ?? (() => System.Environment.TickCount64))();
    private long? _eventFrame;
    public void Redrawn(long frame) => _eventFrame ??= frame;
    public bool IsReady(long frame, bool readable) => _eventFrame is { } at && frame > at && readable;
    public bool IsExpired => _clock() - _started >= 5000;
}
