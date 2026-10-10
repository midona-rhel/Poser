using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using NSubstitute;
using NSubstitute.Core;
using Poser.Core;
using Poser.Domain.Posing;
using Poser.Entities;
using Poser.Files;
using Poser.Services;
using Poser.Domain.Transforms;
using Poser.Documents.AutoSave;
using Poser.Documents.Config;
using Poser.Documents.Files;
using Poser.Application.AutoSave;
using Poser.Application.Events;
using Poser.Application.Lifecycle;
using Poser.Application.Settings;

namespace Poser.Tests.Fixtures;

/// <summary>
/// One actor wired into the harness together with the exact skeleton list
/// instance <see cref="ISkeletonService.GetSkeletons"/> hands back for it, so a
/// test can assert that the very same list reached
/// <see cref="IPoseFileService.CreatePoseFile"/>.
/// </summary>
internal sealed record FakeActor(IActor Actor, IReadOnlyList<ISkeleton> Skeletons)
{
    public string Name => Actor.Name;
}

/// <summary>
/// Builds an <see cref="AutoSaveService"/> over substituted collaborators, a
/// real (empty) <see cref="ConfigurationService"/>, a controllable UTC clock,
/// and a throwaway temp root.
///
/// <para>The framework is deliberately null: the service then never subscribes
/// to the game tick and the test drives <c>Tick(nowUtc)</c> itself, which is the
/// only way to make interval behaviour deterministic.</para>
///
/// <para>THE SERVICE IS SPLIT ACROSS TWO THREADS. <c>SaveNow</c> only captures
/// (<see cref="IPoseFileService.CreatePoseFile"/>) and returns; the folder, the
/// files and the prune all happen on one owned worker. Every assertion about the
/// disk therefore has to be preceded by <see cref="WaitForWrite"/>.</para>
/// </summary>
internal sealed class AutoSaveHarness : IDisposable
{
    public const string StampFormat = "yyyy-MM-dd HH-mm-ss'Z'";

    private readonly List<IActor> _actors = new();
    private AutoSaveService? _service;

    public string Root { get; }
    public IPluginLog Log { get; }
    public IEventBus EventBus { get; }
    public IGPoseService GPose { get; }
    public IActorManager ActorManager { get; }
    public ISkeletonService Skeletons { get; }
    public IBonePosingService BonePosing { get; }
    public IPoseFileService PoseFiles { get; }
    public ConfigurationService Configuration { get; }

    /// <summary>Value returned by the injected clock; mutate freely mid-test.</summary>
    public DateTime NowUtc { get; set; } = new(2026, 3, 4, 6, 0, 0, DateTimeKind.Utc);

    /// <summary>Worker boundary; override to test capture without dispatch.</summary>
    public Func<Action, bool> Dispatch { get; set; } = work =>
    {
        _ = Task.Run(work);
        return true;
    };

    public AutoSaveConfiguration Settings => Configuration.Config.AutoSave;

    public AutoSaveHarness()
    {
        // Deliberately the machine temp dir, never anything under the repo: the
        // snapshots are real files and a synced folder would both slow the run
        // down and hold handles the prune tests need released.
        Root = Path.Combine(
            Path.GetTempPath(), "poser-autosave-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);

        Log = Substitute.For<IPluginLog>();
        EventBus = Substitute.For<IEventBus>();

        GPose = Substitute.For<IGPoseService>();
        GPose.IsGPosing.Returns(true);

        ActorManager = Substitute.For<IActorManager>();
        ActorManager.Actors.Returns(_ => _actors);

        Skeletons = Substitute.For<ISkeletonService>();
        BonePosing = Substitute.For<IBonePosingService>();

        PoseFiles = Substitute.For<IPoseFileService>();
        // A REAL PoseFile per capture, not a substitute: the worker calls
        // PoseFile.Save on whatever it was handed, so a stub would leave the
        // tests asserting against files that were never actually serialized.
        PoseFiles
            .CreatePoseFile(Arg.Any<IReadOnlyList<ISkeleton>>())
            .Returns(_ => NewPoseFile());

        Configuration = new ConfigurationService(new Poser.Tests.Fixtures.MemoryConfigurationPersistence());
    }

    /// <summary>
    /// Constructed on first use so a test can seed config and actors first.
    /// </summary>
    public AutoSaveService Service => _service ??= new AutoSaveService(
        new Poser.Game.AutoSave.PoseAutoSaveCapturePort(Log, () => ActorManager,
            () => Skeletons, () => BonePosing, () => PoseFiles),
        Configuration,
        new PoseAutoSaveStore(Root, message => Log.Error(message),
            message => Log.Info(message), message => Log.Debug(message)),
        message => Log.Error(message), message => Log.Debug(message),
        () => NowUtc, Dispatch);

    /// <summary>
    /// A minimal but genuine pose: two bones, so <c>PoseFile.Save</c> produces
    /// real JSON that <c>PoseFile.Load</c> reads back.
    /// </summary>
    public static PoseFile NewPoseFile() => new()
    {
        Bones =
        {
            ["j_kosi"] = new PoseFile.BoneData
            {
                Position = new Vector3(0f, 0.25f, 0f),
                Rotation = Quaternion.Identity,
                Scale = Vector3.One
            },
            ["j_sebo_a"] = PoseFile.BoneData.Identity
        }
    };

    /// <summary>Adds an actor whose single Character skeleton carries a
    /// user-authored stack.</summary>
    public FakeActor AddActor(string name)
    {
        var actor = Substitute.For<IActor>();
        actor.Name.Returns(name);

        var skeleton = Substitute.For<ISkeleton>();
        var slots = new List<ISkeleton> { skeleton };
        Skeletons.GetSkeletons(actor).Returns(slots);
        BonePosing.GetPoseInfo(skeleton).Returns(BuildPoseInfo());

        _actors.Add(actor);
        return new FakeActor(actor, slots);
    }

    /// <summary>
    /// Advances the injected clock, drives one tick with the same instant (so
    /// the interval decision and the snapshot folder name never disagree), and
    /// waits out any write the tick dispatched — otherwise the next tick's save
    /// would be dropped as "previous snapshot still in flight".
    /// </summary>
    public void TickAt(DateTime nowUtc)
    {
        NowUtc = nowUtc;
        Service.Tick(nowUtc, GPose.IsGPosing);
        WaitForWrite();
    }

    /// <summary>
    /// Makes the WRITE half fail for one actor, by capturing a pose the worker
    /// cannot serialize. The actor is still captured (so it still counts
    /// towards <c>SaveNow</c>'s return), but its file never lands.
    ///
    /// <para>A null capture is the only failure a test can inject from outside
    /// the service: <c>PoseFile.Save</c> swallows its own IO errors, and the
    /// destination path lives inside a folder the worker creates itself, so
    /// there is nothing to lock or pre-occupy. The service's contract for this
    /// is what matters — one bad entry must not abort the snapshot.</para>
    /// </summary>
    public void FailWriteFor(FakeActor actor) =>
        PoseFiles.CreatePoseFile(actor.Skeletons).Returns((PoseFile)null!);

    /// <summary>
    /// Blocks until the write worker has finished everything it does — folder,
    /// files, prune — or fails the test. Returns immediately when nothing was
    /// dispatched, so it is safe to call unconditionally.
    /// </summary>
    public void WaitForWrite(int timeoutMs = 5000)
    {
        if (!Service.WaitForIdle(TimeSpan.FromMilliseconds(timeoutMs)))
            Assert.Fail($"the auto-save write worker was still running after {timeoutMs} ms");
    }

    private static SkeletonPoseInfo BuildPoseInfo()
    {
        var info = new SkeletonPoseInfo();
        var bone = info.GetPoseInfo("j_kosi", partialId: 0);

        var delta = new Transform
        {
            Position = new Vector3(0f, 0.25f, 0f),
            Rotation = Quaternion.Identity,
            Scale = Vector3.Zero
        };

        bone.SetStackTransform(delta);
        return info;
    }

    public static string Stamp(DateTime utc) =>
        utc.ToString(StampFormat, CultureInfo.InvariantCulture);

    /// <summary>The per-day layout's folder name for a UTC instant — LOCAL
    /// day, exactly the service's own conversion, so expectations follow the
    /// machine's time zone the same way the code under test does.</summary>
    public static string Day(DateTime utc) =>
        utc.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>The per-day layout's file-name time prefix (local).</summary>
    public static string Prefix(DateTime utc) =>
        utc.ToLocalTime().ToString("HH-mm-ss", CultureInfo.InvariantCulture);

    public string DayNow() => Day(NowUtc);

    public string PrefixNow() => Prefix(NowUtc);

    public string SeedSnapshot(string folderName, bool withFile = false)
    {
        var dir = Path.Combine(Root, folderName);
        Directory.CreateDirectory(dir);
        if (withFile)
            File.WriteAllText(Path.Combine(dir, "seed.pose"), "{}");
        return dir;
    }

    /// <summary>Snapshot folder names under the root, newest-name first.</summary>
    public IReadOnlyList<string> SnapshotFolders() =>
        Directory.EnumerateDirectories(Root)
            .Select(Path.GetFileName)
            .Where(name => !string.IsNullOrEmpty(name))
            .Select(name => name!)
            .OrderByDescending(name => name, StringComparer.Ordinal)
            .ToList();

    /// <summary>
    /// File names inside one snapshot folder, ordinal-ascending. Empty when the
    /// folder was never created — call <see cref="WaitForWrite"/> first.
    /// </summary>
    public IReadOnlyList<string> SnapshotFiles(string folderName)
    {
        var dir = Path.Combine(Root, folderName);
        if (!Directory.Exists(dir))
            return Array.Empty<string>();

        return Directory.EnumerateFiles(dir)
            .Select(Path.GetFileName)
            .Select(name => name!)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// Overload-agnostic error assertion: <c>IPluginLog.Error</c> has several
    /// signatures and the service picks whichever the interpolated string binds
    /// to, so match on the method name instead of pinning one overload.
    /// </summary>
    public int ErrorCount => Log.ReceivedCalls()
        .Count(call => call.GetMethodInfo().Name == nameof(IPluginLog.Error));

    /// <summary>
    /// How many actors the framework-thread half actually captured. This is the
    /// number <c>SaveNow</c> returns; it says nothing about what reached disk.
    /// </summary>
    public int CaptureCallCount => CaptureCalls.Count;

    private IReadOnlyList<ICall> CaptureCalls => PoseFiles.ReceivedCalls()
        .Where(call => call.GetMethodInfo().Name == nameof(IPoseFileService.CreatePoseFile))
        .ToList();

    public void Dispose()
    {
        // The worker only touches the temp root, but deleting it out from under
        // a live write would spray unrelated IO errors through the log.
        if (_service is not null)
            _service.WaitForIdle(TimeSpan.FromSeconds(5));

        try
        {
            _service?.Dispose();
        }
        catch
        {
            // Dispose failures are asserted explicitly where they matter.
        }

        try
        {
            if (Directory.Exists(Root))
                Directory.Delete(Root, recursive: true);
        }
        catch
        {
            // A leaked temp folder must never fail an otherwise green test.
        }
    }
}
