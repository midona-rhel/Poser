using Poser.Domain.Integration;
using Poser.Game.Mcdf;

namespace Poser.Game.Tests;

public sealed class McdfFileBoundaryTests
{
    [Fact]
    public void Missing_root_is_a_deterministic_boundary_failure()
    {
        using var files = new TempFiles();
        var result = files.Boundary.InspectExportCandidates(
            Path.Combine(files.Root, "missing"),
            new Dictionary<string, IReadOnlyList<string>>());

        Assert.False(result.Success);
        Assert.Equal("Penumbra's mod directory is missing or inaccessible.", result.Detail);
    }

    [Fact]
    public void Valid_local_candidate_is_canonical_readable_and_contained()
    {
        using var files = new TempFiles();
        string mod = Path.Combine(files.Root, "mod");
        string local = Path.Combine(mod, "body.mdl");
        Directory.CreateDirectory(mod);
        File.WriteAllText(local, "payload");

        var result = files.Boundary.InspectExportCandidates(
            mod,
            new Dictionary<string, IReadOnlyList<string>>
            {
                [local] = ["chara/human/c0101/obj/body/b0001/model.mdl"],
            });

        Assert.True(result.Success);
        var candidate = Assert.Single(result.Value!.Candidates);
        Assert.Equal(McdfExportCandidateKind.LocalFile, candidate.Kind);
        Assert.Equal(Path.GetFullPath(local), candidate.LocalPath);
        Assert.Equal(new FileInfo(local).Length, candidate.Length);
    }

    [Fact]
    public void Missing_candidate_is_skipped_without_throwing()
    {
        using var files = new TempFiles();
        string mod = Path.Combine(files.Root, "mod");
        string missing = Path.Combine(mod, "missing.mdl");
        Directory.CreateDirectory(mod);

        var result = files.Boundary.InspectExportCandidates(
            mod,
            new Dictionary<string, IReadOnlyList<string>> { [missing] = ["a/b.mdl"] });

        Assert.Empty(result.Value!.Candidates);
        Assert.Contains("missing on disk", Assert.Single(result.Value.Skipped));
    }

    [Fact]
    public void Reparse_escape_is_skipped()
    {
        using var files = new TempFiles();
        string mod = Path.Combine(files.Root, "mod");
        string outside = Path.Combine(files.Root, "outside");
        Directory.CreateDirectory(mod);
        Directory.CreateDirectory(outside);
        string outsideFile = Path.Combine(outside, "escape.mdl");
        File.WriteAllText(outsideFile, "escape");
        string link = Path.Combine(mod, "linked");
        try
        {
            Directory.CreateSymbolicLink(link, outside);
        }
        catch (UnauthorizedAccessException) { return; }
        catch (IOException) { return; }

        var result = files.Boundary.InspectExportCandidates(
            mod,
            new Dictionary<string, IReadOnlyList<string>>
            {
                [Path.Combine(link, "escape.mdl")] = ["a/b.mdl"],
            });

        Assert.Empty(result.Value!.Candidates);
        Assert.Contains("outside the Penumbra mod directory", Assert.Single(result.Value.Skipped));
    }

    [Fact]
    public void Duplicate_canonical_targets_are_observed_as_one_source()
    {
        using var files = new TempFiles();
        string mod = Path.Combine(files.Root, "mod");
        string real = Path.Combine(mod, "real");
        Directory.CreateDirectory(real);
        string local = Path.Combine(real, "body.mdl");
        File.WriteAllText(local, "payload");
        string alias = Path.Combine(mod, "alias");
        try
        {
            Directory.CreateSymbolicLink(alias, real);
        }
        catch (UnauthorizedAccessException) { return; }
        catch (IOException) { return; }

        var result = files.Boundary.InspectExportCandidates(
            mod,
            new Dictionary<string, IReadOnlyList<string>>
            {
                [local] = ["a/body.mdl"],
                [Path.Combine(alias, "body.mdl")] = ["a/body.mdl"],
            });

        var candidates = result.Value!.Candidates;
        Assert.Equal(2, candidates.Count);
        Assert.Equal(candidates[0].LocalPath, candidates[1].LocalPath);
    }

    [Fact]
    public void Operation_directories_are_unique_and_cleanup_is_retryable()
    {
        using var files = new TempFiles();
        var first = files.Boundary.CreateOperationDirectory();
        var second = files.Boundary.CreateOperationDirectory();
        Assert.True(first.Success && second.Success);
        Assert.NotEqual(first.Value, second.Value);
        Assert.True(Directory.Exists(first.Value));
        Assert.True(Directory.Exists(second.Value));

        Assert.True(files.Boundary.DeleteOperationDirectory(first.Value!).Success);
        Assert.True(files.Boundary.DeleteOperationDirectory(first.Value!).Success);
        Assert.True(files.Boundary.DeleteOperationDirectory(second.Value!).Success);
    }

    [Fact]
    public async Task Metadata_failure_leaves_existing_destination_unchanged()
    {
        using var files = new TempFiles();
        string destination = Path.Combine(files.Root, "export.mcdf");
        File.WriteAllText(destination, "old destination");
        string missing = Path.Combine(files.Root, "missing.mdl");
        var result = await files.Boundary.WritePackage(
            destination,
            new McdfExportContent("", "", "", "",
                [new McdfExportFile(["a/b.mdl"], missing)],
                new Dictionary<string, string>()),
            _ => { }, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("old destination", File.ReadAllText(destination));
        Assert.False(File.Exists(destination + ".tmp"));
    }

    [Fact]
    public async Task Unchanged_valid_export_round_trips_through_boundary()
    {
        using var files = new TempFiles();
        string mod = Path.Combine(files.Root, "mod");
        Directory.CreateDirectory(mod);
        string local = Path.Combine(mod, "body.mdl");
        File.WriteAllText(local, "payload");
        string destination = Path.Combine(files.Root, "export.mcdf");
        var inspection = files.Boundary.InspectExportCandidates(
            mod, new Dictionary<string, IReadOnlyList<string>>
            {
                [local] = ["a/body.mdl"],
            });
        var candidate = Assert.Single(inspection.Value!.Candidates);

        var written = await files.Boundary.WritePackage(
            destination,
            new McdfExportContent("desc", "glamourer", "", "",
                [new McdfExportFile(candidate.GamePaths, candidate.LocalPath!)],
                new Dictionary<string, string>()),
            _ => { }, CancellationToken.None);
        Assert.True(written.Success);

        var operation = files.Boundary.CreateOperationDirectory();
        var read = await files.Boundary.ReadPackage(
            destination, McdfLimits.Default, operation.Value!, _ => { }, CancellationToken.None);
        Assert.True(read.Success);
        Assert.Equal("desc", read.Value!.Description);
        Assert.Equal("glamourer", read.Value.GlamourerData);
        Assert.Single(read.Value.ReplacedGamePaths);
        Assert.True(files.Boundary.DeleteOperationDirectory(operation.Value!).Success);
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
