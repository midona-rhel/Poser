using Poser.Domain.Identity;
using Poser.Domain.Operations;
using Poser.Domain.Transforms;

namespace Poser.Application.Posing;

/// <summary>A prepared native import, addressed by bone names rather than live wrappers.</summary>
public interface IPreparedPoseImport
{
    bool IsEmpty { get; }
    int FileBoneCount { get; }
}

/// <summary>Exact admitted request; delayed work must still own this token.</summary>
public sealed class PoseImportOperation(OperationReceipt pending)
{
    public OperationReceipt Pending { get; } = pending;
}

/// <summary>Game-side reservation, in-pass application and framework scheduling.</summary>
public interface IPoseImportRuntime
{
    bool IsPending { get; }
    bool IsFrameworkThread { get; }
    bool FreezeOnImport { get; }
    bool IsCurrent(PoseImportOperation operation);
    GestureResult CancelActive(string detail);
    GestureResult Reserve(ActorId actor, string description, out PoseImportOperation? operation,
        Action<bool> onFinished, Action<OperationReceipt> onReceipt);
    GestureResult Begin(PoseImportOperation operation, IPreparedPoseImport plan,
        bool expression, bool suppressHistory, string? asset);
    void Schedule(Action action, int ticks);
    void Report(string message, bool error = false);
}
