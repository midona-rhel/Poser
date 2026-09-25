using Poser.Domain.Operations;
using System.ComponentModel;
using System.Reflection;
using Microsoft.Win32.SafeHandles;
using Poser.Application.Integration;
using Poser.Domain.Identity;
using Poser.Domain.Integration;
using Poser.Documents.Mcdf;

namespace Poser.Game.Tests;

public sealed class McdfFileBoundaryTests
{
    [Theory]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public async Task Export_roundtrip_preserves_owned_or_saved_body_profile(bool owned, bool saved)
    {
        using var files = new TempFiles();
        var port = DispatchProxy.Create<IIntegrationRuntimePort, ExportRuntimeProxy>();
        var runtime = (ExportRuntimeProxy)(object)port;
        runtime.CallerThread = System.Environment.CurrentManagedThreadId;
        runtime.ModRoot = files.Root;
        runtime.CustomizeAvailable = true;
        var actor = ActorId.New();
        var session = new ActorIntegrationSession(port, files.Boundary, new ActiveSessionSource());
        const string retained = "{\"Bones\":{\"j_ude_a_l\":{\"Scale\":1.2}}}";
        if (owned)
            Assert.True(session.ApplyBodyProfileJson(actor, retained, "Copied profile").Success);
        runtime.SavedProfile = saved ? Guid.NewGuid() : null;
        var ownership = session.OverridesFor(actor);
        string path = Path.Combine(files.Root, "profile.mcdf");

        Assert.True(session.BeginExport(actor, path, "profile roundtrip").Success);
        await WaitUntilAsync(() => !session.McdfBusy);
        Assert.True(session.Mcdf?.Outcome?.Success, session.Mcdf?.Outcome?.Detail);
        var directory = files.Boundary.CreateOperationDirectory();
        Assert.True(directory.Success);
        try
        {
            var package = await files.Boundary.ReadPackage(path, McdfLimits.Default,
                directory.Value!, _ => { }, CancellationToken.None);
            Assert.True(package.Success, package.Detail);
            string decoded = System.Text.Encoding.UTF8.GetString(
                Convert.FromBase64String(package.Value!.CustomizePlusData));
            Assert.Equal(owned ? retained : saved ? runtime.BodyJson : string.Empty, decoded);
            Assert.Equal(ownership, session.OverridesFor(actor));
            Assert.Equal(owned ? 1 : 0, runtime.BodyWrites);
        }
        finally { files.Boundary.DeleteOperationDirectory(directory.Value!); }
    }

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
        public Poser.Domain.Operations.SessionGeneration? ActiveSessionGeneration { get; } =
            Poser.Domain.Operations.SessionGeneration.New();
    }

    private class ExportRuntimeProxy : DispatchProxy
    {
        public int CallerThread { get; set; }
        public List<int> VendorReadThreads { get; } = new();
        public string ModRoot { get; set; } = "mod-root";
        public bool CustomizeAvailable { get; set; }
        public Guid? SavedProfile { get; set; }
        public string BodyJson { get; set; } = "{\"Bones\":{\"j_kosi\":{\"Scale\":1.1}}}";
        public int BodyWrites { get; private set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            Assert.NotNull(targetMethod);
            string name = targetMethod.Name;
            if (name is "get_Penumbra" or "get_Glamourer")
                return new IntegrationAvailability(true, "available");
            if (name == "get_CustomizePlus")
                return new IntegrationAvailability(CustomizeAvailable, "available");
            VendorReadThreads.Add(System.Environment.CurrentManagedThreadId);
            Assert.Equal(CallerThread, System.Environment.CurrentManagedThreadId);
            if (name == nameof(IIntegrationRuntimePort.ApplyTemporaryBodyProfile))
            {
                BodyWrites++;
                return IntegrationValue<Guid>.Ok(Guid.NewGuid());
            }
            return name switch
            {
                nameof(IIntegrationRuntimePort.ProbeBodyProfile) =>
                    IntegrationValue<BodyProfileProbe>.Ok(new(SavedProfile, SavedProfile != null)),
                nameof(IIntegrationRuntimePort.GetBodyProfileJson) => IntegrationValue<string>.Ok(BodyJson),
                nameof(IIntegrationRuntimePort.CaptureGlamourerState) =>
                    IntegrationValue<string>.Ok("glamourer"),
                nameof(IIntegrationRuntimePort.GetActorMetaManipulations) =>
                    IntegrationValue<string>.Ok("manipulations"),
                nameof(IIntegrationRuntimePort.GetActorResourcePaths) =>
                    IntegrationValue<IReadOnlyDictionary<string, IReadOnlyList<string>>>.Ok(
                        new Dictionary<string, IReadOnlyList<string>>()),
                nameof(IIntegrationRuntimePort.GetModDirectory) =>
                    IntegrationValue<string>.Ok(ModRoot),
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
