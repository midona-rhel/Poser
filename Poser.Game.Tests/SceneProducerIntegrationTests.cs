using System.Numerics;
using System.Reflection;
using Poser.Application.Scene;
using Poser.Domain.Identity;
using Poser.Domain.Scene;
using Poser.Game.Animation;
using Poser.Game.Bindings;
using Poser.Game.Scene;

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
    public void Facial_capture_reads_the_committed_scene_session()
    {
        var sceneField = typeof(FacialPoseCapture).GetField(
            "_scene",
            BindingFlags.Instance | BindingFlags.NonPublic);

        Assert.NotNull(sceneField);
        Assert.Equal(typeof(SceneSession), sceneField!.FieldType);
    }

    [Fact]
    public void Canonical_signature_is_revision_neutral_and_covers_all_scene_content()
    {
        var baseline = CompleteScene(41);
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
        yield return scene with { GazeStates = [gaze with { TargetActor = null }] };
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
                TargetActor: companionId,
                Anchor: new Vector3(4, 5, 6),
                EyesPosition: new Vector3(7, 8, 9),
                HeadPosition: new Vector3(10, 11, 12),
                BodyPosition: new Vector3(13, 14, 15))]);
    }
}
