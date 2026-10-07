using System.Numerics;
using FFXIVClientStructs.Havok.Animation.Rig;
using Poser.Domain.Posing;
using Poser.Domain.Transforms;
using Poser.Entities;

namespace Poser.Game;

public unsafe partial class BonePosingService
{
    private static PoseTransform AnchorPose(Transform value) => new(value.Position, value.Rotation, value.Scale);
    internal static List<IBone> NativeIkMembers(IBone endpoint, IkChainConfig config)
    {
        if (config.Solver == IkSolver.TwoJoint && IkChains.ForEndpoint(endpoint.BoneName) is { } definition)
        {
            IBone? Find(string name) => endpoint.Skeleton.Bones.FirstOrDefault(b =>
                b.PartialId == endpoint.PartialId && b.BoneName == name);
            return Find(definition.FirstJoint) is { } first && Find(definition.SecondJoint) is { } second
                ? [first, second, endpoint] : [];
        }
        var members = new List<IBone> { endpoint };
        for (var parent = endpoint.ParentBone; parent != null && members.Count <= config.CcdDepth; parent = parent.ParentBone)
        {
            if (parent.PartialId != endpoint.PartialId || !ReferenceEquals(parent.Skeleton, endpoint.Skeleton)) break;
            members.Add(parent);
        }
        members.Reverse();
        return members;
    }

    private IkActorAnchor CaptureActorAnchor(IBone endpoint, IkChainConfig config)
    {
        var members = NativeIkMembers(endpoint, config);
        var parent = members.FirstOrDefault()?.ParentBone;
        if (parent?.PartialId != endpoint.PartialId) parent = null;
        _ = endpoint.LastTransform;
        if (parent != null) _ = parent.LastTransform;
        RefreshCache(endpoint);
        var model = ToApplySpace(endpoint, endpoint.LastTransform);
        var authored = GetIkModification(endpoint) ?? Transform.Identity;
        return new(model.Position, model.Rotation, authored.Position, authored.Rotation)
        {
            ParentName = parent?.BoneName, ParentPartial = parent?.PartialId ?? 0,
            ParentTransform = parent == null ? null : AnchorPose(ToApplySpace(parent, parent.LastTransform)),
        };
    }

    private PoseTransform? ActorParentFrame(IBone endpoint, IkActorAnchor anchor, hkaPose* pose = null)
    {
        if (anchor.ParentName == null) return null;
        var parent = endpoint.Skeleton.Bones.FirstOrDefault(b => b.PartialId == anchor.ParentPartial
            && b.BoneName == anchor.ParentName);
        if (parent == null) return null;
        // During evaluation, caches may precede an edited grandparent. Read
        // this pass's model transform; the chosen parent is outside the solve.
        if (pose != null && parent.PartialId == endpoint.PartialId)
        {
            var native = pose->AccessBoneModelSpace(parent.BoneIndex, hkaPose.PropagateOrNot.DontPropagate);
            return native == null ? null : AnchorPose(ReadTransform(native));
        }
        return AnchorPose(ToApplySpace(parent, parent.LastTransform));
    }

    private Vector3 ClampNativeIkTranslation(IBone bone, IkChainConfig config, Vector3 delta, bool fromAuthoredBaseline)
    {
        if (!config.EnforceConstraints || !_ikChains.TryGetValue(ChainKey(bone), out var state)) return delta;
        var members = NativeIkMembers(bone, config);
        if (members.Count < 2) return delta;
        foreach (var member in members) _ = member.LastTransform;
        RefreshCache(bone);
        var authored = GetIkModification(bone) ?? Transform.Identity;
        if (ResolveHeld(state, bone, authored.Position, authored.Rotation) is not { } handle) return delta;
        var points = members.Select(member => ToApplySpace(member, member.LastTransform).Position).ToArray();
        var reach = IkReach.FromChain(points, config);
        var displayed = FromApplySpace(bone, new Transform(handle.Position, Quaternion.Identity, Vector3.One));
        var requested = ToApplySpace(bone, displayed with { Position = displayed.Position + delta });
        var limited = reach.Move(handle.Position, requested.Position - handle.Position);
        var start = fromAuthoredBaseline ? displayed.Position
            : FromApplySpace(bone, displayed with { Position = reach.Clamp(handle.Position) }).Position;
        return FromApplySpace(bone, displayed with { Position = limited }).Position - start;
    }
}
