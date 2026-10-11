using Poser.Domain.Identity;
using Poser.Domain.Presentation;
using Poser.Domain.Transforms;

namespace Poser.Documents.Files;

public sealed class SceneParentLink
{
    public SceneStructureRef Child { get; set; } = new();
    public SceneStructureRef Target { get; set; } = new();
    public string? BoneName { get; set; }
    public PoseSlot Slot { get; set; }
    public int Partial { get; set; }
    public PoseTransform Offset { get; set; } = PoseTransform.Identity;
}

public static class SceneParenting
{
    public static HashSet<(string, Guid)> Keys(SceneFile scene) =>
        scene.Actors.Select(x => (SceneStructureKind.Actor, x.Key)).Concat(scene.Lights.Select(x => (SceneStructureKind.Light, x.Key)))
            .Concat(scene.Actors.Where(x => x.CompanionKind != null).Select(x => (SceneStructureKind.Companion, x.Key)))
            .Concat(scene.Props.Select(x => (SceneStructureKind.Prop, x.Key))).Concat(scene.Cameras.Select(x => (SceneStructureKind.Camera, x.Key)))
            .Concat((scene.Overlays ?? []).Where(x => x.Node?.Kind == OverlayNodeKind.Collider)
                .Select(x => (SceneStructureKind.Overlay, x.Key)))
            .Concat((scene.WorldObjects ?? []).Select(x => (SceneStructureKind.WorldObject, x.Key))).ToHashSet();

    public static void Prune(SceneFile scene, List<string> notes)
    {
        var keys = Keys(scene);
        scene.Parents?.RemoveAll(link =>
        {
            if (!keys.Contains((link.Child.Kind, link.Child.Key))) return true;
            if (keys.Contains((link.Target.Kind, link.Target.Key))) return false;
            notes.Add("A parent was excluded from this save; its child is saved at its current world placement.");
            return true;
        });
        if (scene.Parents is { Count: 0 })
            scene.Parents = null;
    }

    public static string? Validate(SceneFile scene)
    {
        if (scene.Parents is not { } links) return null;
        var keys = Keys(scene);
        var parents = new Dictionary<(string, Guid), (string, Guid)>();
        foreach (var link in links)
        {
            if (link?.Child == null || link.Target == null || !link.Offset.IsValid
                || !keys.Contains((link.Child.Kind, link.Child.Key)) || !keys.Contains((link.Target.Kind, link.Target.Key))
                || link.Child.Kind == SceneStructureKind.Camera || link.Target.Kind == SceneStructureKind.Camera || (link.BoneName != null &&
                    (link.Target.Kind is not (SceneStructureKind.Actor or SceneStructureKind.Companion) || string.IsNullOrWhiteSpace(link.BoneName) || link.BoneName.Length > 256
                        || !Enum.IsDefined(link.Slot) || link.Partial < 0))
                || !parents.TryAdd((link.Child.Kind, link.Child.Key), (link.Target.Kind, link.Target.Key)))
                return "The scene has an invalid transform-parent relationship.";
        }
        foreach (var actor in scene.Actors.Where(x => x.CompanionKind != null))
            parents.TryAdd((SceneStructureKind.Companion, actor.Key), (SceneStructureKind.Actor, actor.Key));
        foreach (var child in parents.Keys)
        {
            var visited = new HashSet<(string, Guid)>();
            var current = child;
            while (parents.TryGetValue(current, out var parent))
            {
                if (!visited.Add(current)) return "The scene has a transform-parent cycle.";
                current = parent;
            }
        }
        return null;
    }
}
