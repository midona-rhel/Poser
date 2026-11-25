using Poser.Application.Scene;
using Poser.Application.Viewport;
using Poser.Domain.Identity;
using Poser.Domain.Scene;

namespace Poser.Application.Transforms;

/// <summary>
/// What a lock stops: moving, rotating and scaling. The inspector, the gizmo,
/// placement, parenting and group transforms all ask <see cref="IsLocked"/>.
/// Cameras lock through their own settings, because nothing else moves them.
/// </summary>
public static class TransformSelectionPolicy
{
    public static EffectiveTransformSelection? Editing(IReadOnlyList<SelectionId> selected,
        SceneSnapshot scene, SceneGroups groups, IViewportReads viewport) =>
        TransformTargetResolver.Resolve(selected, scene, id => IsLocked(id, selected, groups, viewport));

    public static EffectiveTransformSelection? Placement(IReadOnlyList<SelectionId> selected,
        SceneSnapshot scene, SceneGroups groups, IViewportReads viewport) =>
        TransformTargetResolver.Resolve(selected, scene, id => IsLocked(id, null, groups, viewport));

    /// <summary>
    /// Whether a lock stops this thing from being moved, rotated or scaled: its
    /// own lock (a collider's), or a locked group it belongs to. A locked group
    /// still moves as a whole when every member is in <paramref name="together"/>.
    /// Placement and parenting act on one thing at a time and pass null.
    /// </summary>
    public static bool IsLocked(SelectionId id, IReadOnlyList<SelectionId>? together,
        SceneGroups groups, IViewportReads viewport) =>
        (together is null ? groups.IsLockedMember(id) : groups.IsLockedChild(id, together))
        || id.Overlay is { } overlay && viewport.GetCollider(overlay)?.Locked == true;
}
