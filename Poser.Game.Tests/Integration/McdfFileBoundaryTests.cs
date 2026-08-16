using System.ComponentModel;
using System.Reflection;
using Microsoft.Win32.SafeHandles;
using Poser.Application.Integration;
using Poser.Domain.Identity;
using Poser.Domain.Integration;
using Poser.Game.Mcdf;

namespace Poser.Game.Tests;

public sealed class McdfFileBoundaryTests
{
[Fact]
    public async Task Mcdf_boundary_rejects_invalid_roots_and_preserves_destination_on_source_change()
    {
        using var files = new TempFiles();
        Assert.False(files.Boundary.InspectExportCandidates(Path.Combine(files.Root, "missing"), new Dictionary<string, IReadOnlyList<string>>(), CancellationToken.None).Success);

        string mod = Path.Combine(files.Root, "mod");
        string source = Path.Combine(mod, "body.mdl");
        string destination = Path.Combine(files.Root, "export.mcdf");
        Directory.CreateDirectory(mod);
        File.WriteAllText(source, "payload");
        File.WriteAllText(destination, "old");

        var inspection = files.Boundary.InspectExportCandidates(mod, new Dictionary<string, IReadOnlyList<string>> { [source] = ["a/body.mdl"] }, CancellationToken.None);
        var candidate = Assert.Single(inspection.Value!.Candidates);
        Assert.NotEmpty(candidate.Source!.ContentHash);
        File.WriteAllText(source, "changed");

        var result = await files.Boundary.WritePackage(destination, new McdfExportContent("", "", "", "", [new McdfExportFile(candidate.GamePaths, candidate.LocalPath!, candidate.Source)], new Dictionary<string, string>()), _ => { }, CancellationToken.None);
        Assert.False(result.Success);
        Assert.Equal("old", File.ReadAllText(destination));
    }
private const int ChunkSizeForTest = 81920;

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!predicate())
            await Task.Delay(10, timeout.Token);
    }

    private sealed class ActiveSessionSource
        : Poser.Application.Lifecycle.ISessionGenerationSource
    {
        public Poser.Application.Operations.SessionGeneration? ActiveSessionGeneration { get; } =
            Poser.Application.Operations.SessionGeneration.New();
    }

    private class ExportRuntimeProxy : DispatchProxy
    {
        public int CallerThread { get; set; }
        public List<int> VendorReadThreads { get; } = new();

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            Assert.NotNull(targetMethod);
            string name = targetMethod.Name;
            if (name is "get_Penumbra" or "get_Glamourer")
                return new IntegrationAvailability(true, "available");
            if (name == "get_CustomizePlus")
                return new IntegrationAvailability(false, "unavailable");
            VendorReadThreads.Add(System.Environment.CurrentManagedThreadId);
            Assert.Equal(CallerThread, System.Environment.CurrentManagedThreadId);
            return name switch
            {
                nameof(IIntegrationRuntimePort.CaptureGlamourerState) =>
                    IntegrationValue<string>.Ok("glamourer"),
                nameof(IIntegrationRuntimePort.GetActorMetaManipulations) =>
                    IntegrationValue<string>.Ok("manipulations"),
                nameof(IIntegrationRuntimePort.GetActorResourcePaths) =>
                    IntegrationValue<IReadOnlyDictionary<string, IReadOnlyList<string>>>.Ok(
                        new Dictionary<string, IReadOnlyList<string>>()),
                nameof(IIntegrationRuntimePort.GetModDirectory) =>
                    IntegrationValue<string>.Ok("mod-root"),
                _ => throw new NotSupportedException(name),
            };
        }
    }

    private sealed class ExportBoundaryFake : IMcdfFileBoundary
    {
        public ManualResetEventSlim AllowInspection { get; } = new(false);
        public ManualResetEventSlim InspectionEntered { get; } = new(false);
        public int InspectionThread { get; private set; }
        public CancellationToken InspectionCancellation { get; private set; }

        public string GetFileName(string path) => Path.GetFileName(path);
        public IntegrationValue<McdfSummary> ReadSummary(string path) =>
            throw new NotSupportedException();
        public IntegrationValue<McdfOperationDirectory> CreateOperationDirectory() =>
            throw new NotSupportedException();
        public IntegrationValue<McdfExportInspection> InspectExportCandidates(
            string modRoot,
            IReadOnlyDictionary<string, IReadOnlyList<string>> resources,
            CancellationToken cancellation)
        {
            // The release gate is a harness rendezvous, NOT product
            // behaviour, so it deliberately does not observe the operation's
            // token: the test cancels BEFORE it opens the gate, and a
            // cancellable rendezvous let Cancel and Set race to wake this
            // waiter — whenever the cancel won, the call aborted here and
            // never recorded that inspection had run off-thread at all.
            // Cancellation is still observed exactly where the real boundary
            // observes it: below, once the off-thread entry is recorded.
            AllowInspection.Wait(TimeSpan.FromSeconds(5));
            InspectionThread = System.Environment.CurrentManagedThreadId;
            InspectionCancellation = cancellation;
            InspectionEntered.Set();
            cancellation.ThrowIfCancellationRequested();
            return IntegrationValue<McdfExportInspection>.Ok(
                new McdfExportInspection([], []));
        }
        public Task<IntegrationValue<McdfPackage>> ReadPackage(
            string path, McdfLimits limits, McdfOperationDirectory operationDirectory,
            Action<McdfProgressStep> progress, CancellationToken cancellation) =>
            throw new NotSupportedException();
        public Task<IntegrationValue<McdfWriteStats>> WritePackage(
            string destination, McdfExportContent content,
            Action<McdfProgressStep> progress, CancellationToken cancellation) =>
            Task.FromResult(IntegrationValue<McdfWriteStats>.Ok(
                new McdfWriteStats(0, 0)));
        public IntegrationPortResult DeleteOperationDirectory(
            McdfOperationDirectory operationDirectory) =>
            throw new NotSupportedException();
    }

    private sealed class TempFiles : IDisposable
    {
        public string Root { get; } = Path.Combine(
            Path.GetTempPath(), "poser-mcdf-tests", Guid.NewGuid().ToString("N"));
        public McdfFileBoundary Boundary { get; } = new();

        public TempFiles() => Directory.CreateDirectory(Root);

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Root))
                    Directory.Delete(Root, recursive: true);
            }
            catch { }
        }
    }
}
