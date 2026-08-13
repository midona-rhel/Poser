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
            new Dictionary<string, IReadOnlyList<string>>(),
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("Penumbra's mod directory is missing or inaccessible.", result.Detail);
    }

    [Fact]
    public void File_used_as_root_is_an_inaccessible_boundary_failure()
    {
        using var files = new TempFiles();
        string rootFile = Path.Combine(files.Root, "not-a-directory");
        File.WriteAllText(rootFile, "file");

        var result = files.Boundary.InspectExportCandidates(
            rootFile,
            new Dictionary<string, IReadOnlyList<string>>(),
            CancellationToken.None);

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
            },
            CancellationToken.None);

        Assert.True(result.Success);
        var candidate = Assert.Single(result.Value!.Candidates);
        Assert.Equal(McdfExportCandidateKind.LocalFile, candidate.Kind);
        Assert.Equal(Path.GetFullPath(local), candidate.LocalPath);
        Assert.Equal(new FileInfo(local).Length, candidate.Length);
        Assert.NotNull(candidate.Source);
        Assert.NotEmpty(candidate.Source!.ContentHash);
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
            new Dictionary<string, IReadOnlyList<string>> { [missing] = ["a/b.mdl"] },
            CancellationToken.None);

        Assert.Empty(result.Value!.Candidates);
        Assert.Contains("missing on disk", Assert.Single(result.Value.Skipped));
    }

    [Fact]
    public void Directory_candidate_is_skipped_as_unreadable_file()
    {
        using var files = new TempFiles();
        string mod = Path.Combine(files.Root, "mod");
        string directory = Path.Combine(mod, "not-a-file");
        Directory.CreateDirectory(directory);

        var result = files.Boundary.InspectExportCandidates(
            mod,
            new Dictionary<string, IReadOnlyList<string>> { [directory] = ["a/b.mdl"] },
            CancellationToken.None);

        Assert.Empty(result.Value!.Candidates);
        string skipped = Assert.Single(result.Value.Skipped);
        Assert.True(
            skipped.Contains("metadata", StringComparison.Ordinal)
            || skipped.Contains("readable", StringComparison.Ordinal));
    }

    [Fact]
    public void Reparse_escape_is_skipped_with_explicit_capability_skip()
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
        catch (UnauthorizedAccessException ex) { Assert.Skip($"Symlink capability unavailable: {ex.Message}"); }
        catch (IOException ex) { Assert.Skip($"Symlink capability unavailable: {ex.Message}"); }

        var result = files.Boundary.InspectExportCandidates(
            mod,
            new Dictionary<string, IReadOnlyList<string>>
            {
                [Path.Combine(link, "escape.mdl")] = ["a/b.mdl"],
            },
            CancellationToken.None);

        Assert.Empty(result.Value!.Candidates);
        Assert.Contains("outside the Penumbra mod directory", Assert.Single(result.Value.Skipped));
    }

    [Fact]
    public void Duplicate_canonical_targets_are_observed_as_one_source_with_explicit_capability_skip()
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
        catch (UnauthorizedAccessException ex) { Assert.Skip($"Symlink capability unavailable: {ex.Message}"); }
        catch (IOException ex) { Assert.Skip($"Symlink capability unavailable: {ex.Message}"); }

        var result = files.Boundary.InspectExportCandidates(
            mod,
            new Dictionary<string, IReadOnlyList<string>>
            {
                [local] = ["a/body.mdl"],
                [Path.Combine(alias, "body.mdl")] = ["a/body.mdl"],
            },
            CancellationToken.None);

        var candidates = result.Value!.Candidates;
        Assert.Equal(2, candidates.Count);
        Assert.Equal(candidates[0].LocalPath, candidates[1].LocalPath);
    }

    [Fact]
    public async Task Intermediate_reparse_swap_fails_before_destination_commit()
    {
        using var files = new TempFiles();
        string mod = Path.Combine(files.Root, "mod");
        string original = Path.Combine(files.Root, "original");
        string outside = Path.Combine(files.Root, "outside");
        Directory.CreateDirectory(mod);
        Directory.CreateDirectory(original);
        Directory.CreateDirectory(outside);
        string source = Path.Combine(original, "body.mdl");
        string escaped = Path.Combine(outside, "body.mdl");
        File.WriteAllText(source, "inside");
        File.WriteAllText(escaped, "outside");
        string link = Path.Combine(mod, "source");
        try
        {
            Directory.CreateSymbolicLink(link, original);
        }
        catch (UnauthorizedAccessException ex) { Assert.Skip($"Symlink capability unavailable: {ex.Message}"); }
        catch (IOException ex) { Assert.Skip($"Symlink capability unavailable: {ex.Message}"); }

        string selected = Path.Combine(link, "body.mdl");
        var inspection = files.Boundary.InspectExportCandidates(
            mod,
            new Dictionary<string, IReadOnlyList<string>> { [selected] = ["a/body.mdl"] },
            CancellationToken.None);
        var candidate = Assert.Single(inspection.Value!.Candidates);
        Directory.Delete(link);
        Directory.CreateSymbolicLink(link, outside);

        string destination = Path.Combine(files.Root, "export.mcdf");
        var written = await files.Boundary.WritePackage(
            destination,
            new McdfExportContent("", "", "", "",
                [new McdfExportFile(candidate.GamePaths, candidate.LocalPath!, candidate.Source)],
                new Dictionary<string, string>()),
            _ => { },
            CancellationToken.None);

        Assert.False(written.Success);
        Assert.False(File.Exists(destination));
    }

    [Fact]
    public async Task Same_length_source_mutation_fails_before_destination_commit()
    {
        using var files = new TempFiles();
        string mod = Path.Combine(files.Root, "mod");
        string local = Path.Combine(mod, "body.mdl");
        Directory.CreateDirectory(mod);
        File.WriteAllText(local, "payload");
        var inspection = files.Boundary.InspectExportCandidates(
            mod,
            new Dictionary<string, IReadOnlyList<string>> { [local] = ["a/body.mdl"] },
            CancellationToken.None);
        var candidate = Assert.Single(inspection.Value!.Candidates);
        File.WriteAllText(local, "changed");

        string destination = Path.Combine(files.Root, "export.mcdf");
        var written = await files.Boundary.WritePackage(
            destination,
            new McdfExportContent("", "", "", "",
                [new McdfExportFile(candidate.GamePaths, candidate.LocalPath!, candidate.Source)],
                new Dictionary<string, string>()),
            _ => { },
            CancellationToken.None);

        Assert.False(written.Success);
        Assert.False(File.Exists(destination));
    }

    [Fact]
    public async Task Source_identity_change_fails_when_platform_exposes_identity()
    {
        using var files = new TempFiles();
        string mod = Path.Combine(files.Root, "mod");
        string local = Path.Combine(mod, "body.mdl");
        Directory.CreateDirectory(mod);
        File.WriteAllText(local, "payload");
        var inspection = files.Boundary.InspectExportCandidates(
            mod,
            new Dictionary<string, IReadOnlyList<string>> { [local] = ["a/body.mdl"] },
            CancellationToken.None);
        var candidate = Assert.Single(inspection.Value!.Candidates);
        if (candidate.Source?.Identity == null)
            Assert.Skip("The platform did not expose a stable file identity.");

        string replacement = Path.Combine(mod, "replacement.mdl");
        File.WriteAllText(replacement, "payload");
        File.Replace(replacement, local, destinationBackupFileName: null);
        string destination = Path.Combine(files.Root, "export.mcdf");
        var written = await files.Boundary.WritePackage(
            destination,
            new McdfExportContent("", "", "", "",
                [new McdfExportFile(candidate.GamePaths, candidate.LocalPath!, candidate.Source)],
                new Dictionary<string, string>()),
            _ => { },
            CancellationToken.None);

        Assert.False(written.Success);
        Assert.False(File.Exists(destination));
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
    public void Operation_directory_collision_does_not_claim_preexisting_directory()
    {
        using var files = new TempFiles();
        Guid collision = Guid.Parse("11111111-1111-1111-1111-111111111111");
        Guid success = Guid.Parse("22222222-2222-2222-2222-222222222222");
        string root = Path.Combine(Path.GetTempPath(), "Poser");
        string occupied = Path.Combine(root, $"mcdf-{collision:N}");
        Directory.CreateDirectory(occupied);
        File.WriteAllText(Path.Combine(occupied, "keep.txt"), "keep");
        var ids = new Queue<Guid>([collision, success]);
        var boundary = new McdfFileBoundary(() => ids.Dequeue());

        var result = boundary.CreateOperationDirectory();

        Assert.True(result.Success);
        Assert.NotEqual(occupied, result.Value);
        Assert.Equal("keep", File.ReadAllText(Path.Combine(occupied, "keep.txt")));
        Assert.True(boundary.DeleteOperationDirectory(result.Value!).Success);
        Directory.Delete(occupied, recursive: true);
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
        Assert.Empty(Directory.EnumerateFiles(files.Root, ".export.mcdf.*.tmp"));
    }

    [Fact]
    public async Task Stale_temp_and_destination_race_preserve_existing_files()
    {
        using var files = new TempFiles();
        string source = Path.Combine(files.Root, "body.mdl");
        string destination = Path.Combine(files.Root, "export.mcdf");
        File.WriteAllText(source, "payload");
        Guid tempGuid = Guid.Parse("33333333-3333-3333-3333-333333333333");
        Guid freshGuid = Guid.Parse("44444444-4444-4444-4444-444444444444");
        string stale = Path.Combine(files.Root, ".export.mcdf.33333333333333333333333333333333.tmp");
        File.WriteAllText(stale, "stale");
        bool raced = false;
        var ids = new Queue<Guid>([tempGuid, tempGuid, freshGuid]);
        var boundary = new McdfFileBoundary(() => ids.Dequeue());

        var result = await boundary.WritePackage(
            destination,
            new McdfExportContent("", "", "", "",
                [new McdfExportFile(["a/body.mdl"], source)],
                new Dictionary<string, string>()),
            _ =>
            {
                if (!raced)
                {
                    raced = true;
                    File.WriteAllText(destination, "racer");
                }
            },
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("racer", File.ReadAllText(destination));
        Assert.Equal("stale", File.ReadAllText(stale));
    }

    [Fact]
    public void Inspection_cancellation_is_observed_during_hashing()
    {
        using var files = new TempFiles();
        string mod = Path.Combine(files.Root, "mod");
        string local = Path.Combine(mod, "body.mdl");
        Directory.CreateDirectory(mod);
        File.WriteAllBytes(local, new byte[ChunkSizeForTest * 2]);
        using var cancellation = new CancellationTokenSource();
        var boundary = new McdfFileBoundary(inspectChunk: cancellation.Cancel);

        var result = boundary.InspectExportCandidates(
            mod,
            new Dictionary<string, IReadOnlyList<string>> { [local] = ["a/body.mdl"] },
            cancellation.Token);

        Assert.False(result.Success);
        Assert.Contains("cancelled", result.Detail!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Write_cancellation_is_observed_during_payload_copy()
    {
        using var files = new TempFiles();
        string source = Path.Combine(files.Root, "body.mdl");
        string duplicate = Path.Combine(files.Root, "duplicate.mdl");
        File.WriteAllBytes(source, new byte[ChunkSizeForTest * 2]);
        File.Copy(source, duplicate);
        string destination = Path.Combine(files.Root, "export.mcdf");
        using var cancellation = new CancellationTokenSource();
        bool cancelled = false;
        var result = await files.Boundary.WritePackage(
            destination,
            new McdfExportContent("", "", "", "",
                [
                    new McdfExportFile(["a/body.mdl"], source),
                    new McdfExportFile(["a/duplicate.mdl"], duplicate),
                ],
                new Dictionary<string, string>()),
            step =>
            {
                if (!cancelled
                    && step.BytesDone > 0
                    && step.BytesTotal == ChunkSizeForTest * 2)
                {
                    cancelled = true;
                    cancellation.Cancel();
                }
            },
            cancellation.Token);

        Assert.False(result.Success);
        Assert.False(File.Exists(destination));
    }

    [Fact]
    public void Export_dtos_copy_nested_inputs()
    {
        var gamePaths = new List<string> { "a/body.mdl" };
        var source = new Dictionary<string, string> { ["a/body.mdl"] = "b/body.mdl" };
        var file = new McdfExportFile(gamePaths, "body.mdl");
        var content = new McdfExportContent("", "", "", "", [file], source);
        var candidate = new McdfExportCandidate(
            "body.mdl", gamePaths, McdfExportCandidateKind.GamePath, null, 0);
        var inspection = new McdfExportInspection([candidate], gamePaths);
        gamePaths.Add("mutated");
        source["mutated"] = "mutated";

        Assert.Single(file.GamePaths);
        Assert.Single(content.Files);
        Assert.Single(content.Swaps);
        Assert.Single(candidate.GamePaths);
        Assert.Single(inspection.Skipped);
    }

    private const int ChunkSizeForTest = 81920;

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
