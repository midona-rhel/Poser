using Poser.Domain.Identity;
using Poser.Domain.Posing;
using Poser.Entities;
using Poser.Files;
using Poser.Services;

namespace Poser.Game.Scene;

internal sealed partial class SceneRuntimeAdapter
{
    public async Task WaitForFabrikBindings(IEnumerable<object> entities, CancellationToken cancellation)
    {
        var tokens = entities.ToArray();
        IEntityBindings bindings = _bindings;
        // Spawning precedes scene publication. Wait for actual binding admission,
        // not a guessed delay, before remapping portable target keys.
        for (int attempt = 0; attempt < 40; attempt++)
        {
            cancellation.ThrowIfCancellationRequested();
            if (await OnFramework(() => tokens.All(token => token switch
            {
                IActor actor => bindings.GetActorId(actor) != null,
                ILight light => bindings.GetLightId(light) != null,
                IPropHandle prop => bindings.GetPropId(prop) != null,
                IWorldObject world => bindings.GetWorldObjectId(world) != null,
                _ => true,
            }))) return;
            await Task.Delay(50, cancellation);
        }
        // Unavailable references are reported by RestoreFabrik, not rebound by name.
    }

    public IReadOnlyList<string> RestoreFabrik(SceneFile scene, IReadOnlyDictionary<Guid, object> actors,
        IReadOnlyDictionary<Guid, object> props, IReadOnlyDictionary<Guid, object> worlds,
        IReadOnlyDictionary<Guid, object> lights)
    {
        var failures = new List<string>();
        IBone? Bone(SceneBoneAttachment? saved) => saved != null
            && actors.TryGetValue(saved.ActorKey, out var token) && token is IActor actor
                ? _skeletons.GetSkeletons(actor).Where(s => s.Slot == saved.Slot)
                    .SelectMany(s => s.Bones).FirstOrDefault(b => b.PartialId == saved.PartialId
                        && b.BoneName == saved.BoneName) : null;
        SelectionId? Entity(SceneStructureRef? saved)
        {
            if (saved == null) return null;
            IEntityBindings bindings = _bindings;
            return saved.Kind switch
            {
                "light" when lights.TryGetValue(saved.Key, out var token) && token is ILight light
                    && bindings.GetLightId(light) is { } id => SelectionId.ForLight(id),
                "prop" when props.TryGetValue(saved.Key, out var token) && token is IPropHandle prop
                    && bindings.GetPropId(prop) is { } id => SelectionId.ForProp(id),
                "worldObject" when worlds.TryGetValue(saved.Key, out var token) && token is IWorldObject world
                    && bindings.GetWorldObjectId(world) is { } id => SelectionId.ForWorldObject(id),
                _ => null,
            };
        }
        foreach (var actor in scene.Actors)
        {
            if (actor.Fabrik == null || !actors.ContainsKey(actor.Key)) continue;
            foreach (var saved in actor.Fabrik)
            {
                var tip = Bone(new() { ActorKey = actor.Key, Slot = saved.Slot,
                    PartialId = saved.Partial, BoneName = saved.Endpoint });
                if (tip == null || saved.Config.Fabrik is not { } control)
                { failures.Add($"{actor.Name}: FABRIK endpoint {saved.Endpoint} is unavailable."); continue; }
                FabrikTarget Rebind(FabrikTarget target, SceneBoneAttachment? bone, SceneStructureRef? entity) =>
                    target with { Bone = Bone(bone) is { } live ? _bindings.GetBoneId(live) : null,
                        Entity = Entity(entity) };
                var restored = control with
                {
                    Root = Rebind(control.Root, saved.RootBone, saved.RootEntity),
                    Tip = Rebind(control.Tip, saved.TipBone, saved.TipEntity),
                };
                bool Missing(FabrikTarget target) => target.Mode switch
                { IkTargetMode.Bone => target.Bone == null, IkTargetMode.Entity => target.Entity == null, _ => false };
                if (Missing(restored.Root) || Missing(restored.Tip))
                    failures.Add($"{actor.Name}: an endpoint target for {saved.Endpoint} is unavailable.");
                if (_bonePosing.RestoreFabrik(tip, saved.Config with { Fabrik = restored }) is { } error)
                    failures.Add($"{actor.Name}: {error}");
            }
        }
        return failures;
    }
}
