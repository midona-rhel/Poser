using Poser.Application.World;
using Poser.Domain.Identity;

namespace Poser.Game.World;

// Native owners supply exact-instance cleanup; binding failure must not look up
// a saved address or append an acquisition/removal pair to the user's history.
internal sealed record WorldAcquisitionBinding(
    Func<SelectionId?> Resolve, Action Commit, Func<bool> Rollback);

internal sealed class PendingWorldAcquisition(WorldAcquisitionBinding binding,
    Func<SelectionId, WorldAcquisition> admit, Action<string> reportFailure)
{
    private readonly TaskCompletionSource<WorldAcquisition> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _ticks;
    private bool _settled;
    private string? _failure;
    private bool _reportedCleanupFailure;

    public Task<WorldAcquisition> Completion => _completion.Task;

    // Framework-thread only. A refused cleanup remains owned and is retried by
    // the service, even after its caller has received the failure result.
    public bool Tick(bool sessionCurrent)
    {
        if (_settled) return true;
        if (_failure == null)
        {
            if (!sessionCurrent)
                _failure = "World borrowing was cancelled because its session ended.";
            else
            {
                try
                {
                    if (binding.Resolve() is { } entity)
                    {
                        binding.Commit();
                        var result = admit(entity);
                        _settled = true;
                        _completion.TrySetResult(result);
                        return true;
                    }
                    if (++_ticks < 120) return false;
                    _failure = "The borrowed asset did not receive a scene identity.";
                }
                catch (Exception error)
                {
                    _failure = "World borrowing failed: " + error.Message;
                }
            }
        }

        try { _settled = binding.Rollback(); }
        catch (Exception error)
        {
            if (!_reportedCleanupFailure) reportFailure("World borrowing rollback failed: " + error.Message);
        }
        if (!_settled && !_reportedCleanupFailure)
        {
            reportFailure("World borrowing cleanup was refused; its exact acquisition is retained for retry.");
            _reportedCleanupFailure = true;
        }
        _completion.TrySetResult(new(WorldCommandStatus.Unavailable, Detail: _failure +
            (_settled ? " The acquisition was rolled back." : " Cleanup is still pending.")));
        return _settled;
    }
}
