using Poser.Application.Scene;
using Poser.Application.Transforms;
using Poser.Domain.Identity;
using Poser.Domain.Transforms;
using Poser.Files;

namespace Poser.Game.Scene;

public sealed partial class SceneWorkflow
{
    internal TimeSpan StructureBindingBound { get; init; } = TimeSpan.FromSeconds(2);

    private static Dictionary<(string Kind, Guid Key), object> StructureTokens(
        params (string Kind, IReadOnlyDictionary<Guid, object> Entities)[] maps)
    {
        var result = new Dictionary<(string, Guid), object>();
        foreach (var (kind, entities) in maps)
            foreach (var (key, token) in entities) result.Add((kind, key), token);
        return result;
    }

    private static bool HasStructure(SceneFile scene) =>
        scene.Groups is { Count: > 0 } || scene.RootOrder is { Count: > 0 };

    private async Task<string?> WaitForStructure(Operation operation, SceneFile scene,
        IReadOnlyDictionary<(string Kind, Guid Key), object> tokens, CancellationToken cancellation)
    {
        if (!HasStructure(scene)) return null;
        if (_structure == null) return "Scene structure restoration is unavailable.";
        var references = (scene.Groups ?? []).SelectMany(group => group.Members)
            .Concat(scene.RootOrder ?? []).Select(reference => (reference.Kind, reference.Key))
            .Distinct().Where(tokens.ContainsKey).ToArray();
        var deadline = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            var result = await _runtime.OnFramework(() =>
            {
                var stop = Guard(operation, cancellation);
                return (Stop: stop, Ready: stop == null
                    && references.All(reference => _runtime.ResolveSceneEntity(tokens[reference]) != null));
            });
            if (result.Stop != null) return result.Stop;
            if (result.Ready) return null;
            if (deadline.Elapsed >= StructureBindingBound)
                return "The loaded scene's group/order members did not become available.";
            await Task.Delay(50, cancellation);
        }
    }

    private void RestoreStructure(Operation operation, SceneFile scene,
        IReadOnlyDictionary<(string Kind, Guid Key), object> tokens)
    {
        if (!HasStructure(scene)) return;
        SelectionId? Resolve(SceneStructureRef reference)
        {
            if (!tokens.TryGetValue((reference.Kind, reference.Key), out var token)) return null;
            // Recheck at commit: a binding may have disappeared after the readiness wait.
            return _runtime.ResolveSceneEntity(token)
                ?? throw new InvalidOperationException("A scene structure member disappeared before commit.");
        }
        TransformTargetId? Target(SceneStructureRef reference) => Resolve(reference) is { } id
            ? GroupTransformCoordinator.Target(id) : null;
        var entries = new List<LoadedSceneGroup>();
        foreach (var entry in scene.Groups ?? [])
        {
            GroupTransformSnapshot? transform = null;
            if (entry.Transform is { } saved)
            {
                var targets = saved.Members.Select(member => Target(member.Member)).ToArray();
                if (targets.All(target => target != null))
                    transform = SceneGroupTransformCodec.Decode(saved,
                        targets.Select(target => target!.Value).ToArray(), Target);
            }
            entries.Add(new(entry.Key, entry.Name, entry.Parent,
                entry.Members.Select(Resolve).OfType<SelectionId>().ToArray(),
                transform, entry.Transform != null, entry.InitialFrameRotation));
        }
        var order = new List<RootSlot>();
        foreach (var reference in scene.RootOrder ?? [])
            if (reference.Kind == "group") order.Add(RootSlot.ForGroup(reference.Key));
            else if (Resolve(reference) is { } id) order.Add(RootSlot.For(id));
        operation.ImportedGroups.AddRange(_structure!.Import(entries, order));
    }
}
