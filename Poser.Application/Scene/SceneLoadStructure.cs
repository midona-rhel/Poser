using Poser.Application.Lifecycle;
using Poser.Application.Transforms;
using Poser.Domain.Identity;
using Poser.Domain.Scene;
using Poser.Domain.Transforms;
using Poser.Files;

namespace Poser.Application.Scene;

/// <summary>
/// The load's last optional phase: sidebar groups, root order and transform
/// parent links restored over the entities the load created.
/// </summary>
internal sealed class SceneLoadStructure(
    ISceneStatePort sceneState, ISceneStructure structure, TransformParenting parenting)
{
    public static Dictionary<(string Kind, Guid Key), SceneEntityHandle> Tokens(
        params (string Kind, IReadOnlyDictionary<Guid, SceneEntityHandle> Entities)[] maps)
    {
        var result = new Dictionary<(string Kind, Guid Key), SceneEntityHandle>();
        foreach (var (kind, entities) in maps)
            foreach (var (key, token) in entities) result.Add((kind, key), token);
        return result;
    }

    private static bool HasStructure(SceneFile scene) =>
        scene.Groups is { Count: > 0 } || scene.RootOrder is { Count: > 0 } || scene.Parents is { Count: > 0 };

    /// <summary>Waits, bounded, for every loaded member the structure names
    /// to bind. Only a stop (cancel, session replaced) is returned: members
    /// still unbound at the bound are refused by name at commit.</summary>
    public async Task<string?> WaitForBindings(SceneOperation operation, SceneFile scene,
        IReadOnlyDictionary<(string Kind, Guid Key), SceneEntityHandle> tokens, TimeSpan bound,
        CancellationToken cancellation)
    {
        if (!HasStructure(scene)) return null;
        var references = (scene.Groups ?? []).SelectMany(group => group.Members)
            .Concat(scene.RootOrder ?? []).Concat((scene.Parents ?? []).SelectMany(p => new[] { p.Child, p.Target }))
            .Select(reference => (reference.Kind, reference.Key))
            .Distinct().Where(tokens.ContainsKey).ToArray();
        string? stop = null;
        async Task<bool> Settled()
        {
            var result = await sceneState.OnFramework(() =>
            {
                var guard = operation.Guard(sceneState, cancellation);
                return (Stop: guard, Ready: guard == null
                    && references.All(reference => sceneState.ResolveSceneEntity(tokens[reference]) != null));
            });
            stop = result.Stop;
            return result.Stop != null || result.Ready;
        }
        try
        {
            await FrameworkPoll.Until(Settled, bound, TimeSpan.FromMilliseconds(50), cancellation);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            return "The load was cancelled.";
        }
        return stop;
    }

    /// <summary>
    /// Restores groups, root order and parent links over what the load
    /// created. All of it is OPTIONAL (see <see cref="SceneLoadTransaction"/>'s
    /// load policy): a member that did not bind, a refused link or a structure
    /// import that fails as a whole is a named "Group" or "Parent" refusal
    /// added to <paramref name="outcomes"/>, and the entities stay restored.
    /// </summary>
    public void Restore(SceneOperation operation, SceneFile scene,
        IReadOnlyDictionary<(string Kind, Guid Key), SceneEntityHandle> tokens,
        List<SceneEntityOutcome> outcomes)
    {
        if (!HasStructure(scene)) return;
        // A reference this load created a token for but that did not bind is
        // a refusal; one it never created (category left out, entity
        // refused) is already named or deliberately absent.
        bool Loaded(SceneStructureRef reference) => reference.Kind == "companion"
            ? tokens.ContainsKey(("actor", reference.Key))
            : tokens.ContainsKey((reference.Kind, reference.Key));
        SelectionId? Resolve(SceneStructureRef reference)
        {
            if (reference.Kind == "companion")
            {
                var owner = Resolve(new() { Kind = "actor", Key = reference.Key });
                return owner?.Actor is { } actor && parenting.ResolveCompanion(actor) is { } companion
                    ? SelectionId.ForActor(companion) : null;
            }
            return tokens.TryGetValue((reference.Kind, reference.Key), out var token)
                ? sceneState.ResolveSceneEntity(token) : null;
        }

        if (scene.Groups is { Count: > 0 } || scene.RootOrder is { Count: > 0 })
        {
            TransformTargetId? Target(SceneStructureRef reference) => Resolve(reference) is { } id
                ? GroupTransformCoordinator.Target(id) : null;
            var entries = new List<SceneStructureGroup>();
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
                var members = new List<SelectionId>();
                int missing = 0;
                foreach (var member in entry.Members)
                    if (Resolve(member) is { } resolved) members.Add(resolved);
                    else if (Loaded(member)) missing++;
                if (missing > 0)
                    outcomes.Add(new SceneEntityOutcome(SceneOutcomeKind.Group, entry.Name, false,
                        $"{SceneLoadTransaction.Count(missing, "member")} did not become available in time and " +
                        (missing == 1 ? "was" : "were") + " left out of the group."));
                entries.Add(new(entry.Key, entry.Name, entry.Parent, members,
                    transform, entry.Transform != null, entry.InitialFrameRotation)
                {
                    RestoredId = operation.Replay is { } replay && replay.Groups.TryGetValue(entry.Key, out var id)
                        ? id : null,
                });
            }
            var order = new List<RootSlot>();
            foreach (var reference in scene.RootOrder ?? [])
                if (reference.Kind == "group") order.Add(RootSlot.ForGroup(reference.Key));
                else if (Resolve(reference) is { } id) order.Add(RootSlot.For(id));
            try
            {
                var imported = structure.Import(entries, order);
                operation.ImportedGroups.AddRange(imported);
                operation.HistoryGroups = entries.Zip(imported).ToDictionary(pair => pair.First.Key, pair => pair.Second);
            }
            catch (Exception ex)
            {
                outcomes.Add(new SceneEntityOutcome(SceneOutcomeKind.Group, "Sidebar groups", false,
                    $"The saved groups and order could not be restored: {ex.Message}"));
            }
        }

        foreach (var link in scene.Parents ?? [])
        {
            // Category filters and refused spawns leave no token: those keep
            // their saved placement without a word, as before.
            if (!Loaded(link.Child) || !Loaded(link.Target))
                continue;
            if (RestoreLink(operation, link, Resolve) is { } refusal)
                outcomes.Add(new SceneEntityOutcome(SceneOutcomeKind.Parent, EntityName(scene, link.Child), false,
                    $"{refusal} It was kept where it was saved."));
        }
    }

    /// <summary>One parent link; null when it landed, else why not.</summary>
    private string? RestoreLink(SceneOperation operation, SceneParentLink link,
        Func<SceneStructureRef, SelectionId?> resolve)
    {
        if (resolve(link.Child) is not { } child || resolve(link.Target) is not { } target)
            return "The entity or its parent did not become available in time.";
        if (link.BoneName is { } name)
        {
            if (target.Actor is not { } actor) return "A bone parent must belong to an actor.";
            if (parenting.ResolveBone(actor, link.Slot, name, link.Partial) is not { } bone)
                return $"The parent bone '{name}' is not on the restored actor.";
            target = bone;
        }
        if (!parenting.Import(child, new(target, link.Offset)))
            return "The parent link was refused (the entity cannot be parented, or the link would form a cycle).";
        operation.ImportedLinks.Add(child);
        return null;
    }

    /// <summary>The saved name of the entity a structure reference names.</summary>
    private static string EntityName(SceneFile scene, SceneStructureRef reference) => reference.Kind switch
    {
        "actor" or "companion" => scene.Actors.FirstOrDefault(entry => entry.Key == reference.Key)?.Name,
        "prop" => scene.Props.FirstOrDefault(entry => entry.Key == reference.Key)?.Name,
        "light" => scene.Lights.FirstOrDefault(entry => entry.Key == reference.Key)?.Light?.Name,
        "camera" => scene.Cameras.FirstOrDefault(entry => entry.Key == reference.Key)?.Camera?.Name,
        "overlay" => scene.Overlays?.FirstOrDefault(entry => entry.Key == reference.Key)?.Node?.Name,
        "worldObject" => scene.WorldObjects?.FirstOrDefault(entry => entry.Key == reference.Key)?.Name,
        _ => null,
    } ?? reference.Kind;
}
