using NSubstitute;
using Poser.Application.Scene;
using Poser.Application.Transforms;
using Poser.Application.Viewport;
using Poser.Domain.Identity;
using Poser.Domain.Posing;
using Poser.Domain.Presentation;
using Poser.Domain.Scene;

namespace Poser.Application.Tests.Transforms;

public sealed class TransformSelectionPolicyTests
{
    [Fact]
    public void Locked_groups_allow_whole_group_editing_but_not_member_placement()
    {
        var a = PropId.New();
        var b = PropId.New();
        SelectionId[] selected = [SelectionId.ForProp(a), SelectionId.ForProp(b)];
        var groups = new SceneGroups();
        groups.Create("Locked", selected)!.Locked = true;
        var scene = new SceneSnapshot(1, [], [], [], [new(a, "A"), new(b, "B")]);
        var viewport = Substitute.For<IViewportReads>();
        Assert.Equal(2, TransformSelectionPolicy.Editing(selected, scene, groups, viewport)!.Targets.Count);
        Assert.Null(TransformSelectionPolicy.Editing([selected[0]], scene, groups, viewport));
        Assert.Null(TransformSelectionPolicy.Placement(selected, scene, groups, viewport));
    }

    [Fact]
    public void Locked_colliders_are_excluded_from_both_editing_and_placement()
    {
        var id = OverlayId.New();
        SelectionId[] selected = [SelectionId.ForOverlay(id)];
        var scene = new SceneSnapshot(1, [], [], [], [], Overlays: [new(id, "Collider", OverlayNodeKind.Collider)]);
        var viewport = Substitute.For<IViewportReads>();
        viewport.GetCollider(id).Returns(new ColliderViewportState(new IkCollider { Locked = true }, true, 1));
        Assert.Null(TransformSelectionPolicy.Editing(selected, scene, new(), viewport));
        Assert.Null(TransformSelectionPolicy.Placement(selected, scene, new(), viewport));
        viewport.GetCollider(id).Returns(new ColliderViewportState(new IkCollider { Locked = false }, true, 1));
        Assert.NotNull(TransformSelectionPolicy.Editing(selected, scene, new(), viewport));
        Assert.NotNull(TransformSelectionPolicy.Placement(selected, scene, new(), viewport));
    }
}
