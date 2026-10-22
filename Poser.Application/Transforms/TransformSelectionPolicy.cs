using Poser.Application.Scene;
using Poser.Application.Viewport;
using Poser.Domain.Identity;
using Poser.Domain.Scene;

namespace Poser.Application.Transforms;

/// <summary>One lock admission policy for inspector, gizmo, pivot and placement selection.</summary>
public static class TransformSelectionPolicy
{
    public static EffectiveTransformSelection? Editing(IReadOnlyList<SelectionId> selected,
        SceneSnapshot scene, SceneGroups groups, IViewportReads viewport) =>
        TransformTargetResolver.Resolve(selected, scene,
            id => groups.IsLockedChild(id, selected) || ColliderLocked(id, viewport));

    // Placement is a discrete per-entity command, not a whole-group gesture.
    // Keep its existing refusal of even a fully selected locked group's members.
    public static EffectiveTransformSelection? Placement(IReadOnlyList<SelectionId> selected,
        SceneSnapshot scene, SceneGroups groups, IViewportReads viewport) =>
        TransformTargetResolver.Resolve(selected, scene,
            id => groups.IsLockedMember(id) || ColliderLocked(id, viewport));

    private static bool ColliderLocked(SelectionId id, IViewportReads viewport) =>
        id.Overlay is { } overlay && viewport.GetCollider(overlay)?.Locked == true;
}
