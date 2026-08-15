using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using Poser.Application.Scene;
using Poser.Core;
using Poser.Domain.Companions;
using Poser.Domain.Identity;
using Poser.Domain.Scene;
using Poser.Entities;
using Poser.Game;
using Poser.Game.Animation;
using Poser.Game.Bindings;
using Poser.Game.Scene;
using Poser.Services;

namespace Poser.Game.Tests;

public sealed class SceneProducerIntegrationTests
{
    [Fact]
    public void Binding_registry_exposes_candidate_discovery_without_snapshot_authority()
    {
        Assert.Null(typeof(StableBindingRegistry).GetProperty("CurrentSnapshot"));
        Assert.Null(typeof(StableBindingRegistry).GetField(
            "_revision",
            BindingFlags.Instance | BindingFlags.NonPublic));
        Assert.NotNull(typeof(StableBindingRegistry).GetMethod("RefreshCandidate"));
    }

    [Fact]
    public void Rejected_candidate_keeps_old_native_binding_until_accepted()
    {
        var oldActor = new ActorBase(new EntityId("same-actor"), "Old", (nint)1, ActorKind.Companion);
        var newActor = new ActorBase(new EntityId("same-actor"), "New", (nint)2, ActorKind.Companion);
        var actors = new TestActorManager(oldActor);
        var registry = NewRegistry(actors);
        var session = new SceneSession(new Poser.Application.Selection.SelectionSession());

        var oldCandidate = registry.RefreshCandidate();
        Admit(session, oldCandidate.Snapshot);
        registry.CommitCandidate(oldCandidate, session.Snapshot);
        var oldId = oldCandidate.Snapshot.Actors[0].Id;

        actors.Current = newActor;
        var rejectedCandidate = registry.RefreshCandidate();
        var newId = rejectedCandidate.Snapshot.Actors[0].Id;
        Assert.NotEqual(oldId, newId);
        Assert.Same(oldActor, registry.Resolve(oldId).Value);
        Assert.Equal(BindingStatus.StaleTarget, registry.Resolve(newId).Status);

        var rejected = session.TryRefresh(rejectedCandidate.Snapshot);
        Assert.True(rejected.Rejected);
        registry.AbortCandidate(rejectedCandidate);
        Assert.Same(oldActor, registry.Resolve(oldId).Value);
        Assert.Equal(BindingStatus.StaleTarget, registry.Resolve(newId).Status);

        var acceptedCandidate = registry.RefreshCandidate();
        var accepted = session.TryRefresh(
            CleanSceneLifecycle.CreateAdmissionCandidate(
                acceptedCandidate.Snapshot,
                session.Snapshot));
        Assert.True(accepted.Accepted);
        registry.CommitCandidate(acceptedCandidate, session.Snapshot);

        Assert.Same(newActor, registry.Resolve(acceptedCandidate.Snapshot.Actors[0].Id).Value);
        Assert.Equal(BindingStatus.StaleTarget, registry.Resolve(oldId).Status);
    }

    [Fact]
    public void No_change_candidate_can_commit_only_matching_exact_ids()
    {
        var actor = new ActorBase(new EntityId("same-actor"), "Actor", (nint)1, ActorKind.Companion);
        var actors = new TestActorManager(actor);
        var registry = NewRegistry(actors);
        var session = new SceneSession(new Poser.Application.Selection.SelectionSession());

        var first = registry.RefreshCandidate();
        Admit(session, first.Snapshot);
        registry.CommitCandidate(first, session.Snapshot);
        var id = first.Snapshot.Actors[0].Id;

        var replay = registry.RefreshCandidate();
        var result = session.TryRefresh(
            CleanSceneLifecycle.CreateAdmissionCandidate(
                replay.Snapshot,
                session.Snapshot));
        Assert.Equal(SceneRefreshOutcome.NoChange, result.Outcome);
        registry.CommitCandidate(replay, session.Snapshot);

        Assert.Same(actor, registry.Resolve(id).Value);
    }

    [Fact]
    public void Producer_failure_and_reentrant_refresh_do_not_strand_a_candidate()
    {
        var actor = new ActorBase(new EntityId("same-actor"), "Actor", (nint)1, ActorKind.Companion);
        var actors = new TestActorManager(actor);
        var registry = NewRegistry(actors);

        actors.ThrowOnRead = true;
        Assert.Throws<InvalidOperationException>(() => registry.RefreshCandidate());
        actors.ThrowOnRead = false;

        var staged = registry.RefreshCandidate();
        Assert.Throws<InvalidOperationException>(() => registry.RefreshCandidate());
        registry.AbortCandidate(staged);

        var retry = registry.RefreshCandidate();
        registry.AbortCandidate(retry);
    }

    /// <summary>
    /// The CharaView pose preview's body appears at object index 441 and is
    /// bound so imports can reach it, WITHOUT a scene descriptor. The scene
    /// signature the refresh coalesces on therefore cannot see it arrive — so
    /// coalescing on that signature alone aborts the very candidate that
    /// carries the preview's bindings, <c>GetActorId</c> answers null for the
    /// preview body forever, and every pose stated against it is dropped in
    /// silence behind a perfectly good render. The auxiliary half of the
    /// candidate is the second signature that case needs.
    /// </summary>
    [Fact]
    public void An_auxiliary_body_publishes_its_bindings_under_an_unmoved_scene()
    {
        var actor = new ActorBase(
            new EntityId("scene-actor"), "Actor", (nint)1, ActorKind.Companion);
        var actors = new TestActorManager(actor);
        var registry = NewRegistry(actors);
        var session = new SceneSession(new Poser.Application.Selection.SelectionSession());

        var first = registry.RefreshCandidate();
        Admit(session, first.Snapshot);
        registry.CommitCandidate(first, session.Snapshot);
        Assert.Single(first.Snapshot.Actors);

        var preview = new ActorBase(
            new EntityId("actor_aux_441"), "Preview", (nint)441, ActorKind.Preview);
        actors.Auxiliary = [preview];

        var staged = registry.RefreshCandidate();
        // The scene is blind to it, by design and forever.
        Assert.Single(staged.Snapshot.Actors);
        Assert.True(CleanSceneLifecycle.CanonicalSignature(staged.Snapshot)
            .ContentEquals(CleanSceneLifecycle.CanonicalSignature(first.Snapshot)));
        // The candidate is not: this is what makes it publishable.
        Assert.True(registry.AuxiliaryBindingsChanged(staged));

        var admitted = session.TryRefresh(
            CleanSceneLifecycle.CreateAdmissionCandidate(
                staged.Snapshot, session.Snapshot));
        Assert.Equal(SceneRefreshOutcome.NoChange, admitted.Outcome);
        registry.CommitCandidate(staged, session.Snapshot);

        Assert.NotNull(registry.GetActorId(preview));
        Assert.Same(preview, registry.Resolve(registry.GetActorId(preview)!.Value).Value);
        Assert.Single(session.Snapshot.Actors);

        // An unmoved preview body coalesces exactly like an unmoved scene.
        var replay = registry.RefreshCandidate();
        Assert.False(registry.AuxiliaryBindingsChanged(replay));
        registry.AbortCandidate(replay);

        // …and its departure is a change again, so the stale binding goes.
        actors.Auxiliary = [];
        var withdrawn = registry.RefreshCandidate();
        Assert.True(registry.AuxiliaryBindingsChanged(withdrawn));
        registry.AbortCandidate(withdrawn);
    }

    private static StableBindingRegistry NewRegistry(TestActorManager actors) =>
        new(
            actors,
            new TestSkeletonService(),
            new TestActorSpawnService(),
            new TestLightingService(),
            new TestCameraService(),
            EmptyProps(),
            EmptyOverlays(),
            EmptyWorldObjects());

    private static void Admit(SceneSession session, SceneSnapshot candidate) =>
        Assert.True(session.TryRefresh(
            CleanSceneLifecycle.CreateAdmissionCandidate(
                candidate,
                session.Snapshot)).Accepted);

    private static PropSpawnService EmptyProps()
    {
        var props = (PropSpawnService)RuntimeHelpers.GetUninitializedObject(
            typeof(PropSpawnService));
        typeof(PropSpawnService).GetField(
            "_props",
            BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(
                props,
                new List<PropHandle>());
        return props;
    }

    /// <summary>An overlay service with no nodes and no live port: the
    /// registry only ever reads its list.</summary>
    private static Poser.Game.Overlays.OverlayNodeService EmptyOverlays()
    {
        var overlays = (Poser.Game.Overlays.OverlayNodeService)
            RuntimeHelpers.GetUninitializedObject(
                typeof(Poser.Game.Overlays.OverlayNodeService));
        typeof(Poser.Game.Overlays.OverlayNodeService).GetField(
            "_nodes",
            BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(
                overlays,
                new List<Poser.Game.Overlays.OverlayNodeHandle>());
        return overlays;
    }

    /// <summary>A world-object service with no claims and no live port: the
    /// registry only ever reads its list.</summary>
    private static Poser.Game.WorldObjects.WorldObjectService EmptyWorldObjects()
    {
        var worldObjects = (Poser.Game.WorldObjects.WorldObjectService)
            RuntimeHelpers.GetUninitializedObject(
                typeof(Poser.Game.WorldObjects.WorldObjectService));
        typeof(Poser.Game.WorldObjects.WorldObjectService).GetField(
            "_adopted",
            BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(
                worldObjects,
                new List<Poser.Game.WorldObjects.AdoptedWorldObject>());
        return worldObjects;
    }

    private sealed class TestActorManager : IActorManager
    {
        public TestActorManager(IActor actor) => Current = actor;
        public IActor Current { get; set; }
        public bool ThrowOnRead { get; set; }
        public IReadOnlyList<IActor> Actors => ThrowOnRead
            ? throw new InvalidOperationException("producer failure")
            : [Current];
        public IReadOnlyList<IActor> Auxiliary { get; set; } = Array.Empty<IActor>();
        public IReadOnlyList<IActor> AuxiliaryActors => Auxiliary;
        public void Dispose() { }
        public void RegisterAuxiliary(ushort objectIndex, ActorKind kind) { }
        public void UnregisterAuxiliary(ushort objectIndex) { }
        public void RefreshActors() { }
        public IActor? GetGPoseTarget() => Current;
        public void SetGPoseTarget(IActor actor) { }
    }

    private sealed class TestSkeletonService : ISkeletonService
    {
        public void Dispose() { }
        public ISkeleton? GetSkeleton(IActor actor) => null;
        public ISkeleton? GetSkeleton(IActor actor, PoseSlot slot) => null;
        public IReadOnlyList<ISkeleton> GetSkeletons(IActor actor) => Array.Empty<ISkeleton>();
        public void RefreshSkeleton(IActor actor) { }
        public void ClearAll() { }
    }

    private sealed class TestActorSpawnService : IActorSpawnService
    {
        public void Dispose() { }
        public IActor? SpawnNewActor(bool reserveCompanionSlot) => null;
        public IActor? CloneActor(IActor source) => null;
        public IActor? SpawnCatalogActor(SpawnCatalogEntry entry) => null;
        public int GetModelCharaId(IActor actor) => 0;
        public void SetModelCharaId(IActor actor, int modelCharaId) { }
        public CompanionKind? GetSpawnedKind(IActor actor) => null;
        public bool DestroyActor(IActor actor) => false;
        public void SetVisibility(IActor actor, bool visible) { }
        public bool IsVisible(IActor actor) => true;
        public bool IsSpawnedActor(IActor actor) => false;
        public bool SetCompanion(IActor owner, CompanionAttachment? container) => false;
        public void DestroyCompanion(IActor owner) { }
        public CompanionAttachment? GetCompanionInfo(IActor owner) => null;

        public IActor? GetCompanionActor(IActor owner) => null;
        public bool HasCompanionSlot(IActor actor) => false;
    }

    private sealed class TestLightingService : ILightingService
    {
        public bool IsAvailable => false;
        public IReadOnlyList<ILight> Lights => Array.Empty<ILight>();
        public IReadOnlyList<GoboEntry> Gobos => Array.Empty<GoboEntry>();
        public void Dispose() { }
        public ILight? SpawnLight(LightKind kind) => null;
        public ILight? CloneLight(ILight source) => null;
        public void DestroyLight(ILight light) { }
        public void DestroyAllLights() { }
        public bool IsSpawnedLight(ILight light) => false;
        public void ReleaseLight(ILight light) { }
        public bool ApplyGobo(ILight light, GoboEntry gobo) => false;
        public void ClearGobo(ILight light) { }
        public IReadOnlyList<WorldLightCandidate> GetWorldLightCandidates() =>
            Array.Empty<WorldLightCandidate>();
        public ILight? CaptureWorldLight(WorldLightCandidate candidate) => null;
    }

    private sealed class TestCameraService : IVirtualCameraService
    {
        public bool IsAvailable => false;
        public IReadOnlyList<IVirtualCamera> Cameras => Array.Empty<IVirtualCamera>();
        public IVirtualCamera? LiveCamera => null;

        public FreeCameraSpeedNotice? SpeedNotice => null;
        public void ReportUiTextFocus(bool focused) { }
        public void Dispose() { }
        public IVirtualCamera? CreateCamera(CameraKind kind) => null;
        public IVirtualCamera? CloneCamera(IVirtualCamera source) => null;
        public void DestroyCamera(IVirtualCamera camera) { }
        public void DestroyAllCameras() { }
        public void SetLive(IVirtualCamera camera) { }
        public bool SetTargetActor(IVirtualCamera camera, IActor actor, string displayName) => false;
        public void ClearTargetActor(IVirtualCamera camera) { }
    }

    [Fact]
    public void Facial_capture_reads_the_committed_scene_session()
    {
        var constructor = Assert.Single(typeof(FacialPoseCapture).GetConstructors());

        Assert.Contains(
            constructor.GetParameters(),
            parameter => parameter.ParameterType == typeof(SceneSession));
    }

    [Fact]
    public void Canonical_signature_is_revision_neutral_and_covers_all_scene_content()
    {
        var baseline = CompleteScene(41);
        var session = new SceneSession(new Poser.Application.Selection.SelectionSession());

        Assert.Equal(
            SceneRefreshOutcome.Applied,
            session.TryRefresh(baseline).Outcome);
        var signature = CleanSceneLifecycle.CanonicalSignature(baseline);

        Assert.Equal(0UL, signature.Revision);
        Assert.True(signature.ContentEquals(
            CleanSceneLifecycle.CanonicalSignature(baseline with { Revision = 99 })));

        foreach (var changed in ContentMutations(baseline))
        {
            Assert.False(signature.ContentEquals(
                CleanSceneLifecycle.CanonicalSignature(changed)));
        }
    }

    [Fact]
    public void Admission_revision_advances_only_for_content_changes()
    {
        var committed = CompleteScene(12);
        var session = new SceneSession(new Poser.Application.Selection.SelectionSession());
        Assert.Equal(SceneRefreshOutcome.Applied, session.TryRefresh(committed).Outcome);
        var replay = CleanSceneLifecycle.CreateAdmissionCandidate(
            CompleteScene(900),
            committed);
        var changed = CleanSceneLifecycle.CreateAdmissionCandidate(
            CompleteScene(900) with
            {
                Environment = committed.Environment! with { WeatherId = 88 },
            },
            committed);

        Assert.Equal(12UL, replay.Revision);
        Assert.Equal(13UL, changed.Revision);
        Assert.Equal(SceneRefreshOutcome.NoChange, session.TryRefresh(replay).Outcome);
        Assert.Equal(SceneRefreshOutcome.Applied, session.TryRefresh(changed).Outcome);
    }

    [Fact]
    public void Changed_content_at_max_revision_uses_equal_revision_admission()
    {
        var committed = CompleteScene(ulong.MaxValue);
        var session = new SceneSession(new Poser.Application.Selection.SelectionSession());
        Assert.Equal(SceneRefreshOutcome.Applied, session.TryRefresh(committed).Outcome);

        var changed = CleanSceneLifecycle.CreateAdmissionCandidate(
            CompleteScene(0) with
            {
                Environment = committed.Environment! with { WeatherId = 88 },
            },
            committed);

        Assert.Equal(ulong.MaxValue, changed.Revision);
        Assert.Equal(SceneRefreshOutcome.Applied, session.TryRefresh(changed).Outcome);
        Assert.Equal(88U, session.Snapshot.Environment!.WeatherId);
    }

    private static IEnumerable<SceneSnapshot> ContentMutations(SceneSnapshot scene)
    {
        var actor = scene.Actors[0];
        var skeleton = actor.Skeletons[0];
        var root = skeleton.Bones[0];
        var bone = skeleton.Bones[1];
        var otherActor = scene.Actors[1];
        var light = scene.Lights[0];
        var camera = scene.Cameras[0];
        var prop = scene.Props[0];
        var environment = scene.Environment!;
        var gaze = scene.GazeStates[0];

        yield return scene with { Actors = [actor with { Id = actor.Id with { Generation = 4 } }, otherActor] };
        yield return scene with { Actors = [actor with { Name = "Renamed" }, otherActor] };
        yield return scene with { Actors = [actor with { IsPlayer = false }, otherActor] };
        yield return scene with { Actors = [actor with { IsCompanion = true }, otherActor] };
        yield return scene with { Actors = [actor with { IsHidden = true }, otherActor] };
        yield return scene with { Actors = [actor, otherActor with { OwnerActor = null }] };
        yield return scene with
        {
            Actors =
            [
                actor with
                {
                    Skeletons =
                    [skeleton with
                    {
                        Id = skeleton.Id with { Generation = 6 },
                    }],
                },
                otherActor,
            ],
        };
        yield return scene with
        {
            Actors =
            [
                actor with
                {
                    Skeletons =
                    [skeleton with
                    {
                        Bones = [root, bone with { Id = bone.Id with { BoneIndex = 2 } }],
                    }],
                },
                otherActor,
            ],
        };
        yield return scene with
        {
            Actors =
            [
                actor with
                {
                    Skeletons =
                    [skeleton with
                    {
                        Bones = [root, bone with { DisplayName = "Different" }],
                    }],
                },
                otherActor,
            ],
        };
        yield return scene with
        {
            Actors =
            [
                actor with
                {
                    Skeletons =
                    [skeleton with
                    {
                        Bones = [root, bone with { Parent = null }],
                    }],
                },
                otherActor,
            ],
        };
        yield return scene with
        {
            Actors =
            [
                actor with
                {
                    Skeletons =
                    [skeleton with
                    {
                        Bones = [root, bone with { IsHidden = !bone.IsHidden }],
                    }],
                },
                otherActor,
            ],
        };
        yield return scene with { Lights = [light with { Id = light.Id with { Generation = 2 } }] };
        yield return scene with { Lights = [light with { Name = "Renamed" }] };
        yield return scene with { Lights = [light with { Kind = LightKind.Point }] };
        yield return scene with { Lights = [light with { IsOn = false }] };
        yield return scene with { Lights = [light with { Ownership = LightOwnership.World }] };
        yield return scene with { Lights = [light with { AttachedBone = null }] };
        yield return scene with { Cameras = [camera with { Id = camera.Id with { Generation = 5 } }] };
        yield return scene with { Cameras = [camera with { Name = "Renamed" }] };
        yield return scene with { Cameras = [camera with { Kind = CameraKind.Free }] };
        yield return scene with { Cameras = [camera with { IsLive = false }] };
        yield return scene with { Cameras = [camera with { IsDefault = false }] };
        yield return scene with { Cameras = [camera with { IsLocked = false }] };
        yield return scene with { Cameras = [camera with { TargetActor = null }] };
        yield return scene with { Cameras = [camera with { TargetBone = null }] };
        yield return scene with { Cameras = [camera with { TargetOffset = Vector3.Zero }] };
        yield return scene with { Props = [prop with { Id = prop.Id with { Generation = 7 } }] };
        yield return scene with { Props = [prop with { Name = "Renamed" }] };
        yield return scene with { Props = [prop with { Visible = false }] };
        yield return scene with { Environment = environment with { MinuteOfDay = 721 } };
        yield return scene with { Environment = environment with { DayOfMonth = 13 } };
        yield return scene with { Environment = environment with { WeatherId = 45 } };
        yield return scene with { Environment = environment with { IsTimeFrozen = false } };
        yield return scene with { Environment = environment with { IsWeatherOverrideEnabled = false } };
        yield return scene with
        {
            Environment = environment with { HeldSections = EnvironmentSection.None },
        };
        yield return scene with { GazeStates = [gaze with { Actor = otherActor.Id }] };
        yield return scene with { GazeStates = [gaze with { Mode = GazeMode.Forward }] };
        yield return scene with { GazeStates = [gaze with { Parts = GazeParts.Eyes }] };
        yield return scene with { GazeStates = [gaze with { LockedParts = GazeParts.None }] };
        yield return scene with { GazeStates = [gaze with { TargetActor = otherActor.Id }] };
        yield return scene with { GazeStates = [gaze with { Anchor = Vector3.Zero }] };
        yield return scene with { GazeStates = [gaze with { EyesPosition = Vector3.Zero }] };
        yield return scene with { GazeStates = [gaze with { HeadPosition = Vector3.Zero }] };
        yield return scene with { GazeStates = [gaze with { BodyPosition = Vector3.Zero }] };
    }

    private static SceneSnapshot CompleteScene(ulong revision)
    {
        var actorId = new ActorId(
            Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
            3);
        var skeletonId = new SkeletonId(actorId, PoseSlot.Character, 5);
        var rootId = new BoneId(skeletonId, 0, 0, "root");
        var childId = new BoneId(skeletonId, 0, 1, "child");
        var companionId = new ActorId(
            Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"),
            2);
        var lightId = new LightId(
            Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc"),
            1);
        var cameraId = new CameraId(
            Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd"),
            4);
        var propId = new PropId(
            Guid.Parse("eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee"),
            6);

        return new SceneSnapshot(
            revision,
            [
                new ActorDescriptor(
                    actorId,
                    "Actor",
                    [new SkeletonDescriptor(
                        skeletonId,
                        [
                            new BoneDescriptor(rootId, "Root", null),
                            new BoneDescriptor(childId, "Child", rootId, IsHidden: true),
                        ])],
                    IsPlayer: true),
                new ActorDescriptor(
                    companionId,
                    "Companion",
                    [],
                    IsCompanion: true,
                    OwnerActor: actorId),
            ],
            [new LightDescriptor(
                lightId,
                "Light",
                LightKind.Spot,
                IsOn: true,
                Ownership: LightOwnership.GPose,
                AttachedBone: childId)],
            [new CameraDescriptor(
                cameraId,
                "Camera",
                CameraKind.Game,
                IsLive: true,
                IsDefault: true,
                IsLocked: true,
                TargetActor: actorId,
                TargetBone: childId,
                TargetOffset: new Vector3(1, 2, 3))],
            [new PropDescriptor(propId, "Prop")],
            new EnvironmentDescriptor(
                720,
                12,
                44,
                IsTimeFrozen: true,
                IsWeatherOverrideEnabled: true,
                HeldSections: EnvironmentSection.Sky),
            [new GazeDescriptor(
                actorId,
                GazeMode.Position,
                GazeParts.All,
                GazeParts.Eyes,
                Anchor: new Vector3(4, 5, 6),
                EyesPosition: new Vector3(7, 8, 9),
                HeadPosition: new Vector3(10, 11, 12),
                BodyPosition: new Vector3(13, 14, 15))]);
    }
}
