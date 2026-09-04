using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Poser.Domain.Identity;
using Poser.Domain.Transforms;

namespace Poser.Files;

public static class SceneGroupTransformCodec
{
    private static (string, Guid) Key(SceneStructureRef reference) => (reference.Kind, reference.Key);

    public static string? Validate(SceneFile scene)
    {
        var groups = scene.Groups ?? [];
        if (groups.Any(group => group == null)) return "A group entry is null.";
        if (groups.All(group => group.Transform == null)) return null;
        if (groups.Any(group => group.Key == Guid.Empty)
            || groups.Select(group => group.Key).Distinct().Count() != groups.Count)
            return "Group transform identities are missing or duplicated.";
        if (groups.Any(group => group.Parent is { } parent
            && !groups.Any(candidate => candidate.Key == parent)))
            return "A group parent reference is missing.";
        var known = new HashSet<(string, Guid)>();
        foreach (var actor in scene.Actors) known.Add(("actor", actor.Key));
        foreach (var prop in scene.Props) known.Add(("prop", prop.Key));
        foreach (var light in scene.Lights.Where(light => light.Attachment == null)) known.Add(("light", light.Key));
        foreach (var world in scene.WorldObjects ?? []) known.Add(("worldObject", world.Key));
        foreach (var group in groups.Where(group => group.Transform != null))
        {
            var effective = new HashSet<(string, Guid)>();
            var visited = new HashSet<Guid>();
            bool Visit(SceneGroupEntry current)
            {
                if (!visited.Add(current.Key) || current.Members == null) return false;
                foreach (var member in current.Members)
                    if (member == null || !known.Contains(Key(member)) || !effective.Add(Key(member))) return false;
                foreach (var child in groups.Where(child => child.Parent == current.Key))
                    if (!Visit(child)) return false;
                return true;
            }
            var saved = group.Transform!;
            if (!Visit(group) || effective.Count < 2 || saved.Members == null
                || saved.Members.Any(member => member?.Member == null)
                || saved.Members.Count != effective.Count
                || !effective.SetEquals(saved.Members.Select(member => Key(member.Member))))
                return $"Group '{group.Name}' has incomplete or invalid transform member references.";
            if (!Valid(saved)) return $"Group '{group.Name}' has invalid transform controls or member poses.";
        }
        return null;
    }

    public static bool Valid(SceneGroupTransformEntry saved) =>
        new GroupTransformFrame(saved.FrameOrigin, saved.FrameRotation).IsValid
        && new GroupTransformControls(saved.Position, saved.Rotation, saved.SpacingScale, saved.OwnScale).IsValid
        && saved.Members is { Count: >= 2 }
        && saved.Members.All(member => member?.Member != null
            && member.Initial.IsValid && member.Expected.IsValid);

    public static GroupTransformSnapshot? Decode(SceneGroupTransformEntry saved,
        IReadOnlyCollection<TransformTargetId> required,
        Func<SceneStructureRef, TransformTargetId?> resolve)
    {
        if (!Valid(saved) || saved.Members.Count != required.Count) return null;
        var initial = new Dictionary<TransformTargetId, PoseTransform>();
        var expected = new Dictionary<TransformTargetId, PoseTransform>();
        foreach (var member in saved.Members)
        {
            if (resolve(member.Member) is not { } target || !required.Contains(target)
                || !initial.TryAdd(target, member.Initial)) return null;
            expected.Add(target, member.Expected);
        }
        if (!GroupTransformBaseline.TryCapture(initial,
                new(saved.FrameOrigin, saved.FrameRotation), out var baseline, out _)) return null;
        var result = new GroupTransformSnapshot(baseline!, expected,
            new(saved.Position, saved.Rotation, saved.SpacingScale, saved.OwnScale));
        return result.IsValid ? result : null;
    }

    public static void Rebase(SceneFile scene, Func<Vector3, Vector3> move, Quaternion turn)
    {
        PoseTransform Move(PoseTransform value) => value with {
            Position = move(value.Position), Rotation = Quaternion.Normalize(turn * value.Rotation) };
        foreach (var group in scene.Groups ?? [])
        {
            if (group.InitialFrameRotation is { } frame)
                group.InitialFrameRotation = Quaternion.Normalize(turn * frame);
            if (group.Transform is not { } saved) continue;
            saved.FrameOrigin = move(saved.FrameOrigin);
            saved.FrameRotation = Quaternion.Normalize(turn * saved.FrameRotation);
            saved.Position = move(saved.Position);
            // Authored rotation is in F axes: world delta = F * local * F^-1.
            // Moving F by turn conjugates the world delta without changing local.
            foreach (var member in saved.Members)
            {
                member.Initial = Move(member.Initial);
                member.Expected = Move(member.Expected);
            }
        }
    }
}
