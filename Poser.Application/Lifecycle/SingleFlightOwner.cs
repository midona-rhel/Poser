using Poser.Domain.Identity;
using Poser.Domain.Operations;

namespace Poser.Application.Lifecycle;

/// <summary>What every single-flight operation carries: the receipt identity
/// it was admitted under and the two flags publication and the per-phase
/// guards read.</summary>
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
/// The single-flight slot an owner runs its operations in: one operation at a
/// time, an owner-local epoch, Pending-to-terminal receipts, late-completion
/// armor on publication, and the bounded cancel/drain before disposal.
///
/// <para>Publication is refused for anything but the CURRENT operation, and a
/// terminal publishes exactly once: a late completion can neither overwrite a
/// newer operation's read models nor its own terminal. The owner decides its
/// own admission refusals and what its operations do.</para>
/// </summary>
internal sealed class SingleFlightOwner<TOperation, TProgress>
    where TOperation : SingleFlightOperation
    where TProgress : class
{
    private readonly object _gate = new();
    private readonly Func<TOperation, TProgress, TProgress>? _shapeStep;

    /// <summary>Cancelled only by <see cref="Drain"/> and deliberately never
    /// disposed: a task the drain abandoned after its bounded join still reads
    /// its token, and a timer-less source holds nothing worth releasing.</summary>
    private readonly CancellationTokenSource _disposal = new();

    private TProgress? _progress;
    private OperationReceipt? _receipt;
    private CancellationTokenSource? _cancellation;
    private Task? _task;
    private TOperation? _current;
    private OperationEpoch _epoch;

    /// <param name="shapeStep">Rewrites a non-terminal step under the
    /// publication gate, for an owner whose steps depend on state another
    /// thread sets under that gate.</param>
    public SingleFlightOwner(Func<TOperation, TProgress, TProgress>? shapeStep = null) =>
        _shapeStep = shapeStep;

    /// <summary>Raised after every publication. Observer failures never
    /// poison the operation.</summary>
    public event Action? Changed;

    public TProgress? Progress => _progress;

    public OperationReceipt? Receipt => _receipt;

    public bool Busy => _task is { IsCompleted: false };

    /// <summary>The running task's join handle; completed when idle.</summary>
    public Task Completion => _task ?? Task.CompletedTask;

    /// <summary>Admission is closed for good.</summary>
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
            // Observer failures never poison the transaction.
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
    /// Bounded cancel/drain: the operation's token and the disposal token
    /// cancel, and the task is joined inside <paramref name="bound"/>. The
    /// join is bounded because a task parked on a framework hop cannot finish
    /// while disposal holds the framework thread; an abandoned task cannot
    /// mutate anything — every phase re-guards on the cancelled token.
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
            // A cancelled or faulted task is a completed drain; its
            // failure evidence already published through its own path.
        }
    }
}
