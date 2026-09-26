using Poser.Domain.Identity;
using Poser.Application.Scene;
using Poser.Domain.Posing;
using Poser.Entities;
using Poser.Files;
using Poser.Services;

namespace Poser.Game.Scene;

internal sealed partial class SceneRuntimeAdapter
{
    public async Task WaitForFabrikBindings(IEnumerable<SceneEntityHandle> entities, CancellationToken cancellation)
    {
        var tokens = entities.ToArray();
        // Spawning precedes scene publication. Wait for actual binding admission,
        // not a guessed delay, before remapping portable target keys.
        for (int attempt = 0; attempt < 40; attempt++)
        {
            cancellation.ThrowIfCancellationRequested();
            if (await OnFramework(() => tokens.All(token => ResolveSceneEntity(token) != null))) return;
            await Task.Delay(50, cancellation);
        }
        // Unavailable references are reported by RestoreFabrik, not rebound by name.
    }

    public IReadOnlyList<string> RestoreFabrik(SceneFile scene, IReadOnlyDictionary<Guid, SceneEntityHandle> actors,
        IReadOnlyDictionary<Guid, SceneEntityHandle> props, IReadOnlyDictionary<Guid, SceneEntityHandle> worlds,
        IReadOnlyDictionary<Guid, SceneEntityHandle> lights)
    {
        var failures = new List<string>();
        IBone? Bone(SceneBoneAttachment? saved) => saved != null
            && actors.TryGetValue(saved.ActorKey, out var token) && _handles.Resolve<IActor>(token, SceneEntityKind.Actor) is { } actor
                ? _skeletons.GetSkeletons(actor).Where(s => s.Slot == saved.Slot)
                    .SelectMany(s => s.Bones).FirstOrDefault(b => b.PartialId == saved.PartialId
                        && b.BoneName == saved.BoneName) : null;
        SelectionId? Entity(SceneStructureRef? saved)
        {
            if (saved == null) return null;
            IEntityBindings bindings = _bindings;
            return saved.Kind switch
            {
                "light" when lights.TryGetValue(saved.Key, out var token) && _handles.Resolve<ILight>(token, SceneEntityKind.Light) is { } light
                    && bindings.GetLightId(light) is { } id => SelectionId.ForLight(id),
                "prop" when props.TryGetValue(saved.Key, out var token) && _handles.Resolve<IPropHandle>(token, SceneEntityKind.Prop) is { } prop
                    && bindings.GetPropId(prop) is { } id => SelectionId.ForProp(id),
                "worldObject" when worlds.TryGetValue(saved.Key, out var token) && _handles.Resolve<IWorldObject>(token, SceneEntityKind.WorldObject) is { } world
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
                    Handle = Rebind(control.Handle, saved.HandleBone, saved.HandleEntity),
                };
                bool Missing(FabrikTarget target) => target.Mode switch
                { IkTargetMode.Bone => target.Bone == null, IkTargetMode.Entity => target.Entity == null, _ => false };
                if (Missing(restored.Handle))
                    failures.Add($"{actor.Name}: an endpoint target for {saved.Endpoint} is unavailable.");
                if (_bonePosing.RestoreFabrik(tip, saved.Config with { Fabrik = restored }) is { } error)
                    failures.Add($"{actor.Name}: {error}");
            }
        }
        return failures;
    }
}
