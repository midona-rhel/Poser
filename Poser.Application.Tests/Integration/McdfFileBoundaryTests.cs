using Poser.Domain.Integration;
using Poser.Documents.Mcdf;

namespace Poser.Application.Tests.Integration;

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
