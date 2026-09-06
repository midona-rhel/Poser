namespace Poser.Application.Lifecycle;

/// <summary>Unwinds resources acquired by a host whose constructor did not finish.</summary>
public sealed class StartupCleanup(Action<Exception> reportFailure) : IDisposable
{
    private readonly Stack<Action> _cleanup = new();

    public void OnFailure(Action cleanup) => _cleanup.Push(cleanup);

    public void Complete() => _cleanup.Clear();

    public void Dispose()
    {
        while (_cleanup.TryPop(out var cleanup))
        {
            try { cleanup(); }
            catch (Exception error)
            {
                // Cleanup must continue without replacing the startup exception.
                try { reportFailure(error); }
                catch { }
            }
        }
    }
}
