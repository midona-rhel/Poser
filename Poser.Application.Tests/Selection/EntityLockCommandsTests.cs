using NSubstitute;
using Poser.Application.Presentation;
using Poser.Application.Scene;
using Poser.Application.Selection;
using Poser.Application.Transforms;
using Poser.Domain;
using Poser.Domain.Identity;
using Poser.Domain.Posing;
using Poser.Domain.Presentation;

namespace Poser.Application.Tests.Selection;

public sealed class EntityLockCommandsTests
{
    [Fact]
    public void Toggle_reads_current_state_and_preserves_the_owner_refusal()
    {
        var overlays = Substitute.For<IOverlayControl>();
        var cameras = Substitute.For<ICameraControl>();
        var groups = new SceneGroups();
        var history = new EditHistory();
        var control = new EntityLockCommands(cameras, overlays, groups, new(groups, history, new(history)));
        var id = OverlayId.New();
        var state = new IkCollider();
        overlays.Read(id).Returns(_ => new OverlayReading(id, new OverlayNodeState { Collider = state }));
        overlays.EditCollider(id, Arg.Any<Func<IkCollider, IkCollider>>()).Returns(call =>
        {
            state = call.Arg<Func<IkCollider, IkCollider>>()(state);
            return Outcome.Ok();
        });
        Assert.True(control.Toggle(SelectionId.ForOverlay(id)).Success);
        Assert.True(state.Locked);
        Assert.True(control.Toggle(SelectionId.ForOverlay(id)).Success);
        Assert.False(state.Locked);
        overlays.EditCollider(id, Arg.Any<Func<IkCollider, IkCollider>>()).Returns(Outcome.Fail("unavailable"));
        Assert.False(control.Toggle(SelectionId.ForOverlay(id)).Success);
        Assert.False(state.Locked);
        Assert.False(control.Toggle(SelectionId.ForProp(PropId.New())).Success);
        Assert.False(control.ToggleGroup(Guid.NewGuid()).Success);
    }
}
