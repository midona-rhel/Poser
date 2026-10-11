using System.Reflection;
using Poser.Application.Integration;
using Poser.Application.Lifecycle;
using Poser.Documents.Mcdf;
using Poser.Domain.Identity;
using Poser.Domain.Integration;
using Poser.Domain.Operations;

namespace Poser.Application.Tests.Integration;

public sealed class McdfHistoryTests
{
    [Fact]
    public async Task Reset_and_restore_use_retained_resources_without_rereading_the_original_file()
    {
        var sessions = new Sessions();
        var files = new Files();
        using var integration = new IntegrationGraph(
            DispatchProxy.Create<IIntegrationRuntimeFake, Runtime>(), files, sessions);
        var actor = ActorId.New();
        Assert.True(integration.Mcdf.BeginImport(actor, "original.mcdf").Success);
        await integration.Mcdf.CurrentCompletion;
        Assert.True(integration.Mcdf.Progress!.Outcome!.Success, integration.Mcdf.Progress.Outcome.Detail);
        var captured = integration.Selectors.TryCaptureHistory(actor);
        Assert.True(captured.Success, captured.Detail);
        Assert.NotNull(captured.Value!.McdfResources);

        Assert.True(integration.Reset.ResetActor(actor).Success);
        await integration.Mcdf.CurrentCompletion;
        Assert.Empty(files.Deleted);
        files.OriginalAvailable = false;
        var restored = await integration.Selectors.RestoreHistoryAndWait(actor, captured.Value, () => true, CancellationToken.None);
        Assert.True(restored.Success, restored.Detail);
        Assert.Equal(1, files.Reads);
        Assert.Equal(1, files.Copies);

        Assert.True(integration.Reset.ResetAll().Success);
        Assert.Equal(2, files.Deleted.Count);
        Assert.False(integration.Selectors.RestoreHistory(actor, captured.Value).Success);
    }

    [Fact]
    public async Task Copy_failure_keeps_retained_resources_and_does_not_apply_a_partial_package()
    {
        var files = new Files();
        using var integration = new IntegrationGraph(
            DispatchProxy.Create<IIntegrationRuntimeFake, Runtime>(), files, new Sessions());
        var actor = ActorId.New();
        Assert.True(integration.Mcdf.BeginImport(actor, "original.mcdf").Success);
        await integration.Mcdf.CurrentCompletion;
        var captured = integration.Selectors.TryCaptureHistory(actor).Value!;
        Assert.True(integration.Reset.ResetActor(actor).Success);
        files.FailCopy = true;
        var failed = await integration.Selectors.RestoreHistoryAndWait(actor, captured, () => true, CancellationToken.None);
        Assert.False(failed.Success);
        Assert.Null(integration.Ownership.OverridesFor(actor).Mcdf);
        Assert.DoesNotContain("directory-1", files.Deleted);
        files.FailCopy = false;
        var retried = await integration.Selectors.RestoreHistoryAndWait(actor, captured, () => true, CancellationToken.None);
        Assert.True(retried.Success, retried.Detail);
    }

    [Fact]
    public async Task Import_held_past_its_deadline_is_cancelled_and_its_late_completion_changes_nothing()
    {
        var staged = Path.Combine(Path.GetTempPath(), $"poser-test-{Guid.NewGuid():N}.mcdf");
        File.WriteAllText(staged, "package");
        try
        {
            var files = new Files { Hold = new(), InputExists = File.Exists };
            using var integration = new IntegrationGraph(
                DispatchProxy.Create<IIntegrationRuntimeFake, Runtime>(), files, new Sessions());
            var actor = ActorId.New();
            Assert.True(integration.Mcdf.BeginImport(actor, staged).Success);
            var id = integration.Mcdf.Receipt!.OperationId;

            var waited = await integration.Mcdf.AwaitOperation(
                id, TimeSpan.FromMilliseconds(100), CancellationToken.None, TimeSpan.FromMilliseconds(100));

            // Still reading: the parent stopped, the child is cancelled but not
            // terminal, so the scene adapter must keep the staged input.
            Assert.Equal(McdfWaitEnd.DeadlinePassed, waited.End);
            Assert.False(waited.Terminal);
            Assert.False(waited.Applied);
            Assert.True(File.Exists(staged));

            files.Hold.SetResult();
            await integration.Mcdf.CurrentCompletion;
            Assert.True(Assert.Single(files.InputPresentAtRead));
            Assert.Equal(OperationReceiptState.Cancelled, integration.Mcdf.Receipt!.State);
            Assert.Null(integration.Ownership.OverridesFor(actor).Mcdf);
        }
        finally
        {
            File.Delete(staged);
        }
    }

    [Fact]
    public async Task Late_cancel_of_a_finished_operation_never_cancels_the_newer_one()
    {
        var files = new Files();
        using var integration = new IntegrationGraph(
            DispatchProxy.Create<IIntegrationRuntimeFake, Runtime>(), files, new Sessions());
        var first = ActorId.New();
        Assert.True(integration.Mcdf.BeginImport(first, "first.mcdf").Success);
        var old = integration.Mcdf.Receipt!.OperationId;
        await integration.Mcdf.CurrentCompletion;

        files.Hold = new();
        var second = ActorId.New();
        Assert.True(integration.Mcdf.BeginImport(second, "second.mcdf").Success);
        var late = await integration.Mcdf.CancelAndDrain(old, TimeSpan.FromMilliseconds(50));
        Assert.True(late.Terminal);
        Assert.Null(late.Receipt);

        files.Hold.SetResult();
        await integration.Mcdf.CurrentCompletion;
        Assert.Equal(OperationReceiptState.Applied, integration.Mcdf.Receipt!.State);
        Assert.NotNull(integration.Ownership.OverridesFor(second).Mcdf);
    }

    [Fact]
    public async Task Unload_returns_a_cancelled_parent_without_waiting_on_the_blocked_framework_thread()
    {
        var files = new Files { Hold = new() };
        var port = DispatchProxy.Create<IIntegrationRuntimeFake, Runtime>();
        var runtime = (Runtime)(object)port;
        using var integration = new IntegrationGraph(port, files, new Sessions());
        var actor = ActorId.New();
        Assert.True(integration.Mcdf.BeginImport(actor, "held.mcdf").Success);
        var id = integration.Mcdf.Receipt!.OperationId;
        await files.ReadEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Dispose holds the framework thread: no hop queued from here runs.
        runtime.FrameworkBlocked = true;
        using var parent = new CancellationTokenSource();
        var wait = integration.Mcdf.AwaitOperation(id, TimeSpan.FromMinutes(1), parent.Token);
        parent.Cancel();
        integration.Mcdf.AbandonWaits();

        var waited = await wait.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(McdfWaitEnd.ParentCancelled, waited.End);
        Assert.False(waited.Terminal);
        var settled = integration.Mcdf.Settled(id);
        Assert.False(settled.IsCompleted);

        // The child was cancelled directly, so once it can run it stops.
        runtime.FrameworkBlocked = false;
        files.Hold.SetResult();
        await settled.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(OperationReceiptState.Cancelled, integration.Mcdf.Receipt!.State);
        Assert.Null(integration.Ownership.OverridesFor(actor).Mcdf);
    }

    private sealed class Sessions : ISessionGenerationSource
    {
        public SessionGeneration? ActiveSessionGeneration { get; } = SessionGeneration.New();
    }

    public class Runtime : DispatchProxy
    {
        public bool Resources, OwnedDuplicate;
        /// <summary>A framework thread blocked in Dispose: hops never run.</summary>
        public volatile bool FrameworkBlocked;
        public int Assignments;
        private readonly Guid _collection = Guid.NewGuid();
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            if (method!.Name == nameof(IIntegrationRuntimeFake.OnFrameworkThread))
            {
                if (FrameworkBlocked)
                {
                    var never = Activator.CreateInstance(typeof(TaskCompletionSource<>)
                        .MakeGenericType(method.GetGenericArguments()[0]))!;
                    return never.GetType().GetProperty(nameof(TaskCompletionSource<int>.Task))!
                        .GetValue(never);
                }
                var value = ((Delegate)args![0]!).DynamicInvoke();
                return typeof(Task).GetMethod(nameof(Task.FromResult))!
                    .MakeGenericMethod(method.GetGenericArguments()[0]).Invoke(null, [value]);
            }
            if (method.Name == nameof(IIntegrationRuntimeFake.AssignTemporaryCollection))
            {
                Assignments++;
                return IntegrationResult.Ok();
            }
            if (method.Name == nameof(IIntegrationRuntimeFake.RedrawAndWait))
                return Task.FromResult(IntegrationResult.Ok());
            return method.Name switch
            {
                "get_Penumbra" => new IntegrationAvailability(Resources, "Unavailable"),
                "get_Glamourer" or "get_CustomizePlus" => new IntegrationAvailability(false, "Unavailable"),
                nameof(IIntegrationRuntimeFake.GetCollectionAssignment) =>
                    IntegrationValue<CollectionAssignment>.Ok(new(_collection, "Temporary", false)),
                nameof(IIntegrationRuntimeFake.GetCollections) =>
                    IntegrationValue<IReadOnlyList<ExternalItem>>.Ok([]),
                nameof(IIntegrationRuntimeFake.CaptureInheritedCollection) =>
                    IntegrationValue<SpawnCollectionSnapshot?>.Ok(OwnedDuplicate
                        ? new(new Dictionary<string, string> { ["model"] = "mod/model" }, "") : null),
                nameof(IIntegrationRuntimeFake.CreateTemporaryCollection) => IntegrationValue<Guid>.Ok(Guid.NewGuid()),
                nameof(IIntegrationRuntimeFake.AddTemporaryMods) =>
                    IntegrationResult.Ok(),
                nameof(IIntegrationRuntimeFake.DeleteTemporaryCollection) => IntegrationResult.Ok(),
                nameof(IIntegrationRuntimeFake.IsResolvable) => true,
                nameof(IIntegrationRuntimeFake.GetActorName) => IntegrationValue<string>.Ok("Test actor"),
                _ => throw new NotSupportedException(method.Name),
            };
        }
    }

    private sealed class Files : IMcdfFileBoundary
    {
        public int Reads, Copies;
        public bool OriginalAvailable = true, FailCopy, Resources;
        private int _directories;
        public HashSet<string> Deleted { get; } = [];
        public string GetFileName(string path) => path;
        public IntegrationValue<McdfOperationDirectory> CreateOperationDirectory() =>
            IntegrationValue<McdfOperationDirectory>.Ok(new($"directory-{++_directories}", "owner", null, null));
        /// <summary>Holds the read until released, ignoring cancellation, as a
        /// slow package read does; records whether the staged input existed.</summary>
        public TaskCompletionSource? Hold;
        public Func<string, bool>? InputExists;
        public List<bool> InputPresentAtRead { get; } = [];
        public TaskCompletionSource ReadEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<IntegrationValue<McdfPackage>> ReadPackage(string path, McdfLimits limits,
            McdfOperationDirectory directory, Action<McdfProgressStep> progress, CancellationToken cancellation)
        {
            Reads++;
            ReadEntered.TrySetResult();
            if (Hold is { } hold)
                await hold.Task;
            if (InputExists is { } exists)
                InputPresentAtRead.Add(exists(path));
            return OriginalAvailable
                ? IntegrationValue<McdfPackage>.Ok(new(path, "", "", "", "", Resources ? new Dictionary<string,string> { ["model"] = "mod/model" } : new Dictionary<string,string>(),
                    new Dictionary<string,string>(), directory.Path, 0, 0))
                : IntegrationValue<McdfPackage>.Fail("Original file removed");
        }
        public Task<IntegrationValue<McdfPackage>> CopyPackage(McdfPackage package, McdfOperationDirectory source,
            McdfOperationDirectory destination, CancellationToken cancellation)
        {
            Copies++;
            Assert.DoesNotContain(source.Path, Deleted);
            return Task.FromResult(FailCopy ? IntegrationValue<McdfPackage>.Fail("Copy refused")
                : IntegrationValue<McdfPackage>.Ok(package with { OperationDirectory = destination.Path }));
        }
        public IntegrationResult DeleteOperationDirectory(McdfOperationDirectory directory)
        { Deleted.Add(directory.Path); return IntegrationResult.Ok(); }
        public IntegrationValue<McdfSummary> ReadSummary(string path) => throw new NotSupportedException();
        public IntegrationValue<McdfExportInspection> InspectExportCandidates(string root,
            IReadOnlyDictionary<string, IReadOnlyList<string>> resources, CancellationToken cancellation) =>
            throw new NotSupportedException();
        public Task<IntegrationValue<McdfWriteStats>> WritePackage(string destination, McdfExportContent content,
            Action<McdfProgressStep> progress, CancellationToken cancellation) => throw new NotSupportedException();
    }
}
