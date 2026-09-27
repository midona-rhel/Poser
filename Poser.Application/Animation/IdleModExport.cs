using Poser.Documents.Animation;
using Poser.Domain.Identity;

namespace Poser.Application.Animation;

public interface IIdleModRuntime
{
    Task<IdleModPackage> CaptureAsync(ActorId actor);
}

public interface IIdleModExport
{
    bool Busy { get; }
    Task ExportAsync(ActorId actor, string destination);
}

public sealed class IdleModExport(IIdleModRuntime runtime) : IIdleModExport
{
    private int _busy;
    public bool Busy => Volatile.Read(ref _busy) != 0;
    public async Task ExportAsync(ActorId actor, string destination)
    {
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
            throw new InvalidOperationException("An idle export is already running.");
        try
        {
            var package = await runtime.CaptureAsync(actor);
            await Task.Run(() => package.WriteNew(destination));
        }
        finally { Volatile.Write(ref _busy, 0); }
    }
}
