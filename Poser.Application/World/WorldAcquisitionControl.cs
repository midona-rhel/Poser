using Poser.Application.Lifecycle;
using Poser.Application.Selection;
using Poser.Domain.Operations;

namespace Poser.Application.World;

public interface IWorldAcquisitionControl
{
    void Acquire(WorldCandidateId candidate);
}

/// <summary>Borrow completion and selection advance independently of the overlay that initiated them.</summary>
public sealed class WorldAcquisitionControl(IWorldService world, ISessionGenerationSource sessions,
    SelectionSession selection, Action<string> reportFailure) : IWorldAcquisitionControl
{
    private (SessionGeneration Session, Task<WorldAcquisition> Result)? _pending;

    public void Acquire(WorldCandidateId candidate)
    {
        Tick();
        if (_pending != null || sessions.ActiveSessionGeneration is not { } session) return;
        _pending = (session, world.Acquire(candidate));
    }

    public void Tick()
    {
        if (_pending is not { } pending) return;
        if (sessions.ActiveSessionGeneration != pending.Session) { _pending = null; return; }
        if (!pending.Result.IsCompleted) return;
        _pending = null;
        if (pending.Result.IsCompletedSuccessfully && pending.Result.Result is { Success: true, Entity: { } entity })
            selection.Select(entity);
        else reportFailure(pending.Result.IsCompletedSuccessfully
            ? pending.Result.Result.Detail ?? "That world asset could not be borrowed."
            : "The world borrowing command failed.");
    }
}
