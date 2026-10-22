using Poser.Application.Presentation;
using Poser.Application.Scene;
using Poser.Domain;
using Poser.Domain.Identity;

namespace Poser.Application.Selection;

/// <summary>Lock commands re-read the exact subject and delegate to its existing journal owner.</summary>
public sealed class EntityLockCommands(ICameraControl cameras, IOverlayControl overlays,
    SceneGroups groups, GroupSteps groupSteps)
{
    public bool? Read(SelectionId id) => id switch
    {
        { Camera: { } camera } => cameras.Read(camera)?.IsLocked,
        { Overlay: { } overlay } => overlays.Read(overlay)?.State.Collider?.Locked,
        _ => null,
    };

    public Outcome Toggle(SelectionId id)
    {
        if (Read(id) is not { } locked) return Outcome.Fail("This entity has no available lock.");
        if (id.Camera is { } camera) return cameras.Set(camera, CameraProperties.IsLocked, !locked);
        if (id.Overlay is { } overlay) return overlays.EditCollider(overlay, value => value with { Locked = !locked });
        return Outcome.Fail("This entity has no available lock.");
    }

    public Outcome ToggleGroup(Guid id)
    {
        if (groups.Find(id) is not { } group) return Outcome.Fail("The group is no longer available.");
        groupSteps.SetLocked(id, !group.Locked);
        return Outcome.Ok();
    }
}
