using Poser.Documents.Animation;
using Poser.Domain.Identity;

namespace Poser.Application.Animation;

public interface IIdleModRuntime
{
    Task<IdleModChoices> DescribeAsync(ActorId actor);
    Task<IdleModPackage> CaptureAsync(ActorId actor, IdleModOptions options);
}

public sealed class IdleModExport(IIdleModRuntime runtime)
{
    private int _busy;
    public bool Busy => Volatile.Read(ref _busy) != 0;
    public Task<IdleModChoices> DescribeAsync(ActorId actor) => runtime.DescribeAsync(actor);
    public async Task ExportAsync(ActorId actor, string destination, IdleModOptions? options = null)
    {
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
            throw new InvalidOperationException("An idle export is already running.");
        try
        {
            var choices = await runtime.DescribeAsync(actor);
            options ??= choices.Default;
            choices.Validate(options);
            var package = await runtime.CaptureAsync(actor, options with { Name = options.Name.Trim() });
            await Task.Run(() => package.WriteNew(destination));
        }
        finally { Volatile.Write(ref _busy, 0); }
    }
}
