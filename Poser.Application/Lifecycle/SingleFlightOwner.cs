using Poser.Domain.Identity;
using Poser.Domain.Operations;

namespace Poser.Application.Lifecycle;

/// <summary>Shared state for an operation that may be cancelled or invalidated.</summary>
internal abstract class SingleFlightOperation
{
    public required Guid OperationId { get; init; }
    public required OperationEpoch Epoch { get; init; }
    public required SessionGeneration Session { get; init; }
    public required ActorId Target { get; init; }
    public bool Invalidated;
    public bool TerminalPublished;
}

/// <summary>
/// Tracks an operation's task, cancellation, progress, and result.
/// Callers must check Busy and Closed before admitting work; Admit does not
/// enforce either. Updates from a replaced operation are ignored, and its
/// final result can be published only once.
/// </summary>
internal sealed class SingleFlightOwner<TOperation, TProgress>
    where TOperation : SingleFlightOperation
    where TProgress : class
{
    private readonly object _gate = new();
    private readonly Func<TOperation, TProgress, TProgress>? _shapeStep;

    /// <summary>Cancelled by Drain. Kept alive because a task can still read
    /// its token after the timed wait returns.</summary>
    private readonly CancellationTokenSource _disposal = new();

    private TProgress? _progress;
    private OperationReceipt? _receipt;
    private CancellationTokenSource? _cancellation;
    private Task? _task;
    private TOperation? _current;
    private OperationEpoch _epoch;

    /// <param name="shapeStep">Optionally adjusts progress while holding the
    /// same lock used for publication.</param>
    public SingleFlightOwner(Func<TOperation, TProgress, TProgress>? shapeStep = null) =>
        _shapeStep = shapeStep;

    /// <summary>Notifies observers of published changes. Exceptions from
    /// observers are caught by RaiseChanged.</summary>
    public event Action? Changed;

    public TProgress? Progress => _progress;

    public OperationReceipt? Receipt => _receipt;

    public bool Busy => _task is { IsCompleted: false };

    /// <summary>The running task's join handle; completed when idle.</summary>
    public Task Completion => _task ?? Task.CompletedTask;

    /// <summary>Whether the caller has marked this owner closed to new work.</summary>
    public bool Closed { get; private set; }

    /// <summary>Cancelled at drain. Waits that must outlive a user cancel —
    /// a rollback's release barrier — end here instead.</summary>
    public CancellationToken Disposal => _disposal.Token;

    /// <summary>Makes a new operation current: a fresh cancellation source,
    /// the next epoch, a Pending receipt and the first progress, all
    /// published under the gate.</summary>
    public TOperation Admit(
        Func<OperationEpoch, TOperation> create, TProgress first,
        out CancellationToken cancellation)
    {
        _cancellation?.Dispose();
        _cancellation = new CancellationTokenSource();
        cancellation = _cancellation.Token;
        _epoch = _epoch.IsValid ? _epoch.Next() : OperationEpoch.First;
        var operation = create(_epoch);
        lock (_gate)
        {
            _current = operation;
            _receipt = OperationReceipt.Pending(
                operation.OperationId, operation.Epoch, operation.Session, operation.Target);
            _progress = first;
        }
        return operation;
    }

    /// <summary>Runs <paramref name="body"/> as the slot's task.</summary>
    public void Run(Func<Task> body) => _task = Task.Run(body, CancellationToken.None);

    /// <summary>Cooperative cancellation of the running operation.</summary>
    public void Cancel() => _cancellation?.Cancel();

    /// <summary>Replaces the current, non-terminal operation's progress under
    /// the gate when <paramref name="replace"/> answers one. The caller raises
    /// <see cref="Changed"/>.</summary>
    public bool TryReplaceProgress(Func<TOperation, TProgress?, TProgress?> replace)
    {
        lock (_gate)
        {
            if (_current is not { TerminalPublished: false } operation
                || replace(operation, _progress) is not { } next)
                return false;
            _progress = next;
            return true;
        }
    }

    /// <summary>Publishes a non-terminal step for the exact current
    /// operation only.</summary>
    public void PublishStep(TOperation operation, TProgress progress)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(_current, operation) || operation.TerminalPublished)
                return;
            _progress = _shapeStep is null ? progress : _shapeStep(operation, progress);
        }
        RaiseChanged();
    }

    /// <summary>Publishes the terminal progress and receipt exactly once,
    /// and only while the operation is still the current one.</summary>
    public void PublishTerminal(TOperation operation, TProgress progress, OperationReceipt receipt)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(_current, operation) || operation.TerminalPublished)
                return;
            operation.TerminalPublished = true;
            _progress = progress;
            _receipt = receipt;
        }
        RaiseChanged();
    }

    public void RaiseChanged()
    {
        try
        {
            Changed?.Invoke();
        }
        catch
        {
            // A failed observer must not turn a completed operation into a failure.
        }
    }

    /// <summary>Closes admission permanently. False when it already was.</summary>
    public bool Close()
    {
        if (Closed)
            return false;
        Closed = true;
        return true;
    }

    /// <summary>
    /// Cancels the current operation and waits up to
    /// <paramref name="bound"/>. Returning after the timeout does not prove the
    /// task has stopped; operation code must check cancellation before each
    /// later mutation.
    /// </summary>
    public void Drain(TimeSpan bound)
    {
        _cancellation?.Cancel();
        _disposal.Cancel();
        try
        {
            _task?.Wait(bound);
        }
        catch (AggregateException)
        {
            // The task completed by cancellation or failure. Its workflow
            // is responsible for recording the result.
        }
    }
}
