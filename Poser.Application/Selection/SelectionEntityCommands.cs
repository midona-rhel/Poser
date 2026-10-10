using Poser.Application.Transforms;
using Poser.Domain;
using Poser.Domain.Identity;
using Poser.Domain.Scene;

namespace Poser.Application.Selection;

/// <summary>The current pointer-free facts that decide which shared entity
/// verbs can run for one exact selection identity.</summary>
public sealed record CurrentSelectionEntity(
    SelectionId Id,
    bool CanChangeVisibility,
    bool? IsVisible,
    SelectionRemoval Removal);

public enum SelectionRemoval
{
    None,
    Destroy,
    Release,
}

public sealed record SelectionRemovalRequest(SelectionId Id, SelectionRemoval Removal);

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

/// <summary>Reads capabilities only for the exact current SelectionId. It
/// never repairs an old generation into its successor.</summary>
public interface ICurrentSelectionEntityReads
{
    CurrentSelectionEntity? ReadCurrent(SelectionId id);
}

/// <summary>Executes one already-authorized command after resolving its id to
/// the live host object again.</summary>
public interface ISelectionEntityCommandPort
{
    bool? ReadVisibility(SelectionId id);
    Outcome SetVisibility(SelectionId id, bool visible);
    Task<SelectionRemovalResult> Remove(IReadOnlyList<SelectionRemovalRequest> requests);
}

/// <summary>Shared selection commands for sidebar and context-menu routes.
/// Reads the exact id immediately before dispatch; the host port must repeat
/// exact binding and ownership checks before touching a live entity.</summary>
public sealed class SelectionEntityCommands(
    ICurrentSelectionEntityReads reads,
    ISelectionEntityCommandPort port,
    TransformHistory history)
{
    private const string Unavailable = "That entity is no longer available.";

    public bool? ReadVisibility(SelectionId id)
    {
        var current = reads.ReadCurrent(id);
        if (current is not { CanChangeVisibility: true } || current.Id != id)
            return null;
        return port.ReadVisibility(id);
    }

    /// <summary>Shows or hides every eligible id. Several targets are one
    /// history entry holding only the writes that landed; refused targets
    /// append nothing and are reported per item. Ids that cannot change
    /// visibility (cameras, bones) are left out silently.</summary>
    public SelectionVisibilityResult SetVisibility(IEnumerable<SelectionId> ids, bool visible)
    {
        var items = new List<SelectionVisibilityItem>();
        var eligible = new List<SelectionId>();
        foreach (var id in ids.Distinct())
        {
            var current = reads.ReadCurrent(id);
            if (current is null || current.Id != id)
            {
                // A stale entity id is a refusal; a non-entity id (a bone,
                // a gaze point) was never a visibility target.
                if (IsEntityKind(id.Kind)) items.Add(new(id, new(false, Unavailable)));
                continue;
            }
            if (current.CanChangeVisibility) eligible.Add(id);
        }
        void Apply()
        {
            foreach (var id in eligible) items.Add(new(id, Write(id, visible)));
        }
        // One target keeps its own value step, so entity-scoped undo still
        // reaches it; several share one batch entry.
        if (eligible.Count > 1)
            history.RecordLifecycleBatch(visible ? "Show entities" : "Hide entities", Apply);
        else
            Apply();
        return new(items);
    }

    private Outcome Write(SelectionId id, bool visible)
    {
        try { return port.SetVisibility(id, visible); }
        catch (Exception ex) { return new(false, ex.Message); }
    }

    private static bool IsEntityKind(SceneEntityKind kind) => kind is SceneEntityKind.Actor
        or SceneEntityKind.Light or SceneEntityKind.Prop or SceneEntityKind.Camera
        or SceneEntityKind.Overlay or SceneEntityKind.WorldObject;

    public Task<SelectionRemovalResult> Remove(IEnumerable<SelectionId> ids)
    {
        var requests = new List<SelectionRemovalRequest>();
        foreach (var id in ids.Distinct())
        {
            var current = reads.ReadCurrent(id);
            if (current is not { Removal: not SelectionRemoval.None } entity
                || entity.Id != id)
                continue;
            requests.Add(new(id, entity.Removal));
        }
        // Capture the complete intent before dispatch so the runtime can
        // revalidate and journal one batch on its owning thread.
        return requests.Count == 0
            ? Task.FromResult(new SelectionRemovalResult(Array.Empty<SelectionRemovalItem>()))
            : port.Remove(requests.ToArray());
    }
}

/// <summary>Exact descriptor lookup helper kept with the application read
/// contract so entity capabilities stay free of live handles.</summary>
public static class SelectionEntityCapabilities
{
    public static CurrentSelectionEntity? Read(SceneSnapshot scene, SelectionId id)
    {
        ArgumentNullException.ThrowIfNull(scene);
        return id.Kind switch
        {
            SceneEntityKind.Actor when id.Actor is { } actor =>
                scene.Actors.FirstOrDefault(x => x.Id == actor) is { } value
                    ? new(id, true, !value.IsHidden,
                        value.IsAdopted ? SelectionRemoval.Release : SelectionRemoval.Destroy)
                    : null,
            SceneEntityKind.Light when id.Light is { } light =>
                scene.Lights.FirstOrDefault(x => x.Id == light) is { } value
                    ? new(id, true, value.IsOn,
                        value.Ownership == LightOwnership.Spawned
                            ? SelectionRemoval.Destroy : SelectionRemoval.Release)
                    : null,
            SceneEntityKind.Prop when id.Prop is { } prop =>
                scene.Props.FirstOrDefault(x => x.Id == prop) is { } value
                    ? new(id, true, value.Visible, SelectionRemoval.Destroy)
                    : null,
            SceneEntityKind.Camera when id.Camera is { } camera =>
                scene.Cameras.FirstOrDefault(x => x.Id == camera) is { } value
                    ? new(id, false, null,
                        value.IsDefault ? SelectionRemoval.None : SelectionRemoval.Destroy)
                    : null,
            SceneEntityKind.Overlay when id.Overlay is { } overlay =>
                scene.Overlays.FirstOrDefault(x => x.Id == overlay) is { } value
                    ? new(id, true, value.Visible, SelectionRemoval.Destroy)
                    : null,
            SceneEntityKind.WorldObject when id.WorldObject is { } world =>
                scene.WorldObjects.FirstOrDefault(x => x.Id == world) is { } value
                    ? new(id, true, value.Visible, SelectionRemoval.Release)
                    : null,
            _ => null,
        };
    }
}
