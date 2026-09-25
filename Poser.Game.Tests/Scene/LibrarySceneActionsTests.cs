using System.Numerics;
using NSubstitute;
using Poser.Application.Library;
using Poser.Application.Scene;
using Poser.Domain.Operations;
using Poser.Domain.Scene;
using Poser.Files;
using Poser.Library;
using Poser.Services;

namespace Poser.Game.Tests.Scene;

public sealed class LibrarySceneActionsTests
{
    [Fact]
    public void Saved_entries_never_inherit_destructive_scene_options_and_use_the_requested_anchor()
    {
        var scene = Substitute.For<ISceneWorkflow>();
        var anchors = Substitute.For<IPlacementAnchorSource>();
        anchors.TryCurrentFor(ObjectPlacementMode.RelativeToCamera, out Arg.Any<Vector3>(),
            out Arg.Any<float>(), out Arg.Any<string?>()).Returns(call =>
        {
            call[1] = new Vector3(3, 4, 5); call[2] = 1.25f; call[3] = null; return true;
        });
        var preferences = new SceneLoadPreferences { Options = new() { ClearExistingScene = true } };
        var actions = new LibrarySceneActions(scene, preferences, anchors,
            Substitute.For<ISceneCreation>(), Substitute.For<IPendingSceneCreation>());
        actions.SpawnEntry("group.xivs", PoseLibraryEntryKind.Group, ObjectPlacementMode.RelativeToCamera);
        scene.Received().BeginLoad("group.xivs", Arg.Is<SceneLoadOptions>(o =>
            !o.ClearExistingScene && o.PlacementPosition == new Vector3(3, 4, 5) && o.PlacementYaw == 1.25f));
        actions.LoadScene("scene.xivs", ObjectPlacementMode.AsSaved);
        scene.Received().BeginLoad("scene.xivs", Arg.Is<SceneLoadOptions>(o => o.ClearExistingScene));
    }

    [Fact]
    public void Missing_anchor_refuses_explicit_placement_but_portal_can_fall_back_to_saved()
    {
        var scene = Substitute.For<ISceneWorkflow>();
        scene.BeginLoad(default!, default).ReturnsForAnyArgs(SceneActionResult.Ok());
        var actions = new LibrarySceneActions(scene, new(), Substitute.For<IPlacementAnchorSource>(),
            Substitute.For<ISceneCreation>(), Substitute.For<IPendingSceneCreation>());
        Assert.False(actions.SpawnEntry("actor.xiva", PoseLibraryEntryKind.Actor,
            ObjectPlacementMode.RelativeToSelectedActor).Success);
        scene.DidNotReceiveWithAnyArgs().BeginLoad(default!, default);
        Assert.True(actions.SpawnEntry("actor.xiva", PoseLibraryEntryKind.Actor,
            ObjectPlacementMode.RelativeToSelectedActor, fallbackToSaved: true).Success);
        scene.Received(1).BeginLoad("actor.xiva", Arg.Is<SceneLoadOptions>(o =>
            o.Placement == ObjectPlacementMode.AsSaved && !o.ClearExistingScene));
    }

    [Fact]
    public void Pose_spawn_hands_an_immutable_request_to_the_application_pending_owner()
    {
        var creation = Substitute.For<ISceneCreation>();
        var pending = Substitute.For<IPendingSceneCreation>();
        var handle = new SceneEntityHandle(SessionGeneration.New(), Poser.Domain.Identity.SceneEntityKind.Actor);
        creation.CreateActor(default!).ReturnsForAnyArgs(new SceneCreationResult(handle));
        var actions = new LibrarySceneActions(Substitute.For<ISceneWorkflow>(), new(),
            Substitute.For<IPlacementAnchorSource>(), creation, pending);
        var options = new PoseImportOptions { ApplyScale = true };
        Assert.True(actions.SpawnPose("saved.pose", options).Success);
        options.ApplyScale = false;
        pending.Received(1).ApplyPoseWhenReady(handle, "saved.pose",
            Arg.Is<PoseImportOptions>(o => o.ApplyScale));
    }
}

