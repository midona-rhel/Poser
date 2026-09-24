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
    bool SetVisibility(SelectionId id, bool visible);
    Task<bool> Remove(SelectionId id, SelectionRemoval removal);
}

/// <summary>Shared selection commands for sidebar and context-menu routes.
/// Reads the exact id immediately before dispatch; the host port must repeat
/// exact binding and ownership checks before touching a live entity.</summary>
public sealed class SelectionEntityCommands(
    ICurrentSelectionEntityReads reads,
    ISelectionEntityCommandPort port)
{
    public bool? ReadVisibility(SelectionId id)
    {
        var current = reads.ReadCurrent(id);
        if (current is not { CanChangeVisibility: true } || current.Id != id)
            return null;
        return port.ReadVisibility(id);
    }

    public int SetVisibility(IEnumerable<SelectionId> ids, bool visible)
    {
        int applied = 0;
        foreach (var id in ids.Distinct())
        {
            var current = reads.ReadCurrent(id);
            if (current is not { CanChangeVisibility: true } || current.Id != id)
                continue;
            if (port.SetVisibility(id, visible))
                applied++;
        }
        return applied;
    }

    public async Task<int> Remove(IEnumerable<SelectionId> ids)
    {
        var pending = new List<Task<bool>>();
        foreach (var id in ids.Distinct())
        {
            var current = reads.ReadCurrent(id);
            if (current is not { Removal: not SelectionRemoval.None } entity
                || entity.Id != id)
                continue;
            // Start each host operation on the caller's owning thread before
            // awaiting asynchronous borrowed-asset release results.
            pending.Add(port.Remove(id, entity.Removal));
        }
        var results = await Task.WhenAll(pending);
        return results.Count(applied => applied);
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
