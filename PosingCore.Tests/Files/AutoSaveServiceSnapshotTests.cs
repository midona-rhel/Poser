using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NSubstitute;
using Poser.Entities;
using Poser.Services;
using Poser.Tests.Fixtures;

namespace Poser.Tests.Files;

/// <summary>
/// What a snapshot contains and where it lands: candidate selection, folder
/// naming, file naming, and per-actor failure isolation.
/// </summary>
public class AutoSaveServiceSnapshotTests
{
    [Fact]
    public void SaveNow_exports_only_actors_with_authored_edits()
    {
        using var h = new AutoSaveHarness();
        var alpha = h.AddActor("Alpha");
        var beta = h.AddActor("Beta");
        var gamma = h.AddActor("Gamma", authored: false);

        var saved = h.Service.SaveNow("test");

        Assert.Equal(2, saved);
        Assert.Equal(2, h.ExportCallCount);

        var folder = Path.Combine(h.Root, h.StampNow());
        h.PoseFiles.Received(1).ExportPose(alpha.Skeletons, Path.Combine(folder, "Alpha.pose"));
        h.PoseFiles.Received(1).ExportPose(beta.Skeletons, Path.Combine(folder, "Beta.pose"));
        h.PoseFiles.DidNotReceive().ExportPose(gamma.Skeletons, Arg.Any<string>());

        Assert.Equal(h.NowUtc, h.Service.LastSaveUtc);
    }

    [Fact]
    public void SaveNow_names_the_folder_from_the_injected_utc_clock()
    {
        using var h = new AutoSaveHarness();
        h.NowUtc = new DateTime(2026, 12, 31, 23, 45, 6, DateTimeKind.Utc);
        h.AddActor("Alpha");

        h.Service.SaveNow("test");

        // 24-hour UTC, so folder-name order is time order (the deliberate
        // deviation from Ktisis/Brio's 12-hour "hh").
        Assert.Equal(new[] { "2026-12-31 23-45-06Z" }, h.SnapshotFolders());
    }

    [Fact]
    public void SaveNow_with_no_authored_edits_writes_nothing()
    {
        using var h = new AutoSaveHarness();
        h.AddActor("Alpha", authored: false);
        h.AddActor("Beta", authored: false);

        var saved = h.Service.SaveNow("test");

        Assert.Equal(0, saved);
        Assert.Equal(0, h.ExportCallCount);
        Assert.Empty(Directory.GetDirectories(h.Root));
        Assert.Null(h.Service.LastSaveUtc);
    }

    [Fact]
    public void SaveNow_with_no_actors_at_all_writes_nothing()
    {
        using var h = new AutoSaveHarness();

        var saved = h.Service.SaveNow("test");

        Assert.Equal(0, saved);
        Assert.Equal(0, h.ExportCallCount);
        Assert.Empty(Directory.GetDirectories(h.Root));
    }

    [Fact]
    public void SaveNow_deduplicates_identical_actor_names_within_a_snapshot()
    {
        using var h = new AutoSaveHarness();
        var first = h.AddActor("Zidane");
        var second = h.AddActor("Zidane");

        var saved = h.Service.SaveNow("test");

        Assert.Equal(2, saved);
        var folder = Path.Combine(h.Root, h.StampNow());
        h.PoseFiles.Received(1).ExportPose(first.Skeletons, Path.Combine(folder, "Zidane.pose"));
        h.PoseFiles.Received(1).ExportPose(second.Skeletons, Path.Combine(folder, "Zidane (2).pose"));
    }

    [Fact]
    public void SaveNow_sanitizes_invalid_filename_characters()
    {
        using var h = new AutoSaveHarness();
        var messy = h.AddActor("A<b>:c");

        h.Service.SaveNow("test");

        var folder = Path.Combine(h.Root, h.StampNow());
        h.PoseFiles.Received(1).ExportPose(messy.Skeletons, Path.Combine(folder, "A_b__c.pose"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void SaveNow_falls_back_to_Actor_for_a_blank_name(string blank)
    {
        using var h = new AutoSaveHarness();
        var nameless = h.AddActor(blank);

        h.Service.SaveNow("test");

        var folder = Path.Combine(h.Root, h.StampNow());
        h.PoseFiles.Received(1).ExportPose(nameless.Skeletons, Path.Combine(folder, "Actor.pose"));
    }

    [Fact]
    public void SaveNow_continues_after_an_export_returns_false()
    {
        using var h = new AutoSaveHarness();
        var bad = h.AddActor("Bad");
        var good = h.AddActor("Good");
        h.FailExportFor(bad);

        var saved = h.Service.SaveNow("test");

        // Brio aborts the whole snapshot on one bad actor; this must not.
        Assert.Equal(1, saved);
        Assert.Equal(2, h.ExportCallCount);
        h.PoseFiles.Received(1).ExportPose(good.Skeletons, Arg.Any<string>());
        Assert.True(h.ErrorCount >= 1, "the failed export must be logged as an error");
    }

    [Fact]
    public void SaveNow_continues_after_an_export_throws()
    {
        using var h = new AutoSaveHarness();
        var boom = h.AddActor("Boom");
        var good = h.AddActor("Good");
        h.PoseFiles
            .ExportPose(boom.Skeletons, Arg.Any<string>())
            .Returns(_ => throw new IOException("disk gone"));

        var saved = h.Service.SaveNow("test");

        Assert.Equal(1, saved);
        h.PoseFiles.Received(1).ExportPose(good.Skeletons, Arg.Any<string>());
        Assert.True(h.ErrorCount >= 1, "the throwing export must be logged as an error");
    }

    [Fact]
    public void SaveNow_continues_after_the_skeleton_scan_throws_for_one_actor()
    {
        using var h = new AutoSaveHarness();
        h.AddActorThatThrows("Broken", new InvalidOperationException("skeleton gone"));
        var good = h.AddActor("Good");

        var saved = h.Service.SaveNow("test");

        // The broken actor never becomes a candidate, so it is never exported.
        Assert.Equal(1, saved);
        Assert.Equal(1, h.ExportCallCount);
        h.PoseFiles.Received(1).ExportPose(good.Skeletons, Arg.Any<string>());
        Assert.True(h.ErrorCount >= 1, "the failed actor scan must be logged as an error");
    }

    [Fact]
    public void SaveNow_suffixes_the_folder_when_the_timestamp_already_exists()
    {
        using var h = new AutoSaveHarness();
        var collided = h.SeedSnapshot(h.StampNow());
        var actor = h.AddActor("Alpha");

        var saved = h.Service.SaveNow("test");

        Assert.Equal(1, saved);
        var expectedFolder = Path.Combine(h.Root, $"{h.StampNow()} (2)");
        Assert.True(Directory.Exists(expectedFolder));
        Assert.True(Directory.Exists(collided));
        h.PoseFiles.Received(1)
            .ExportPose(actor.Skeletons, Path.Combine(expectedFolder, "Alpha.pose"));
    }

    [Fact]
    public void SaveNow_passes_the_skeleton_list_from_the_skeleton_service()
    {
        using var h = new AutoSaveHarness();
        var actor = h.AddActor("Alpha");

        h.Service.SaveNow("test");

        var forwarded = h.PoseFiles.ReceivedCalls()
            .Single(call => call.GetMethodInfo().Name == nameof(IPoseFileService.ExportPose))
            .GetArguments()[0];

        // Reference identity: the exporter gets exactly what ISkeletonService
        // returned, not a copy or a re-query.
        Assert.Same(actor.Skeletons, forwarded);
        Assert.Equal(h.Skeletons.GetSkeletons(actor.Actor), (IReadOnlyList<ISkeleton>)forwarded!);
    }
}
