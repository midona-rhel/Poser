using Poser.Domain.Operations;
using System.ComponentModel;
using System.Reflection;
using Microsoft.Win32.SafeHandles;
using Poser.Application.Integration;
using Poser.Domain.Identity;
using Poser.Domain.Integration;
using Poser.Documents.Mcdf;

namespace Poser.Application.Tests.Integration;

public sealed class McdfFileBoundaryTests
{
    [Fact]
    public async Task History_copy_has_independent_payloads_and_refuses_a_changed_source()
    {
        var boundary = new McdfFileBoundary();
        var source = boundary.CreateOperationDirectory().Value!;
        var destination = boundary.CreateOperationDirectory().Value!;
        try
        {
            var payload = Path.Combine(source.Path, "p0000.dat");
            File.WriteAllBytes(payload, [1, 2, 3]);
            var package = new McdfPackage("source.mcdf", "saved", "", "", "",
                new Dictionary<string, string> { ["chara/test.mdl"] = payload },
                new Dictionary<string, string>(), source.Path, 1, 3);
            var copied = await boundary.CopyPackage(package, source, destination, TestContext.Current.CancellationToken);
            Assert.True(copied.Success, copied.Detail);
            var copyPath = copied.Value!.ReplacedGamePaths["chara/test.mdl"];
            Assert.NotEqual(payload, copyPath);

            File.AppendAllText(payload, "changed");
            Assert.False((await boundary.CopyPackage(package, source, destination,
                TestContext.Current.CancellationToken)).Success);

            Assert.True(boundary.DeleteOperationDirectory(source).Success);
            Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(copyPath));
        }
        finally
        {
            boundary.DeleteOperationDirectory(source);
            boundary.DeleteOperationDirectory(destination);
        }
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task Export_roundtrip_preserves_owned_or_saved_body_profile(bool owned, bool saved)
    {
        using var files = new TempFiles();
        var port = DispatchProxy.Create<IIntegrationRuntimePort, ExportRuntimeProxy>();
        var runtime = (ExportRuntimeProxy)(object)port;
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

    [Theory]
    [InlineData("A9993E364706816ABA3E25717850C26C9CD0D89D", true)]
    [InlineData("6437b3ac38465133ffb63b75273a8db548c558465d79db03fd359c6cd5bd9d84", false)]
    public async Task Reads_legacy_and_current_payload_hashes_without_accepting_corruption(string hash, bool valid)
    {
        using var files = new TempFiles();
        var path = Path.Combine(files.Root, "external.mcdf");
        using (var stream = File.Create(path))
        using (var compressed = K4os.Compression.LZ4.Legacy.LZ4Legacy.Encode(stream))
        using (var writer = new BinaryWriter(compressed))
        {
            var header = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new
            {
                Files = new[] { new { GamePaths = new[] { "chara/test.mdl" }, Length = 3, Hash = hash } },
            });
            writer.Write("MCDF"u8);
            writer.Write((byte)1);
            writer.Write(header.Length);
            writer.Write(header);
            writer.Write("abc"u8);
        }
        var directory = files.Boundary.CreateOperationDirectory().Value!;
        try
        {
            var read = await files.Boundary.ReadPackage(path, McdfLimits.Default, directory, _ => { }, CancellationToken.None);
            Assert.Equal(valid, read.Success);
            if (valid) Assert.Equal("abc", File.ReadAllText(read.Value!.ReplacedGamePaths["chara/test.mdl"]));
        }
        finally { files.Boundary.DeleteOperationDirectory(directory); }
    }

    [Fact]
    public async Task Export_uses_current_wire_hash_and_deduplicates_payloads()
    {
        using var files = new TempFiles();
        var payload = Path.Combine(files.Root, "test.mdl");
        var path = Path.Combine(files.Root, "export.mcdf");
        File.WriteAllText(payload, "abc");
        var written = await files.Boundary.WritePackage(path,
            new McdfExportContent("test", "glamourer", "profile", "meta",
                [new(["chara/one.mdl"], payload), new(["chara/two.mdl"], payload)], new Dictionary<string, string>()),
            _ => { }, CancellationToken.None);
        Assert.True(written.Success, written.Detail);
        using var stream = File.OpenRead(path);
        using var compressed = K4os.Compression.LZ4.Legacy.LZ4Legacy.Decode(stream);
        using var reader = new BinaryReader(compressed);
        Assert.Equal("MCDF", new string(reader.ReadChars(4)));
        Assert.Equal(1, reader.ReadByte());
        using var header = System.Text.Json.JsonDocument.Parse(reader.ReadBytes(reader.ReadInt32()));
        var entry = Assert.Single(header.RootElement.GetProperty("Files").EnumerateArray());
        Assert.Equal("6437B3AC38465133FFB63B75273A8DB548C558465D79DB03FD359C6CD5BD9D85", entry.GetProperty("Hash").GetString());
        Assert.Equal(2, entry.GetProperty("GamePaths").GetArrayLength());
        Assert.Equal("profile", header.RootElement.GetProperty("CustomizePlusData").GetString());
        Assert.Equal("abc"u8.ToArray(), reader.ReadBytes(3));
        Assert.Equal(-1, compressed.ReadByte());
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
