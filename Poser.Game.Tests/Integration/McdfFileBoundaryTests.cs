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
        catch (IOException ex) when ((ex.HResult & 0xFFFF) == 1314)
        { Assert.Skip($"Symlink privilege unavailable: {ex.Message}"); }
        catch (PlatformNotSupportedException ex) { Assert.Skip($"Symlink capability unavailable: {ex.Message}"); }
        catch (NotSupportedException ex) { Assert.Skip($"Symlink capability unavailable: {ex.Message}"); }

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
        catch (IOException ex) when ((ex.HResult & 0xFFFF) == 1314)
        { Assert.Skip($"Symlink privilege unavailable: {ex.Message}"); }
        catch (PlatformNotSupportedException ex) { Assert.Skip($"Symlink capability unavailable: {ex.Message}"); }
        catch (NotSupportedException ex) { Assert.Skip($"Symlink capability unavailable: {ex.Message}"); }

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
        catch (IOException ex) when ((ex.HResult & 0xFFFF) == 1314)
        { Assert.Skip($"Symlink privilege unavailable: {ex.Message}"); }
        catch (PlatformNotSupportedException ex) { Assert.Skip($"Symlink capability unavailable: {ex.Message}"); }
        catch (NotSupportedException ex) { Assert.Skip($"Symlink capability unavailable: {ex.Message}"); }

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
        Assert.True(first.Success, first.Detail);
        Assert.True(second.Success, second.Detail);
        Assert.NotEqual(first.Value, second.Value);
        Assert.True(Directory.Exists(first.Value!.Path));
        Assert.True(Directory.Exists(second.Value!.Path));

        Assert.True(files.Boundary.DeleteOperationDirectory(first.Value!).Success);
        Assert.True(files.Boundary.DeleteOperationDirectory(first.Value!).Success);
        Assert.True(files.Boundary.DeleteOperationDirectory(second.Value!).Success);
    }

    [Fact]
    public void Operation_directory_collision_does_not_claim_preexisting_directory()
    {
        using var files = new TempFiles();
        Guid collision = Guid.NewGuid();
        Guid success = Guid.NewGuid();
        string root = Path.Combine(Path.GetTempPath(), "Poser");
        string occupied = Path.Combine(root, $"mcdf-{collision:N}");
        Directory.CreateDirectory(occupied);
        File.WriteAllText(Path.Combine(occupied, "keep.txt"), "keep");
        var ids = new Queue<Guid>([collision, success]);
        var boundary = new McdfFileBoundary(() => ids.Dequeue());

        var result = boundary.CreateOperationDirectory();

        Assert.True(result.Success, result.Detail);
        Assert.NotEqual(occupied, result.Value!.Path);
        Assert.Equal("keep", File.ReadAllText(Path.Combine(occupied, "keep.txt")));
        Assert.True(boundary.DeleteOperationDirectory(result.Value!).Success);
        Directory.Delete(occupied, recursive: true);
    }

    [Fact]
    public void Operation_staging_collision_is_never_adopted_or_deleted()
    {
        Guid collision = Guid.NewGuid();
        Guid success = Guid.NewGuid();
        string root = Path.Combine(Path.GetTempPath(), "Poser");
        string staging = Path.Combine(root, $".mcdf-staging-{collision:N}");
        Directory.CreateDirectory(staging);
        string foreign = Path.Combine(staging, "foreign.txt");
        File.WriteAllText(foreign, "foreign");
        var ids = new Queue<Guid>([collision, success]);
        var boundary = new McdfFileBoundary(() => ids.Dequeue());

        var result = boundary.CreateOperationDirectory();

        Assert.True(result.Success, result.Detail);
        Assert.Equal("foreign", File.ReadAllText(foreign));
        Assert.True(boundary.DeleteOperationDirectory(result.Value!).Success);
        Directory.Delete(staging, recursive: true);
    }

    [Fact]
    public void Operation_cleanup_refuses_owner_token_mismatch()
    {
        var boundary = new McdfFileBoundary();
        var allocated = boundary.CreateOperationDirectory();
        Assert.True(allocated.Success, allocated.Detail);
        var ownership = allocated.Value!;
        File.WriteAllText(Path.Combine(ownership.Path, ".owner"), "foreign-token");
        File.WriteAllText(Path.Combine(ownership.Path, "foreign.txt"), "foreign");

        var deleted = boundary.DeleteOperationDirectory(ownership);

        Assert.False(deleted.Success);
        Assert.Equal("foreign", File.ReadAllText(Path.Combine(ownership.Path, "foreign.txt")));
        Directory.Delete(ownership.Path, recursive: true);
    }

    [Fact]
    public void Operation_cleanup_refuses_identity_mismatch_and_preserves_foreign_replacement()
    {
        var boundary = new McdfFileBoundary();
        var allocated = boundary.CreateOperationDirectory();
        Assert.True(allocated.Success, allocated.Detail);
        var ownership = allocated.Value!;
        Directory.Delete(ownership.Path, recursive: true);
        Directory.CreateDirectory(ownership.Path);
        File.WriteAllText(Path.Combine(ownership.Path, ".owner"), ownership.OwnerToken);
        File.WriteAllText(Path.Combine(ownership.Path, "foreign.txt"), "foreign");

        var deleted = boundary.DeleteOperationDirectory(ownership);

        Assert.False(deleted.Success);
        Assert.Equal("foreign", File.ReadAllText(Path.Combine(ownership.Path, "foreign.txt")));
        Directory.Delete(ownership.Path, recursive: true);
    }

    [Fact]
    public void Operation_cleanup_refuses_marker_identity_replacement_even_with_same_token()
    {
        var boundary = new McdfFileBoundary();
        var allocated = boundary.CreateOperationDirectory();
        Assert.True(allocated.Success, allocated.Detail);
        var ownership = allocated.Value!;
        string marker = Path.Combine(ownership.Path, ".owner");
        string replacement = marker + ".replacement";
        File.Move(marker, replacement);
        File.WriteAllText(marker, ownership.OwnerToken);
        File.WriteAllText(Path.Combine(ownership.Path, "foreign.txt"), "foreign");

        var deleted = boundary.DeleteOperationDirectory(ownership);

        Assert.False(deleted.Success);
        Assert.Equal("foreign", File.ReadAllText(Path.Combine(ownership.Path, "foreign.txt")));
        Assert.True(File.Exists(marker));
        Directory.Delete(ownership.Path, recursive: true);
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
    public async Task Cancellation_after_finalization_prevents_commit_and_preserves_destination()
    {
        using var files = new TempFiles();
        string source = Path.Combine(files.Root, "body.mdl");
        string destination = Path.Combine(files.Root, "export.mcdf");
        File.WriteAllText(source, "payload");
        File.WriteAllText(destination, "old destination");
        using var cancellation = new CancellationTokenSource();
        var boundary = new McdfFileBoundary(
            beforeCommit: _ => cancellation.Cancel());

        var result = await boundary.WritePackage(
            destination,
            new McdfExportContent("", "", "", "",
                [new McdfExportFile(["a/body.mdl"], source)],
                new Dictionary<string, string>()),
            _ => { }, cancellation.Token);

        Assert.False(result.Success);
        Assert.Contains("cancelled", result.Detail!, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("old destination", File.ReadAllText(destination));
    }

    [Fact]
    public async Task Exact_temp_cleanup_failure_retains_path_evidence_and_destination()
    {
        using var files = new TempFiles();
        string source = Path.Combine(files.Root, "body.mdl");
        string destination = Path.Combine(files.Root, "export.mcdf");
        File.WriteAllText(source, "payload");
        File.WriteAllText(destination, "old destination");
        string? ownedTemporary = null;
        var boundary = new McdfFileBoundary(
            beforeCommit: temporary =>
            {
                ownedTemporary = temporary;
                throw new IOException("injected commit refusal");
            },
            markDeleteOnClose: _ =>
                throw new IOException("injected disposition refusal"));

        var result = await boundary.WritePackage(
            destination,
            new McdfExportContent("", "", "", "",
                [new McdfExportFile(["a/body.mdl"], source)],
                new Dictionary<string, string>()),
            _ => { }, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("old destination", File.ReadAllText(destination));
        Assert.NotNull(ownedTemporary);
        Assert.True(File.Exists(ownedTemporary));
        Assert.Contains("injected commit refusal", result.Detail!);
        Assert.Contains("injected disposition refusal", result.Detail!);
        Assert.Contains(ownedTemporary, result.Detail!);
        Assert.Contains("manual cleanup", result.Detail!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Existing_destination_replacement_is_refused_without_overwrite()
    {
        using var files = new TempFiles();
        string source = Path.Combine(files.Root, "body.mdl");
        string destination = Path.Combine(files.Root, "export.mcdf");
        string foreign = Path.Combine(files.Root, "foreign.mcdf");
        File.WriteAllText(source, "payload");
        File.WriteAllText(destination, "original destination");
        File.WriteAllText(foreign, "foreign destination");
        int identityReads = 0;
        var boundary = new McdfFileBoundary(
            beforeDestinationCommit: _ => File.Replace(foreign, destination, null),
            getDestinationIdentity: _ =>
                ++identityReads == 1 ? "admitted" : "replaced");

        var result = await boundary.WritePackage(
            destination,
            new McdfExportContent("", "", "", "",
                [new McdfExportFile(["a/body.mdl"], source)],
                new Dictionary<string, string>()),
            _ => { }, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("foreign destination", File.ReadAllText(destination));
    }

    [Fact]
    public async Task Existing_destination_disappearance_is_refused_without_create()
    {
        using var files = new TempFiles();
        string source = Path.Combine(files.Root, "body.mdl");
        string destination = Path.Combine(files.Root, "export.mcdf");
        File.WriteAllText(source, "payload");
        File.WriteAllText(destination, "original destination");
        var boundary = new McdfFileBoundary(
            beforeDestinationCommit: _ => File.Delete(destination));

        var result = await boundary.WritePackage(
            destination,
            new McdfExportContent("", "", "", "",
                [new McdfExportFile(["a/body.mdl"], source)],
                new Dictionary<string, string>()),
            _ => { }, CancellationToken.None);

        Assert.False(result.Success);
        Assert.False(File.Exists(destination));
    }

    [Fact]
    public async Task Commit_renames_the_exact_owned_temp_handle_not_a_path_replacement()
    {
        if (!OperatingSystem.IsWindows())
            Assert.Skip("Exact open-handle rename is a Windows boundary contract.");
        using var files = new TempFiles();
        string source = Path.Combine(files.Root, "body.mdl");
        string destination = Path.Combine(files.Root, "export.mcdf");
        File.WriteAllText(source, "payload");
        string? foreignTemp = null;
        var boundary = new McdfFileBoundary(beforeCommit: temporary =>
        {
            string displaced = temporary + ".displaced";
            File.Move(temporary, displaced);
            File.WriteAllText(temporary, "foreign replacement");
            foreignTemp = temporary;
        });

        var result = await boundary.WritePackage(
            destination,
            new McdfExportContent("", "", "", "",
                [new McdfExportFile(["a/body.mdl"], source)],
                new Dictionary<string, string>()),
            _ => { }, CancellationToken.None);

        Assert.True(result.Success, result.Detail);
        Assert.True(File.Exists(destination));
        Assert.Equal("foreign replacement", File.ReadAllText(foreignTemp!));
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

    [Fact]
    public void Export_dto_deconstruction_and_reference_equality_are_explicit()
    {
        var firstFile = new McdfExportFile(["a/body.mdl"], "body.mdl");
        var equivalentFile = new McdfExportFile(["a/body.mdl"], "body.mdl");
        var content = new McdfExportContent(
            "description", "glamourer", "customize", "manipulations",
            [firstFile], new Dictionary<string, string> { ["a"] = "b" });

        var (paths, localPath, source) = firstFile;
        var (description, glamourer, customize, manipulations, files, swaps) = content;

        Assert.Equal(["a/body.mdl"], paths);
        Assert.Equal("body.mdl", localPath);
        Assert.Null(source);
        Assert.Equal("description", description);
        Assert.Equal("glamourer", glamourer);
        Assert.Equal("customize", customize);
        Assert.Equal("manipulations", manipulations);
        Assert.Single(files);
        Assert.Single(swaps);
        Assert.NotEqual(firstFile, equivalentFile);
    }

    [Fact]
    public async Task Begin_export_freezes_vendor_state_then_inspects_off_thread_cancellably()
    {
        int callerThread = System.Environment.CurrentManagedThreadId;
        var runtime = DispatchProxy.Create<IIntegrationRuntimePort, ExportRuntimeProxy>();
        var runtimeProxy = (ExportRuntimeProxy)(object)runtime;
        runtimeProxy.CallerThread = callerThread;
        var boundary = new ExportBoundaryFake();
        var session = new ActorIntegrationSession(runtime, boundary);
        var actor = new ActorId(Guid.NewGuid(), 1);

        var started = session.BeginExport(actor, "export.mcdf", "description");

        Assert.True(started.Success, started.Detail);
        Assert.All(runtimeProxy.VendorReadThreads, thread => Assert.Equal(callerThread, thread));
        Assert.True(session.McdfBusy);
        Assert.True(session.Mcdf!.Cancellable);
        Assert.Equal(McdfPhase.CapturingExport, session.Mcdf.Phase);
        Assert.False(boundary.InspectionEntered.IsSet);

        session.CancelMcdf();
        boundary.AllowInspection.Set();
        Assert.True(boundary.InspectionEntered.Wait(
            TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        await WaitUntilAsync(() => !session.McdfBusy);

        Assert.NotEqual(callerThread, boundary.InspectionThread);
        Assert.True(boundary.InspectionCancellation.CanBeCanceled);
        Assert.True(boundary.InspectionCancellation.IsCancellationRequested);
        Assert.True(session.Mcdf!.Outcome!.Cancelled);
    }

    [Fact]
    public void Operation_post_rename_verification_failure_cleans_exact_directory()
    {
        Guid id = Guid.NewGuid();
        string root = Path.Combine(Path.GetTempPath(), "Poser");
        string staging = Path.Combine(root, $".mcdf-staging-{id:N}");
        string directory = Path.Combine(root, $"mcdf-{id:N}");
        var boundary = new McdfFileBoundary(
            newGuid: () => id,
            getOperationFinalPath: _ => throw new IOException("injected postcondition failure"));

        var result = boundary.CreateOperationDirectory();

        Assert.False(result.Success);
        Assert.False(Directory.Exists(staging));
        Assert.False(Directory.Exists(directory));
    }

    [Fact]
    public void Required_handle_path_retrieval_failure_fails_closed()
    {
        using var files = new TempFiles();
        string mod = Path.Combine(files.Root, "mod");
        string local = Path.Combine(mod, "body.mdl");
        Directory.CreateDirectory(mod);
        File.WriteAllText(local, "payload");
        var boundary = new McdfFileBoundary(
            getFinalPath: _ => throw new Win32Exception(5));

        var result = boundary.InspectExportCandidates(
            mod,
            new Dictionary<string, IReadOnlyList<string>>
            {
                [local] = ["a/body.mdl"],
            },
            CancellationToken.None);

        Assert.False(result.Success);
    }

    [Fact]
    public void Long_final_handle_path_is_captured_without_a_fixed_buffer()
    {
        using var files = new TempFiles();
        string mod = Path.Combine(files.Root, "mod");
        string nested = mod;
        try
        {
            while (nested.Length < 620)
                nested = Path.Combine(nested, new string('d', 40));
            Directory.CreateDirectory(nested);
            string local = Path.Combine(nested, "body.mdl");
            File.WriteAllText(local, "payload");

            var result = files.Boundary.InspectExportCandidates(
                mod,
                new Dictionary<string, IReadOnlyList<string>>
                {
                    [local] = ["a/body.mdl"],
                },
                CancellationToken.None);

            Assert.True(result.Success, result.Detail);
            Assert.Equal(
                Path.GetFullPath(local),
                Assert.Single(result.Value!.Candidates).Source!.CanonicalPath);
        }
        catch (PathTooLongException ex)
        {
            Assert.Skip($"Long paths are not enabled on this Windows installation: {ex.Message}");
        }
    }

    [Fact]
    public async Task Missing_current_identity_fails_when_inspection_expected_one()
    {
        using var files = new TempFiles();
        string source = Path.Combine(files.Root, "body.mdl");
        File.WriteAllText(source, "payload");
        var inspection = files.Boundary.InspectExportCandidates(
            files.Root,
            new Dictionary<string, IReadOnlyList<string>>
            {
                [source] = ["a/body.mdl"],
            },
            CancellationToken.None);
        var candidate = Assert.Single(inspection.Value!.Candidates);
        if (candidate.Source!.Identity == null)
            Assert.Skip("The platform did not expose a stable file identity.");
        var boundary = new McdfFileBoundary(getIdentity: _ => null);

        var result = await boundary.WritePackage(
            Path.Combine(files.Root, "export.mcdf"),
            new McdfExportContent("", "", "", "",
                [new McdfExportFile(candidate.GamePaths, candidate.LocalPath!, candidate.Source)],
                new Dictionary<string, string>()),
            _ => { }, CancellationToken.None);

        Assert.False(result.Success);
    }

    private const int ChunkSizeForTest = 81920;

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!predicate())
            await Task.Delay(10, timeout.Token);
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
        public IntegrationValue<McdfOperationDirectory> CreateOperationDirectory() =>
            throw new NotSupportedException();
        public IntegrationValue<McdfExportInspection> InspectExportCandidates(
            string modRoot,
            IReadOnlyDictionary<string, IReadOnlyList<string>> resources,
            CancellationToken cancellation)
        {
            AllowInspection.Wait(TimeSpan.FromSeconds(5), cancellation);
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
