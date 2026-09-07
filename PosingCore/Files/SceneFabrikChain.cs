using System;
using System.Numerics;
using Poser.Domain.Identity;
using Poser.Domain.Posing;

namespace Poser.Files;

public sealed record SceneFabrikChain(PoseSlot Slot, int Partial, string Endpoint,
    IkChainConfig Config, SceneBoneAttachment? RootBone, SceneStructureRef? RootEntity,
    SceneBoneAttachment? TipBone, SceneStructureRef? TipEntity)
{
    public static SceneFabrikChain Capture(PoseSlot slot, int partial, string endpoint, IkChainConfig config)
    {
        var control = config.Fabrik!;
        static SceneBoneAttachment? Bone(FabrikTarget target) => target.Bone is { } bone
            ? new() { ActorKey = bone.Skeleton.Actor.LogicalId, Slot = bone.Skeleton.Slot,
                PartialId = bone.PartialId, BoneName = bone.CanonicalName } : null;
        static SceneStructureRef? Entity(FabrikTarget target) => target.Entity switch
        {
            { Light: { } id } => new() { Kind = "light", Key = id.LogicalId },
            { Prop: { } id } => new() { Kind = "prop", Key = id.LogicalId },
            { WorldObject: { } id } => new() { Kind = "worldObject", Key = id.LogicalId },
            _ => null,
        };
        return new(slot, partial, endpoint, config with { Fabrik = control with
            { Root = control.Root with { Bone = null, Entity = null },
              Tip = control.Tip with { Bone = null, Entity = null } } },
            Bone(control.Root), Entity(control.Root), Bone(control.Tip), Entity(control.Tip));
    }

    public static void Rebase(SceneFile scene, Func<Vector3, Vector3> move, Quaternion turn)
    {
        FabrikTarget RebaseTarget(FabrikTarget target) => target.Mode switch
        {
            IkTargetMode.World => target with { Position = move(target.Position),
                Rotation = Quaternion.Normalize(turn * target.Rotation) },
            IkTargetMode.Bone or IkTargetMode.Entity => target with
                { Position = Vector3.Transform(target.Position, turn) },
            _ => target,
        };
        foreach (var actor in scene.Actors)
            if (actor.Fabrik is { } chains)
                for (int i = 0; i < chains.Count; i++)
                    if (chains[i].Config.Fabrik is { } control)
                        chains[i] = chains[i] with { Config = chains[i].Config with { Fabrik = control with
                            { Root = RebaseTarget(control.Root), Tip = RebaseTarget(control.Tip) } } };
    }
}
