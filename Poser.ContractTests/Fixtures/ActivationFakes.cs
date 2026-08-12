namespace Poser.ContractTests.Fixtures;

/// <summary>
/// Test-only composition fixture. Startup currently resolves concrete
/// services directly, so this records the desired activation/cleanup contract
/// without pretending to exercise the production constructor.
/// </summary>
internal sealed class FakeActivationHost
{
    private readonly List<FakeActivationResource> _resources = new();

    public IReadOnlyList<FakeActivationResource> Resources => _resources;

    public ActivationResult Activate(
        IReadOnlyList<Func<FakeActivationResource>> factories,
        int? failAt = null)
    {
        try
        {
            for (var index = 0; index < factories.Count; index++)
            {
                if (failAt == index)
                    throw new InvalidOperationException($"activation step {index} failed");

                _resources.Add(factories[index]());
            }

            return ActivationResult.Successful();
        }
        catch (Exception ex)
        {
            DisposeReverse();
            return ActivationResult.Failed(ex.Message);
        }
    }

    public void Dispose() => DisposeReverse();

    private void DisposeReverse()
    {
        for (var index = _resources.Count - 1; index >= 0; index--)
            _resources[index].Dispose();
    }
}

internal sealed class FakeActivationResource(string name, IList<string> events) : IDisposable
{
    public string Name { get; } = name;

    public void Dispose() => events.Add($"dispose:{Name}");
}

internal readonly record struct ActivationResult(bool Success, string? Detail)
{
    public static ActivationResult Successful() => new(true, null);
    public static ActivationResult Failed(string detail) => new(false, detail);
}
