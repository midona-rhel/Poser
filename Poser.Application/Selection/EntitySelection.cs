using Poser.Domain.Identity;
using Poser.Domain.Scene;

namespace Poser.Application.Selection;

/// <summary>
/// The selection questions the ANONYMOUS GROUP asks: a multiselect of two
/// or more scene entities behaves as a group that was never created —
/// manipulated together about its centroid, presented on one Selection
/// page — while bones and gaze anchors stay posing concerns and never
/// count toward it.
/// </summary>
public static class EntitySelection
{
    public static bool IsEntity(SceneEntityKind kind) => kind
        is SceneEntityKind.Actor
        or SceneEntityKind.Prop
        or SceneEntityKind.Light
        or SceneEntityKind.Camera
        or SceneEntityKind.WorldObject
        or SceneEntityKind.Overlay;

    public static int CountEntities(IReadOnlyList<SelectionId> selected)
    {
        int count = 0;
        for (int i = 0; i < selected.Count; i++)
            if (IsEntity(selected[i].Kind))
                count++;
        return count;
    }

    /// <summary>Two or more entities, whatever their kinds — the anonymous
    /// group's whole membership test.</summary>
    public static bool IsMultiEntity(IReadOnlyList<SelectionId> selected) =>
        CountEntities(selected) >= 2;

    /// <summary>Two or more entities spanning more than one kind — the
    /// selections the uniform transform branches cannot resolve.</summary>
    public static bool IsMixedEntities(IReadOnlyList<SelectionId> selected)
    {
        SceneEntityKind? seen = null;
        int count = 0;
        for (int i = 0; i < selected.Count; i++)
        {
            var kind = selected[i].Kind;
            if (!IsEntity(kind))
                continue;
            count++;
            if (seen != null && seen != kind)
                return count >= 2;
            seen = kind;
        }
        return false;
    }
}
