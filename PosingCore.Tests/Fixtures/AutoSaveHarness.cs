using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using NSubstitute;
using NSubstitute.Core;
using Poser.Config;
using Poser.Core;
using Poser.Entities;
using Poser.Files;
using Poser.Services;

namespace Poser.Tests.Fixtures;

/// <summary>
/// One actor wired into the harness together with the exact skeleton list
/// instance <see cref="ISkeletonService.GetSkeletons"/> hands back for it, so a
/// test can assert that the very same list reached
/// <see cref="IPoseFileService.ExportPose"/>.
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

    public AutoSaveConfiguration Settings => Configuration.Config.AutoSave;

    public AutoSaveHarness()
    {
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
        PoseFiles
            .ExportPose(Arg.Any<IReadOnlyList<ISkeleton>>(), Arg.Any<string>())
            .Returns(true);

        Configuration = new ConfigurationService(Substitute.For<IDalamudPluginInterface>());
    }

    /// <summary>
    /// Constructed on first use so a test can seed config and actors first.
    /// </summary>
    public AutoSaveService Service => _service ??= new AutoSaveService(
        Log,
        (IFramework?)null,
        EventBus,
        GPose,
        () => ActorManager,
        () => Skeletons,
        () => BonePosing,
        () => PoseFiles,
        Configuration,
        Root,
        () => NowUtc);

    /// <summary>
    /// Adds an actor whose single Character skeleton either carries a
    /// user-authored (unnamed-layer) stack or only a service-owned named layer.
    /// The named-layer case is what proves the predicate discriminates on
    /// <c>Layer == null</c> rather than merely on "has any stack".
    /// </summary>
    public FakeActor AddActor(string name, bool authored = true)
    {
        var actor = Substitute.For<IActor>();
        actor.Name.Returns(name);

        var skeleton = Substitute.For<ISkeleton>();
        var slots = new List<ISkeleton> { skeleton };
        Skeletons.GetSkeletons(actor).Returns(slots);
        BonePosing.GetPoseInfo(skeleton).Returns(BuildPoseInfo(authored));

        _actors.Add(actor);
        return new FakeActor(actor, slots);
    }

    /// <summary>
    /// Advances the injected clock and drives one tick with the same instant, so
    /// the interval decision and the snapshot folder name never disagree.
    /// </summary>
    public void TickAt(DateTime nowUtc)
    {
        NowUtc = nowUtc;
        Service.Tick(nowUtc);
    }

    /// <summary>Actor whose skeleton lookup blows up during the scan.</summary>
    public IActor AddActorThatThrows(string name, Exception failure)
    {
        var actor = Substitute.For<IActor>();
        actor.Name.Returns(name);
        Skeletons.GetSkeletons(actor).Returns(_ => throw failure);
        _actors.Add(actor);
        return actor;
    }

    public void FailExportFor(FakeActor actor) =>
        PoseFiles.ExportPose(actor.Skeletons, Arg.Any<string>()).Returns(false);

    private static SkeletonPoseInfo BuildPoseInfo(bool authored)
    {
        var info = new SkeletonPoseInfo();
        var bone = info.GetPoseInfo("j_kosi", partialId: 0);

        var delta = new Transform
        {
            Position = new Vector3(0f, 0.25f, 0f),
            Rotation = Quaternion.Identity,
            Scale = Vector3.Zero
        };

        if (authored)
            bone.SetStackTransform(delta);
        else
            bone.SetLayerTransform("expression", delta, TransformComponents.Rotation);

        return info;
    }

    public static string Stamp(DateTime utc) =>
        utc.ToString(StampFormat, CultureInfo.InvariantCulture);

    public string StampNow() => Stamp(NowUtc);

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
    /// Overload-agnostic error assertion: <c>IPluginLog.Error</c> has several
    /// signatures and the service picks whichever the interpolated string binds
    /// to, so match on the method name instead of pinning one overload.
    /// </summary>
    public int ErrorCount => Log.ReceivedCalls()
        .Count(call => call.GetMethodInfo().Name == nameof(IPluginLog.Error));

    public int ExportCallCount => PoseFiles.ReceivedCalls()
        .Count(call => call.GetMethodInfo().Name == nameof(IPoseFileService.ExportPose));

    /// <summary>Paths passed to every ExportPose call, in call order.</summary>
    public IReadOnlyList<string> ExportedPaths => PoseFiles.ReceivedCalls()
        .Where(call => call.GetMethodInfo().Name == nameof(IPoseFileService.ExportPose))
        .Select(call => (string)call.GetArguments()[1]!)
        .ToList();

    /// <summary>File names (with extension) passed to every ExportPose call.</summary>
    public IReadOnlyList<string> ExportedFileNames =>
        ExportedPaths.Select(Path.GetFileName).Select(name => name!).ToList();

    public void Dispose()
    {
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
