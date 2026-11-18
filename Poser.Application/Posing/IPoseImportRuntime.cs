using Poser.Domain.Identity;
using Poser.Domain.Operations;
using Poser.Domain.Transforms;

namespace Poser.Application.Posing;

/// <summary>A pose file that has been read and is ready to apply. It finds bones by
/// name, so it holds on to nothing in game memory.</summary>
public interface IPreparedPoseImport
{
    bool IsEmpty { get; }
    int FileBoneCount { get; }
}

/// <summary>One import that was accepted. Work that runs later first checks that this is
/// still the current import.</summary>
public sealed class PoseImportOperation(OperationReceipt pending)
{
    public OperationReceipt Pending { get; } = pending;
}

/// <summary>The game side of a pose import. It holds the actor for the import, writes the
/// pose while the game updates the skeleton, and runs work on the game's main thread.</summary>
public interface IPoseImportRuntime
{
    bool IsPending { get; }
    /// <summary>True while another import or an IK bake is being applied. A new
    /// import has to wait until it is done.</summary>
    bool AdmissionBusy { get; }
    bool IsFrameworkThread { get; }
    bool FreezeOnImport { get; }
    bool IsCurrent(PoseImportOperation operation);
    GestureResult CancelActive(string detail);
    GestureResult Reserve(ActorId actor, string description, out PoseImportOperation? operation,
        Action<bool> onFinished, Action<OperationReceipt> onReceipt);
    GestureResult Begin(PoseImportOperation operation, IPreparedPoseImport plan,
        bool expression, bool suppressHistory);
    void Schedule(Action action, int ticks);
    void Report(string message, bool error = false);
}
