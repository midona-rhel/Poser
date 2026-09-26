using Dalamud.Plugin.Services;
using Poser.Application.Posing;
using Poser.Config;
using Poser.Domain.Identity;
using Poser.Domain.Operations;
using Poser.Domain.Transforms;
using Poser.Files;
using Poser.Game.Bindings;

namespace Poser.Game.Posing;

internal sealed class PreparedPoseImport(PoseImportPlan plan) : IPreparedPoseImport
{
    public PoseImportPlan Plan { get; } = plan;
    public bool IsEmpty => Plan.IsEmpty;
    public int FileBoneCount => Plan.FileBoneCount;
}

/// <summary>Framework scheduling and the native in-pass engine behind the import workflow.</summary>
public sealed class PoseImportRuntime(
    StableBindingRegistry bindings, PoseImportCapture capture,
    ConfigurationService configuration, IFramework framework, IPluginLog log) : IPoseImportRuntime
{
    public bool IsPending => capture.IsPending;
    public bool IsFrameworkThread => framework.IsInFrameworkUpdateThread;
    public bool FreezeOnImport => configuration.Config.FreezeActorOnPoseImport;
    public bool IsCurrent(PoseImportOperation operation) => capture.IsCurrent(operation);
    public GestureResult CancelActive(string detail) => capture.CancelActive(detail);

    public GestureResult Reserve(ActorId id, string description, out PoseImportOperation? operation,
        Action<bool> onFinished, Action<OperationReceipt> onReceipt)
    {
        operation = null;
        if (bindings.Resolve(id) is not { Success: true, Value: { } actor })
            return GestureResult.Fail("The actor is no longer available.");
        return capture.Reserve(actor, description, out operation, onFinished, onReceipt);
    }

    public GestureResult Begin(PoseImportOperation operation, IPreparedPoseImport plan,
        bool expression, bool suppressHistory, string? asset) =>
        plan is PreparedPoseImport prepared
            ? capture.Begin(operation, prepared.Plan, expression, suppressHistory, asset)
            : GestureResult.Fail("The prepared import belongs to another runtime.");

    public void Schedule(Action action, int ticks) =>
        framework.RunOnTick(action, delayTicks: ticks);

    public void Report(string message, bool error = false)
    {
        if (error) log.Error(message);
        else log.Warning(message);
    }
}
