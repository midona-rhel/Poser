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
        using var integration = new ActorIntegrationSession(
            DispatchProxy.Create<IIntegrationRuntimePort, Runtime>(), files, sessions);
        var actor = ActorId.New();
        Assert.True(integration.BeginImport(actor, "original.mcdf").Success);
        await integration.PendingCompletion;
        Assert.True(integration.Mcdf!.Outcome!.Success, integration.Mcdf.Outcome.Detail);
        var captured = integration.TryCaptureHistory(actor);
        Assert.True(captured.Success, captured.Detail);
        Assert.NotNull(captured.Value!.McdfResources);

        Assert.True(integration.ResetActor(actor).Success);
        await integration.PendingCompletion;
        Assert.Empty(files.Deleted);
        files.OriginalAvailable = false;
        var restored = await integration.RestoreHistoryAndWait(actor, captured.Value, () => true, CancellationToken.None);
        Assert.True(restored.Success, restored.Detail);
        Assert.Equal(1, files.Reads);
        Assert.Equal(1, files.Copies);

        Assert.True(integration.ResetAll().Success);
        Assert.Equal(2, files.Deleted.Count);
        Assert.False(integration.RestoreHistory(actor, captured.Value).Success);
    }

    [Fact]
    public async Task A_new_session_cannot_reuse_old_history_resources()
    {
        var sessions = new Sessions();
        var files = new Files();
        using var integration = new ActorIntegrationSession(
            DispatchProxy.Create<IIntegrationRuntimePort, Runtime>(), files, sessions);
        var actor = ActorId.New();
        Assert.True(integration.BeginImport(actor, "original.mcdf").Success);
        await integration.PendingCompletion;
        var captured = integration.TryCaptureHistory(actor).Value!;
        sessions.ActiveSessionGeneration = SessionGeneration.New();
        Assert.False(integration.RestoreHistory(actor, captured).Success);
        Assert.Equal(0, files.Copies);
    }

    [Fact]
    public async Task Copy_failure_keeps_retained_resources_and_does_not_apply_a_partial_package()
    {
        var files = new Files();
        using var integration = new ActorIntegrationSession(
            DispatchProxy.Create<IIntegrationRuntimePort, Runtime>(), files, new Sessions());
        var actor = ActorId.New();
        Assert.True(integration.BeginImport(actor, "original.mcdf").Success);
        await integration.PendingCompletion;
        var captured = integration.TryCaptureHistory(actor).Value!;
        Assert.True(integration.ResetActor(actor).Success);
        files.FailCopy = true;
        var failed = await integration.RestoreHistoryAndWait(actor, captured, () => true, CancellationToken.None);
        Assert.False(failed.Success);
        Assert.Null(integration.OverridesFor(actor).Mcdf);
        Assert.DoesNotContain("directory-1", files.Deleted);
        files.FailCopy = false;
        var retried = await integration.RestoreHistoryAndWait(actor, captured, () => true, CancellationToken.None);
        Assert.True(retried.Success, retried.Detail);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Import_replaces_a_proven_duplicate_collection_but_not_a_foreign_temporary(bool owned)
    {
        var port = DispatchProxy.Create<IIntegrationRuntimePort, Runtime>();
        var runtime = (Runtime)(object)port;
        runtime.Resources = true;
        runtime.OwnedDuplicate = owned;
        using var integration = new ActorIntegrationSession(port, new Files { Resources = true }, new Sessions());
        Assert.True(integration.BeginImport(ActorId.New(), "body.mcdf").Success);
        await integration.PendingCompletion;
        Assert.Equal(owned, integration.Mcdf!.Outcome!.Success);
        Assert.Equal(owned ? 1 : 0, runtime.Assignments);
        if (!owned) Assert.Contains("another plugin", integration.Mcdf.Outcome.Detail);
    }

    private sealed class Sessions : ISessionGenerationSource
    {
        public SessionGeneration? ActiveSessionGeneration { get; set; } = SessionGeneration.New();
    }

    public class Runtime : DispatchProxy
    {
        public bool Resources, OwnedDuplicate;
        public int Assignments;
        private readonly Guid _collection = Guid.NewGuid();
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            if (method!.Name == nameof(IIntegrationRuntimePort.OnFrameworkThread))
            {
                var value = ((Delegate)args![0]!).DynamicInvoke();
                return typeof(Task).GetMethod(nameof(Task.FromResult))!
                    .MakeGenericMethod(method.GetGenericArguments()[0]).Invoke(null, [value]);
            }
            if (method.Name == nameof(IIntegrationRuntimePort.AssignTemporaryCollection))
            {
                Assignments++;
                return IntegrationPortResult.Ok();
            }
            return method.Name switch
            {
                "get_Penumbra" => new IntegrationAvailability(Resources, "Unavailable"),
                "get_Glamourer" or "get_CustomizePlus" => new IntegrationAvailability(false, "Unavailable"),
                nameof(IIntegrationRuntimePort.GetCollectionAssignment) =>
                    IntegrationValue<CollectionAssignment>.Ok(new(_collection, "Temporary", false)),
                nameof(IIntegrationRuntimePort.GetCollections) =>
                    IntegrationValue<IReadOnlyList<ExternalItem>>.Ok([]),
                nameof(IIntegrationRuntimePort.CaptureInheritedCollection) =>
                    IntegrationValue<SpawnCollectionSnapshot?>.Ok(OwnedDuplicate
                        ? new(new Dictionary<string, string> { ["model"] = "mod/model" }, "") : null),
                nameof(IIntegrationRuntimePort.CreateTemporaryCollection) => IntegrationValue<Guid>.Ok(Guid.NewGuid()),
                nameof(IIntegrationRuntimePort.AddTemporaryMods) or nameof(IIntegrationRuntimePort.DeleteTemporaryCollection) =>
                    IntegrationPortResult.Ok(),
                nameof(IIntegrationRuntimePort.RedrawAndWait) => Task.FromResult(IntegrationPortResult.Ok()),
                nameof(IIntegrationRuntimePort.IsResolvable) => true,
                nameof(IIntegrationRuntimePort.GetActorName) => IntegrationValue<string>.Ok("Test actor"),
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
        public Task<IntegrationValue<McdfPackage>> ReadPackage(string path, McdfLimits limits,
            McdfOperationDirectory directory, Action<McdfProgressStep> progress, CancellationToken cancellation)
        {
            Reads++;
            return Task.FromResult(OriginalAvailable
                ? IntegrationValue<McdfPackage>.Ok(new(path, "", "", "", "", Resources ? new Dictionary<string,string> { ["model"] = "mod/model" } : new Dictionary<string,string>(),
                    new Dictionary<string,string>(), directory.Path, 0, 0))
                : IntegrationValue<McdfPackage>.Fail("Original file removed"));
        }
        public Task<IntegrationValue<McdfPackage>> CopyPackage(McdfPackage package, McdfOperationDirectory source,
            McdfOperationDirectory destination, CancellationToken cancellation)
        {
            Copies++;
            Assert.DoesNotContain(source.Path, Deleted);
            return Task.FromResult(FailCopy ? IntegrationValue<McdfPackage>.Fail("Copy refused")
                : IntegrationValue<McdfPackage>.Ok(package with { OperationDirectory = destination.Path }));
        }
        public IntegrationPortResult DeleteOperationDirectory(McdfOperationDirectory directory)
        { Deleted.Add(directory.Path); return IntegrationPortResult.Ok(); }
        public IntegrationValue<McdfSummary> ReadSummary(string path) => throw new NotSupportedException();
        public IntegrationValue<McdfExportInspection> InspectExportCandidates(string root,
            IReadOnlyDictionary<string, IReadOnlyList<string>> resources, CancellationToken cancellation) =>
            throw new NotSupportedException();
        public Task<IntegrationValue<McdfWriteStats>> WritePackage(string destination, McdfExportContent content,
            Action<McdfProgressStep> progress, CancellationToken cancellation) => throw new NotSupportedException();
    }
}
