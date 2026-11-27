using Poser.Domain;
using Poser.Domain.Identity;
using Poser.Domain.Scene;

namespace Poser.Application.Selection;

/// <summary>What can be done to one selected thing right now: whether it can be
/// shown or hidden, and how it is removed.</summary>
public sealed record CurrentSelectionEntity(
    SelectionId Id,
    bool CanChangeVisibility,
    SelectionRemoval Removal);

public enum SelectionRemoval
{
    None,
    Destroy,
    Release,
}

public enum SelectionRemovalStatus
{
    Removed,
    AlreadyAbsent,
    Refused,
    Failed,
}

public sealed record SelectionRemovalItem(
    SelectionId Id, SelectionRemovalStatus Status, string? Detail = null);

public sealed record SelectionRemovalResult(IReadOnlyList<SelectionRemovalItem> Items)
{
    public int AppliedCount => Items.Count(item => item.Status == SelectionRemovalStatus.Removed);
}

public sealed record SelectionVisibilityItem(SelectionId Id, Outcome Result);

public sealed record SelectionVisibilityResult(IReadOnlyList<SelectionVisibilityItem> Items)
{
    public int AppliedCount => Items.Count(item => item.Result.Success);
}

/// <summary>
/// Showing, hiding and removing selected things, for the sidebar, the context
/// menus and group gates. <c>Poser.Game</c> does the work.
/// </summary>
public interface ISelectedEntityActions
{
    /// <summary>Whether the thing is shown. Null when it cannot be shown or
    /// hidden, or is no longer in the scene.</summary>
    bool? ReadVisibility(SelectionId id);

    /// <summary>Shows or hides each thing that can be. Several things are one
    /// undo step that holds only the changes that worked. Things that can never
    /// be shown or hidden, such as cameras and bones, are left out without a
    /// message; things that left the scene are reported.</summary>
    SelectionVisibilityResult SetVisibility(IEnumerable<SelectionId> ids, bool visible);

    /// <summary>Deletes what Poser made and releases what it borrowed from the
    /// world, as one undo step. Things that cannot be removed are left out.</summary>
    Task<SelectionRemovalResult> Remove(IEnumerable<SelectionId> ids);
}

/// <summary>Reads what can be done to a selected thing from the scene, without
/// touching the game.</summary>
public static class SelectionEntityCapabilities
{
    public static CurrentSelectionEntity? Read(SceneSnapshot scene, SelectionId id)
    {
        return id.Kind switch
        {
            SceneEntityKind.Actor when id.Actor is { } actor =>
                scene.Actors.FirstOrDefault(x => x.Id == actor) is { } value
                    ? new(id, true,
                        value.IsAdopted ? SelectionRemoval.Release : SelectionRemoval.Destroy)
                    : null,
            SceneEntityKind.Light when id.Light is { } light =>
                scene.Lights.FirstOrDefault(x => x.Id == light) is { } value
                    ? new(id, true,
                        value.Ownership == LightOwnership.Spawned
                            ? SelectionRemoval.Destroy : SelectionRemoval.Release)
                    : null,
            SceneEntityKind.Prop when id.Prop is { } prop =>
                scene.Props.FirstOrDefault(x => x.Id == prop) is { } value
                    ? new(id, true, SelectionRemoval.Destroy)
                    : null,
            SceneEntityKind.Camera when id.Camera is { } camera =>
                scene.Cameras.FirstOrDefault(x => x.Id == camera) is { } value
                    ? new(id, false,
                        value.IsDefault ? SelectionRemoval.None : SelectionRemoval.Destroy)
                    : null,
            SceneEntityKind.Overlay when id.Overlay is { } overlay =>
                scene.Overlays.FirstOrDefault(x => x.Id == overlay) is { } value
                    ? new(id, true, SelectionRemoval.Destroy)
                    : null,
            SceneEntityKind.WorldObject when id.WorldObject is { } world =>
                scene.WorldObjects.FirstOrDefault(x => x.Id == world) is { } value
                    ? new(id, true, SelectionRemoval.Release)
                    : null,
            _ => null,
        };
    }
}
