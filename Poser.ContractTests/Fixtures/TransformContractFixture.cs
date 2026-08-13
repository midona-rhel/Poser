using System.Numerics;
using Poser.Application.Posing;
using Poser.Application.Scene;
using Poser.Application.Transforms;
using Poser.Domain.Identity;
using Poser.Domain.Posing;
using Poser.Domain.Scene;
using Poser.Domain.Transforms;

namespace Poser.ContractTests.Fixtures;

internal static class TestIds
{
    public static readonly Guid ActorLineage =
        Guid.Parse("11111111-1111-1111-1111-111111111111");

    public static ActorId Actor(uint generation = 0) =>
        new(ActorLineage, generation);

    public static TransformTargetId ActorTarget(uint generation = 0) =>
        TransformTargetId.ForActor(Actor(generation));

    public static TransformTargetId BoneTarget(
        uint actorGeneration = 0,
        uint skeletonGeneration = 0,
        string name = "j_kao",
        int boneIndex = 1)
    {
        var skeleton = new SkeletonId(
            Actor(actorGeneration),
            PoseSlot.Character,
            skeletonGeneration);
        return TransformTargetId.ForBone(new BoneId(
            skeleton,
            PartialId: 0,
            BoneIndex: boneIndex,
            CanonicalName: name));
    }
}

internal static class TestScenes
{
    public static SceneSnapshot ActorScene(ActorId actor) =>
        new(
            Revision: actor.Generation + 1,
            Actors: new[]
            {
                new ActorDescriptor(
                    actor,
                    "Test actor",
                    Array.Empty<SkeletonDescriptor>()),
            },
            Lights: Array.Empty<LightDescriptor>(),
            Cameras: Array.Empty<CameraDescriptor>(),
            Props: Array.Empty<PropDescriptor>());

    public static SceneSnapshot ActorAndBoneScene(ActorId actor, BoneId bone) =>
        ActorAndBonesScene(actor, bone);

    public static SceneSnapshot ActorAndBonesScene(
        ActorId actor,
        params BoneId[] bones) =>
        new(
            Revision: actor.Generation + 1,
            Actors: new[]
            {
                new ActorDescriptor(
                    actor,
                    "Test actor",
                    new[]
                    {
                        new SkeletonDescriptor(
                            bones[0].Skeleton,
                            bones.Select(bone => new BoneDescriptor(
                                bone,
                                bone.CanonicalName,
                                Parent: null)).ToArray()),
                    }),
            },
            Lights: Array.Empty<LightDescriptor>(),
            Cameras: Array.Empty<CameraDescriptor>(),
            Props: Array.Empty<PropDescriptor>());
}

internal sealed class FakeTransformRuntime : ITransformRuntimePort
{
    private readonly Dictionary<TransformTargetId, TransformTargetState> _states = new();

    public List<TransformTargetId> CaptureCalls { get; } = new();
    public List<TransformTargetId> ApplyCalls { get; } = new();
    public List<TransformTargetId> RestoreCalls { get; } = new();
    public Action? DuringApply { get; set; }
    public Action? DuringRestore { get; set; }
    public int? FailApplyCall { get; set; }
    public bool MutateBeforeApplyFailure { get; set; }
    public int? FailCaptureCall { get; set; }
    public HashSet<int> FailRestoreCalls { get; } = new();
    public HashSet<int> MutateBeforeRestoreFailureCalls { get; } = new();
    public Dictionary<int, string> RestoreFailureDetails { get; } = new();
    public TransformPortStatus FailureStatus { get; set; } =
        TransformPortStatus.Rejected;
    public string FailureDetail { get; set; } = "fake runtime failure";
    public string? ApplyFailureDetail { get; set; }
    public string? CaptureFailureDetail { get; set; }
    public string? RestoreFailureDetail { get; set; }

    private int _captureCount;

    public void Seed(TransformTargetState state) =>
        _states[state.Target] = state;

    public TransformTargetState State(TransformTargetId target) =>
        _states[target];

    public TransformPortResult Capture(TransformTargetId target)
    {
        CaptureCalls.Add(target);
        _captureCount++;
        if (FailCaptureCall == _captureCount)
            return TransformPortResult.Fail(
                FailureStatus,
                CaptureFailureDetail ?? FailureDetail);

        return _states.TryGetValue(target, out var state)
            ? TransformPortResult.Ok(state)
            : TransformPortResult.Fail(
                TransformPortStatus.StaleTarget,
                $"Fake state for {target} is absent.");
    }

    public TransformPortResult ApplyAbsolute(
        TransformTargetState baseline,
        PoseTransform desired,
        bool rawBaseline = false)
    {
        ApplyCalls.Add(baseline.Target);
        DuringApply?.Invoke();
        if (FailApplyCall == ApplyCalls.Count)
        {
            if (MutateBeforeApplyFailure)
                _states[baseline.Target] = baseline with { Transform = desired };
            return TransformPortResult.Fail(
                FailureStatus,
                ApplyFailureDetail ?? FailureDetail);
        }

        _states[baseline.Target] = baseline with { Transform = desired };
        return TransformPortResult.Ok();
    }

    public TransformPortResult Restore(TransformTargetState state)
    {
        RestoreCalls.Add(state.Target);
        DuringRestore?.Invoke();
        var call = RestoreCalls.Count;
        if (FailRestoreCalls.Contains(call))
        {
            if (MutateBeforeRestoreFailureCalls.Contains(call))
                _states[state.Target] = state;
            return TransformPortResult.Fail(
                FailureStatus,
                RestoreFailureDetails.GetValueOrDefault(call) ??
                RestoreFailureDetail ??
                FailureDetail);
        }
        _states[state.Target] = state;
        return TransformPortResult.Ok();
    }
}

internal static class TestStates
{
    public static TransformTargetState For(TransformTargetId target) =>
        At(target, 0);

    public static TransformTargetState At(
        TransformTargetId target,
        float positionX,
        bool? hasOverride = null) =>
        new(
            target,
            Translated(positionX),
            new BonePose(),
            HasOverride: hasOverride ?? target.Kind == TransformTargetKind.Actor);

    public static PoseTransform Translated(float x) =>
        PoseTransform.CreateChecked(
            new Vector3(x, 0, 0),
            Quaternion.Identity,
            Vector3.One);
}

internal sealed class TransformApplicationHarness : IDisposable
{
    public TransformApplicationHarness()
    {
        Selection = new Poser.Application.Selection.SelectionSession();
        Scene = new SceneSession(Selection);
        Runtime = new FakeTransformRuntime();
        History = new TransformHistory();
        Gestures = new TransformGestureService(Scene, Runtime, History);
        Commands = new TransformCommandService(Scene, Runtime, History, Gestures);
        PoseEdits = new PoseEditService(Scene, Runtime, History, Gestures);
    }

    public Poser.Application.Selection.SelectionSession Selection { get; }
    public SceneSession Scene { get; }
    public FakeTransformRuntime Runtime { get; }
    public TransformHistory History { get; }
    public TransformGestureService Gestures { get; }
    public TransformCommandService Commands { get; }
    public PoseEditService PoseEdits { get; }

    public void Dispose() => Gestures.Dispose();
}
