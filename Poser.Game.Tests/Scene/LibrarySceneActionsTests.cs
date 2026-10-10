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
}

