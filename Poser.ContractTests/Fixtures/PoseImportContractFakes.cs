using Poser.Application.Lifecycle;
using Poser.Application.Operations;

namespace Poser.ContractTests.Fixtures;

/// <summary>Framework-owned session identity seam for import contract tests.</summary>
internal sealed class FakeSessionGenerationSource : ISessionGenerationSource
{
    public SessionGeneration? ActiveSessionGeneration { get; set; }
}

/// <summary>Deterministic framework tick queue used by import tests that need
/// to distinguish accepted/pending from terminal completion.</summary>
internal sealed class FakeFrameworkTicks
{
    private readonly List<(int Delay, Action Callback)> _queued = new();

    public IReadOnlyList<(int Delay, Action Callback)> Queued => _queued;

    public void Enqueue(Action callback, int delayTicks = 0) =>
        _queued.Add((delayTicks, callback));

    public void RunNext()
    {
        if (_queued.Count == 0)
            return;
        var next = _queued[0];
        _queued.RemoveAt(0);
        next.Callback();
    }
}
