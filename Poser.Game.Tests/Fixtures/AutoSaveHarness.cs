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
using Poser.Domain.Posing;
using Poser.Domain.Transforms;
using Poser.Documents.AutoSave;
using Poser.Documents.Config;
using Poser.Documents.Files;
using Poser.Application.AutoSave;
using Poser.Application.Events;
using Poser.Application.Lifecycle;
using Poser.Application.Settings;
using Poser.Game.Entities;
using Poser.Game.Services;

namespace Poser.Game.Tests.Fixtures;

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
/// Builds autosave tests with substitute game services, an in-memory configuration,
/// a controllable clock, and a temporary output directory.
/// Tests call Tick directly. Capture runs on the calling thread; the default
/// dispatcher writes files in the background. Call WaitForWrite before checking files.
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

    /// <summary>Starts background work; replace this to control when writes run.</summary>
    public Func<Action, bool> Dispatch { get; set; } = work =>
    {
        _ = Task.Run(work);
        return true;
    };

    public AutoSaveConfiguration Settings => Configuration.Config.AutoSave;

    public AutoSaveHarness()
    {
        // Keep test files outside the repository to avoid interference from folder syncing.
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
        // Return real pose documents so the background writer exercises serialization.
        PoseFiles
            .CreatePoseFile(Arg.Any<IReadOnlyList<ISkeleton>>())
            .Returns(_ => NewPoseFile());

        Configuration = new ConfigurationService(new Poser.Application.Tests.Fixtures.MemoryConfigurationPersistence());
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
    /// A two-bone pose for tests that write and read actual pose files.
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
    /// Sets the clock and ticks the service at the same instant, then waits for
    /// background writes so the next test action starts with an idle writer.
    /// </summary>
    public void TickAt(DateTime nowUtc)
    {
        NowUtc = nowUtc;
        Service.Tick(nowUtc, GPose.IsGPosing);
        WaitForWrite();
    }

    /// <summary>
    /// Returns a null pose for one actor. The capture port includes that entry,
    /// allowing tests to exercise the writer's handling of an invalid document.
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
        // Wait for writes before deleting their output directory.
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
