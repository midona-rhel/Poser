using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using NSubstitute;
using Poser.Application.Animation;
using Poser.Application.Integration;
using Poser.Application.Lifecycle;
using Poser.Application.Operations;
using Poser.Application.Posing;
using Poser.Application.Presentation;
using Poser.Application.Scene;
using Poser.Application.Transforms;
using Poser.Config;
using Poser.Core;
using Poser.Domain.Animation;
using Poser.Domain.Identity;
using Poser.Entities;
using Poser.Files;
using Poser.Game;
using Poser.Game.Bindings;
using Poser.Game.Posing;
using Poser.Services;

namespace Poser.ContractTests.Fixtures;

/// <summary>Framework-owned session identity seam for import contract tests.</summary>
internal sealed class FakeSessionGenerationSource : ISessionGenerationSource
{
    public SessionGeneration? ActiveSessionGeneration { get; set; }
}

/// <summary>Deterministic framework tick queue used by import tests that need
/// to distinguish accepted/pending from terminal completion.</summary>
internal sealed class FakeFrameworkTicks
{
    private readonly List<(int Delay, Action Callback)> _queued = new();

    public IReadOnlyList<(int Delay, Action Callback)> Queued => _queued;

    public void Enqueue(Action callback, int delayTicks = 0) =>
        _queued.Add((delayTicks, callback));

    public void RunNext()
    {
        if (_queued.Count == 0)
            return;
        var next = _queued[0];
        _queued.RemoveAt(0);
        next.Callback();
    }

    public void RunAt(int index)
    {
        var next = _queued[index];
        _queued.RemoveAt(index);
        next.Callback();
    }
}

internal sealed class PoseImportCaptureHarness : IDisposable
{
    private PoseImportPlan _nextPlan = new();
    private readonly FakeFrameworkTicks _ticks = new();
    private readonly Dictionary<ISkeleton, SkeletonPoseInfo> _poseInfos = new();
    private readonly IAnimationRuntimePort _animationPort;
    private readonly IkBakeCapture _ikBake;
    private readonly PoseExportCapture _exports;
    private readonly ConfigurationService _configuration;
    private readonly Dictionary<ISkeleton, Action<IBone, BonePoseInfo>> _registeredActions = new();
    private readonly IActorManager _actorManager;
    private IActor _currentActor;
    private ISkeleton _currentWeaponSkeleton;
    private int _poseInfoCallCount;
    private int _registerCallCount;

    public PoseImportCaptureHarness()
    {
        Framework = Substitute.For<IFramework>();
        Framework.IsInFrameworkUpdateThread.Returns(true);
        Framework.RunOnTick(
                Arg.Any<Action>(),
                Arg.Any<TimeSpan>(),
                Arg.Any<int>(),
                Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                ThrowDuringSchedule?.Invoke();
                _ticks.Enqueue(call.ArgAt<Action>(0), call.ArgAt<int>(2));
                return Task.CompletedTask;
            });

        Actor = Substitute.For<IActor>();
        Actor.Id.Returns(new EntityId("pose-import-actor"));
        Actor.Name.Returns("Pose import actor");
        Actor.Address.Returns((nint)1);
        Actor.ActorKind.Returns(ActorKind.Player);
        Actor.IsCompanion.Returns(true);
        _currentActor = Actor;

        Skeleton = Substitute.For<ISkeleton>();
        Skeleton.Id.Returns(new EntityId("pose-import-skeleton"));
        Skeleton.Name.Returns("Character");
        Skeleton.Actor.Returns(Actor);
        Skeleton.Slot.Returns(PoseSlot.Character);
        Skeleton.CharacterBaseAddress.Returns((nint)2);
        Skeleton.IsValid.Returns(true);

        Bone = Substitute.For<IBone>();
        Bone.Id.Returns(new EntityId("pose-import-bone"));
        Bone.Name.Returns("j_kao");
        Bone.BoneName.Returns("j_kao");
        Bone.BoneIndex.Returns(1);
        Bone.PartialId.Returns(0);
        Bone.Skeleton.Returns(Skeleton);
        Bone.ParentBone.Returns((IBone?)null);
        FaceBone = Substitute.For<IBone>();
        FaceBone.Id.Returns(new EntityId("pose-import-face-bone"));
        FaceBone.Name.Returns("j_mab_l");
        FaceBone.BoneName.Returns("j_mab_l");
        FaceBone.BoneIndex.Returns(2);
        FaceBone.PartialId.Returns(1);
        FaceBone.Skeleton.Returns(Skeleton);
        FaceBone.ParentBone.Returns(Bone);
        FaceBone.ChildBones.Returns(Array.Empty<IBone>());
        FaceBone.LastRawTransform.Returns(Transform.Identity);
        Bone.ChildBones.Returns(new[] { FaceBone });
        Bone.LastRawTransform.Returns(Transform.Identity);
        Skeleton.RootBone.Returns(Bone);
        Skeleton.Bones.Returns(new[] { Bone, FaceBone });
        Skeleton.GetBone("j_kao").Returns(Bone);

        WeaponSkeleton = Substitute.For<ISkeleton>();
        WeaponSkeleton.Id.Returns(new EntityId("pose-import-weapon-skeleton"));
        WeaponSkeleton.Name.Returns("MainHand");
        WeaponSkeleton.Actor.Returns(Actor);
        WeaponSkeleton.Slot.Returns(PoseSlot.MainHand);
        WeaponSkeleton.CharacterBaseAddress.Returns((nint)3);
        WeaponSkeleton.IsValid.Returns(true);
        WeaponBone = Substitute.For<IBone>();
        WeaponBone.Id.Returns(new EntityId("pose-import-weapon-bone"));
        WeaponBone.Name.Returns("n_hara");
        WeaponBone.BoneName.Returns("n_hara");
        WeaponBone.BoneIndex.Returns(1);
        WeaponBone.PartialId.Returns(0);
        WeaponBone.Skeleton.Returns(WeaponSkeleton);
        WeaponBone.ParentBone.Returns((IBone?)null);
        WeaponBone.ChildBones.Returns(Array.Empty<IBone>());
        WeaponBone.LastRawTransform.Returns(Transform.Identity);
        WeaponSkeleton.RootBone.Returns(WeaponBone);
        WeaponSkeleton.Bones.Returns(new[] { WeaponBone });
        _currentWeaponSkeleton = WeaponSkeleton;

        Skeletons = Substitute.For<ISkeletonService>();
        Skeletons.GetSkeletons(Actor).Returns(_ =>
            new[] { Skeleton, _currentWeaponSkeleton });
        Skeletons.GetSkeleton(Actor).Returns(Skeleton);
        Skeletons.GetSkeleton(Actor, PoseSlot.Character).Returns(Skeleton);
        Skeletons.GetSkeleton(Actor, PoseSlot.MainHand).Returns(_ => _currentWeaponSkeleton);

        _actorManager = Substitute.For<IActorManager>();
        _actorManager.Actors.Returns(_ => new[] { _currentActor });
        _actorManager.AuxiliaryActors.Returns(Array.Empty<IActor>());
        var spawn = Substitute.For<IActorSpawnService>();
        spawn.IsVisible(Actor).Returns(true);
        var lighting = Substitute.For<ILightingService>();
        lighting.Lights.Returns(Array.Empty<ILight>());
        var cameras = Substitute.For<IVirtualCameraService>();
        cameras.Cameras.Returns(Array.Empty<IVirtualCamera>());
        var props = (PropSpawnService)RuntimeHelpers.GetUninitializedObject(
            typeof(PropSpawnService));
        typeof(PropSpawnService).GetField(
            "_props",
            BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(
                props,
                new List<PropHandle>());
        Bindings = new StableBindingRegistry(
            _actorManager, Skeletons, spawn, lighting, cameras, props);

        Selection = new Poser.Application.Selection.SelectionSession();
        Scene = new SceneSession(Selection);
        var candidate = Bindings.RefreshCandidate();
        var admitted = Scene.TryRefresh(candidate.Snapshot with { Revision = 1 });
        if (!admitted.Accepted)
            throw new InvalidOperationException(
                $"Pose import test scene was not admitted: {admitted.Outcome}: {admitted.Detail}");
        Bindings.CommitCandidate(candidate, Scene.Snapshot);
        ActorId = candidate.Snapshot.Actors[0].Id;
        var boneId = candidate.Snapshot.Actors[0].Skeletons[0].Bones[0].Id;
        InitialBoneState = TestStates.At(
            TransformTargetId.ForBone(boneId),
            0,
            hasOverride: true);

        Runtime = new FakeTransformRuntime();
        Runtime.Seed(InitialBoneState);
        foreach (var descriptor in candidate.Snapshot.Actors[0].Skeletons
                     .SelectMany(value => value.Bones)
                     .Where(value => value.Id != boneId))
        {
            Runtime.Seed(TestStates.At(
                TransformTargetId.ForBone(descriptor.Id),
                0,
                hasOverride: false));
        }
        Runtime.Seed(TestStates.At(
            TransformTargetId.ForActor(ActorId),
            0,
            hasOverride: true));
        History = new TransformHistory();
        Gestures = new TransformGestureService(Scene, Runtime, History);
        Posing = Substitute.For<IBonePosingService>();
        _poseInfos[Skeleton] = new SkeletonPoseInfo();
        _poseInfos[WeaponSkeleton] = new SkeletonPoseInfo();
        Posing.GetPoseInfo(Arg.Any<ISkeleton>()).Returns(call =>
        {
            var skeleton = call.Arg<ISkeleton>();
            if (ReferenceEquals(skeleton, Skeleton))
                ThrowDuringReset?.Invoke();
            _poseInfoCallCount++;
            if (ThrowOnPoseInfoCall == _poseInfoCallCount)
                throw new InvalidOperationException("pose info read exploded");
            if (!_poseInfos.TryGetValue(skeleton, out var info))
                _poseInfos[skeleton] = info = new SkeletonPoseInfo();
            return info;
        });
        Posing.When(service => service.RegisterTransitiveAction(
                Arg.Any<ISkeleton>(),
                Arg.Any<Action<IBone, BonePoseInfo>>()))
            .Do(call =>
            {
                ThrowDuringRegister?.Invoke();
                _registerCallCount++;
                if (ThrowOnRegisterCall == _registerCallCount)
                    throw new InvalidOperationException("register exploded");
                _registeredActions[call.ArgAt<ISkeleton>(0)] =
                    call.ArgAt<Action<IBone, BonePoseInfo>>(1);
            });
        PoseFiles = Substitute.For<IPoseFileService>();
        PoseFiles.BuildImportPlan(
                Arg.Any<IReadOnlyList<ISkeleton>>(),
                Arg.Any<PoseFile>(),
                Arg.Any<PoseImportOptions>())
            .Returns(_ =>
            {
                ThrowDuringBuildImportPlan?.Invoke();
                return _nextPlan;
            });
        PoseFiles.CreatePoseFile(Arg.Any<IReadOnlyList<ISkeleton>>())
            .Returns(_ =>
            {
                ThrowDuringCreatePoseFile?.Invoke();
                return new PoseFile();
            });
        var sessions = new FakeSessionGenerationSource
        {
            ActiveSessionGeneration = SessionGeneration.New(),
        };
        var log = Substitute.For<IPluginLog>();
        _ikBake = new IkBakeCapture(
            Framework,
            Bindings,
            Posing,
            Skeletons,
            PoseFiles,
            Runtime,
            History,
            Gestures,
            log);
        Imports = new PoseImportCapture(
            Framework,
            Scene,
            sessions,
            Bindings,
            Posing,
            Runtime,
            History,
            Gestures,
            _ikBake,
            PoseFiles,
            Skeletons,
            log);

        _animationPort = Substitute.For<IAnimationRuntimePort>();
        _animationPort.IsSupported(ActorId).Returns(true);
        _animationPort.SetOverallSpeed(ActorId, Arg.Any<float>())
            .Returns(AnimationPortResult.Ok());
        _animationPort.ClearOverallSpeed(ActorId)
            .Returns(AnimationPortResult.Ok());
        _animationPort.RewindPausedControls(ActorId)
            .Returns(_ =>
            {
                RewindCalls++;
                return AnimationPortResult.Ok();
            });
        var animation = new AnimationSession(_animationPort);
        var presentation = new ActorPresentationSession(
            Substitute.For<IPresentationRuntimePort>());
        var integration = new ActorIntegrationSession(
            Substitute.For<IIntegrationRuntimePort>(),
            Substitute.For<IMcdfFileBoundary>());
        var pluginInterface = Substitute.For<IDalamudPluginInterface>();
        pluginInterface.GetPluginConfig().Returns((object?)null);
        _configuration = new ConfigurationService(pluginInterface);
        _exports = new PoseExportCapture(Framework, Posing, PoseFiles, log);
        var edits = new PoseEditService(Scene, Runtime, History, Gestures);
        Facade = new CleanPoseFacade(
            Bindings,
            edits,
            new PoseTransferService(edits),
            Imports,
            _exports,
            _configuration,
            PoseFiles,
            Posing,
            Skeletons,
            Substitute.For<IExpressionService>(),
            Substitute.For<IGazeService>(),
            animation,
            presentation,
            integration,
            Framework,
            log);
    }

    public IFramework Framework { get; }
    public IActor Actor { get; }
    public ISkeleton Skeleton { get; }
    public IBone Bone { get; }
    public IBone FaceBone { get; }
    public ISkeleton WeaponSkeleton { get; }
    public IBone WeaponBone { get; }
    public ActorId ActorId { get; }
    public ISkeletonService Skeletons { get; }
    public StableBindingRegistry Bindings { get; }
    public Poser.Application.Selection.SelectionSession Selection { get; }
    public SceneSession Scene { get; }
    public FakeTransformRuntime Runtime { get; }
    public TransformHistory History { get; }
    public TransformGestureService Gestures { get; }
    public IBonePosingService Posing { get; }
    public IPoseFileService PoseFiles { get; }
    public PoseImportCapture Imports { get; }
    public CleanPoseFacade Facade { get; }
    public TransformTargetState InitialBoneState { get; }
    public Action? ThrowDuringReset { get; set; }
    public Action? ThrowDuringRegister { get; set; }
    public Action? ThrowDuringSchedule { get; set; }
    public int? ThrowOnPoseInfoCall { get; set; }
    public int? ThrowOnRegisterCall { get; set; }
    public Action? ThrowDuringBuildImportPlan { get; set; }
    public Action? ThrowDuringCreatePoseFile { get; set; }
    public int RewindCalls { get; private set; }
    public int BeginCalls => Runtime.ApplyCalls.Count;
    public int RestoreArmCalls { get; private set; }

    public PoseEditResult ArmModelImport(
        float positionX,
        Action<OperationReceipt> onReceipt)
    {
        _nextPlan = new PoseImportPlan
        {
            ModelActor = Actor,
            ModelTransform = new Transform
            {
                Position = new Vector3(positionX, 0, 0),
                Rotation = Quaternion.Identity,
                Scale = Vector3.One,
            },
            FileBoneCount = 1,
        };
        var result = Facade.ImportPose(
            Actor,
            new PoseFile(),
            new PoseImportOptions { ApplyModelTransform = true },
            "model import",
            onReceipt);
        RestoreArmCalls = _animationPort.ReceivedCalls()
            .Count(call => call.GetMethodInfo().Name is
                nameof(IAnimationRuntimePort.ClearOverallSpeed));
        return result;
    }

    public GestureResult BeginResetImport(Action<OperationReceipt> onReceipt)
    {
        var plan = new PoseImportPlan { FileBoneCount = 1 };
        plan.Resets.Add(Bone);
        return Imports.Begin(plan, "reset import", onReceipt: onReceipt);
    }

    public GestureResult BeginModelImport(
        float positionX,
        Action<OperationReceipt> onReceipt)
    {
        var plan = CreateModelPlan(positionX);
        return Imports.Begin(plan, "model import", onReceipt: onReceipt);
    }

    public PoseImportPlan CreateModelPlan(float positionX) => new()
    {
        ModelActor = Actor,
        ModelTransform = new Transform
        {
            Position = new Vector3(positionX, 0, 0),
            Rotation = Quaternion.Identity,
            Scale = Vector3.One,
        },
        FileBoneCount = 1,
    };

    public IActor ReplaceActorObjectAtSameLogicalIdentity()
    {
        var replacement = Substitute.For<IActor>();
        replacement.Id.Returns(new EntityId("pose-import-actor"));
        replacement.Name.Returns("Replacement actor object");
        replacement.Address.Returns((nint)1);
        replacement.ActorKind.Returns(ActorKind.Player);
        replacement.IsCompanion.Returns(true);
        Skeletons.GetSkeletons(replacement).Returns(Array.Empty<ISkeleton>());
        _currentActor = replacement;
        var candidate = Bindings.RefreshCandidate();
        var admitted = Scene.TryRefresh(candidate.Snapshot with
        {
            Revision = Scene.Revision + 1,
        });
        if (!admitted.Accepted)
            throw new InvalidOperationException(
                $"Replacement scene was not admitted: {admitted.Detail}");
        Bindings.CommitCandidate(candidate, Scene.Snapshot);
        return replacement;
    }

    public void ReplaceWeaponSlot()
    {
        var replacementSkeleton = Substitute.For<ISkeleton>();
        replacementSkeleton.Id.Returns(new EntityId("pose-import-weapon-skeleton-replacement"));
        replacementSkeleton.Name.Returns("MainHand replacement");
        replacementSkeleton.Actor.Returns(Actor);
        replacementSkeleton.Slot.Returns(PoseSlot.MainHand);
        replacementSkeleton.CharacterBaseAddress.Returns((nint)4);
        replacementSkeleton.IsValid.Returns(true);
        var replacementBone = Substitute.For<IBone>();
        replacementBone.Id.Returns(new EntityId("pose-import-weapon-bone-replacement"));
        replacementBone.Name.Returns("n_hara");
        replacementBone.BoneName.Returns("n_hara");
        replacementBone.BoneIndex.Returns(1);
        replacementBone.PartialId.Returns(0);
        replacementBone.Skeleton.Returns(replacementSkeleton);
        replacementBone.ParentBone.Returns((IBone?)null);
        replacementBone.ChildBones.Returns(Array.Empty<IBone>());
        replacementBone.LastRawTransform.Returns(Transform.Identity);
        replacementSkeleton.RootBone.Returns(replacementBone);
        replacementSkeleton.Bones.Returns(new[] { replacementBone });
        _currentWeaponSkeleton = replacementSkeleton;

        var candidate = Bindings.RefreshCandidate();
        var admitted = Scene.TryRefresh(candidate.Snapshot with
        {
            Revision = Scene.Revision + 1,
        });
        if (!admitted.Accepted)
            throw new InvalidOperationException(
                $"Weapon replacement scene was not admitted: {admitted.Detail}");
        Bindings.CommitCandidate(candidate, Scene.Snapshot);
    }

    public GestureResult BeginWriteImport(Action<OperationReceipt> onReceipt)
    {
        var desired = Transform.Identity;
        desired.Position = new Vector3(1, 0, 0);
        var plan = new PoseImportPlan { FileBoneCount = 1 };
        plan.Writes.Add((Bone, desired, TransformComponents.All));
        return Imports.Begin(plan, "write import", onReceipt: onReceipt);
    }

    public GestureResult BeginFaceExpressionImport(Action<OperationReceipt> onReceipt)
    {
        var desired = Transform.Identity;
        desired.Position = new Vector3(1, 0, 0);
        var plan = new PoseImportPlan { FileBoneCount = 1 };
        plan.Writes.Add((FaceBone, desired, TransformComponents.All));
        return Imports.Begin(
            plan,
            "face expression import",
            expression: true,
            onReceipt: onReceipt);
    }

    public GestureResult BeginHeadRestoreImport(Action<OperationReceipt> onReceipt)
    {
        var desired = Transform.Identity;
        desired.Position = new Vector3(1, 0, 0);
        var plan = new PoseImportPlan { FileBoneCount = 2 };
        plan.Writes.Add((Bone, desired, TransformComponents.All));
        plan.Writes.Add((FaceBone, desired, TransformComponents.All));
        return Imports.Begin(
            plan,
            "head restore import",
            expression: true,
            onReceipt: onReceipt);
    }

    public void SeedHeadInteractiveStack()
    {
        var delta = Transform.Identity;
        delta.Position = new Vector3(0.1f, 0, 0);
        _poseInfos[Skeleton]
            .GetPoseInfo("j_kao", 0)
            .RestoreInteractiveStacks(new[]
            {
                new BonePoseTransformInfo(TransformComponents.All, delta),
            });
    }

    public void UseWeaponFlattenPlan()
    {
        var desired = Transform.Identity;
        desired.Position = new Vector3(2, 0, 0);
        _nextPlan = new PoseImportPlan { FileBoneCount = 1 };
        _nextPlan.Resets.Add(WeaponBone);
        _nextPlan.Writes.Add((WeaponBone, desired, TransformComponents.All));
    }

    public void ReachFlattenSetup()
    {
        FireCharacterNativeActions();
        EndRegisteredNativeBatch();
        RunNextDelay(4);
        FireCharacterNativeActions();
        EndRegisteredNativeBatch();
        RunNextDelay(0);
        UseWeaponFlattenPlan();
    }

    public void FireRegisteredNativeAction() =>
        FireCharacterNativeActions();

    public void FireCharacterNativeActions()
    {
        if (!_registeredActions.TryGetValue(Skeleton, out var action))
            return;
        action(Bone, _poseInfos[Skeleton].GetPoseInfo("j_kao", 0));
        action(FaceBone, _poseInfos[Skeleton].GetPoseInfo("j_mab_l", 1));
    }

    public void FireWeaponNativeAction()
    {
        if (_registeredActions.TryGetValue(WeaponSkeleton, out var action))
            action(WeaponBone, _poseInfos[WeaponSkeleton].GetPoseInfo("n_hara", 0));
    }

    public int InteractiveStackCount =>
        _poseInfos[Skeleton].GetPoseInfo("j_kao", 0).Stacks.Count;

    public void EndRegisteredNativeBatch(bool executed = true) =>
        Posing.TransitiveActionsEnded +=
            Raise.Event<Action<TransitiveActionOutcome>>(
                new TransitiveActionOutcome(Skeleton, executed));

    public void EndWeaponNativeBatch(bool executed = true) =>
        Posing.TransitiveActionsEnded +=
            Raise.Event<Action<TransitiveActionOutcome>>(
                new TransitiveActionOutcome(WeaponSkeleton, executed));

    public void RunNextDelay(int delay)
    {
        var queued = _ticks.Queued
            .Select((item, queuedIndex) => (item, queuedIndex))
            .First(item => item.item.Delay == delay)
            .queuedIndex;
        _ticks.RunAt(queued);
    }

    public void RunIfQueued(int delay)
    {
        var queued = _ticks.Queued
            .Select((item, queuedIndex) => (item, queuedIndex))
            .FirstOrDefault(item => item.item.Delay == delay);
        if (queued.item.Callback != null)
            _ticks.RunAt(queued.queuedIndex);
    }

    public void RunQueued(int index)
    {
        var delayed = _ticks.Queued
            .Select((item, queuedIndex) => (item, queuedIndex))
            .First(item => item.item.Delay == 4)
            .queuedIndex;
        _ticks.RunAt(delayed);
        RestoreArmCalls = _animationPort.ReceivedCalls()
            .Count(call => call.GetMethodInfo().Name is
                nameof(IAnimationRuntimePort.ClearOverallSpeed));
    }

    public void Dispose()
    {
        Imports.Dispose();
        _exports.Dispose();
        _ikBake.Dispose();
        Gestures.Dispose();
    }
}
