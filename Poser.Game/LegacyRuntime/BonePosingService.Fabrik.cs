using System.Numerics;
using Poser.Core;
using Poser.Domain.Identity;
using Poser.Domain.Posing;
using Poser.Entities;

namespace Poser.Game;

public unsafe partial class BonePosingService
{
    private static bool HasFabrikChildren(IBone bone) => bone.ChildBones.Any(b =>
        !b.IsHiddenBone && b.PartialId == bone.PartialId && ReferenceEquals(b.Skeleton, bone.Skeleton));

    internal static List<IBone> FabrikMembers(IBone tip, IkChainConfig config)
    {
        var result = new List<IBone>();
        for (IBone? bone = tip; bone != null && result.Count <= config.ParentDepth; bone = bone.ParentBone)
        {
            // A Havok pose cannot address indices from a different partial.
            if (bone != tip && bone.IsHiddenBone || bone.PartialId != tip.PartialId || !ReferenceEquals(bone.Skeleton, tip.Skeleton)) break;
            result.Add(bone);
        }
        result.Reverse();
        var child = tip;
        for (int i = 0; i < config.ChildDepth; i++)
        {
            var children = child.ChildBones.Where(b => !b.IsHiddenBone
                && b.PartialId == tip.PartialId && ReferenceEquals(b.Skeleton, tip.Skeleton)).ToArray();
            // A depth does not identify a branch: stop rather than picking one arbitrarily.
            if (children.Length != 1) break;
            child = children[0];
            result.Add(child);
        }
        return result;
    }

    public IkChainConfig PrepareIkConfiguration(IBone endpoint, IkChainConfig config)
    {
        if (config.Solver is not (IkSolver.Fabrik or IkSolver.Rope)) return config;
        var previous = GetIkConfiguration(endpoint);
        if (config.Fabrik == null && config.Enabled
            || config.Fabrik != null && previous != null && ReferenceEquals(config.Fabrik, previous.Fabrik)
                && (config.ParentDepth != previous.ParentDepth || config.ChildDepth != previous.ChildDepth))
            return config with { Fabrik = CaptureFabrikControl(endpoint, config) };
        return config;
    }

    private bool FabrikOverlap(IBone tip, IkChainConfig config)
    {
        if (!config.Enabled) return false;
        var members = FabrikMembers(tip, config);
        return GetIkChains(tip.Skeleton).Any(other => other.Config.Enabled
            && !ReferenceEquals(other.Endpoint, tip) && other.Endpoint.PartialId == tip.PartialId
            && members.Any(b => other.Bones.Contains(b.BoneName)));
    }

    private FabrikControl? CaptureFabrikControl(IBone tip, IkChainConfig config)
    {
        var members = FabrikMembers(tip, config);
        if (members.Count == 0) return null;
        RefreshCache(tip);
        var poses = members.Select(bone =>
        {
            var pose = ToApplySpace(bone, bone.LastTransform);
            var authored = GetIkModification(bone) ?? Transform.Identity;
            return new FabrikBonePose(bone.BoneName, bone.PartialId, pose.Position, pose.Rotation,
                authored.Position, authored.Rotation);
        }).ToArray();
        FabrikTarget Point(int index) => new(IkTargetMode.Actor,
            members[index].LastTransform.Position, members[index].LastTransform.Rotation,
            poses[index].AuthoredPosition, poses[index].AuthoredRotation);
        var targetBone = GetIkBoneTarget(tip) is { } anchor ? _bindings.GetBoneId(anchor) : null;
        var handle = config.Fabrik?.Handle
            ?? CaptureFabrikTarget(tip, config.TargetMode, targetBone, GetIkEntityTarget(tip))
            ?? Point(members.IndexOf(tip));
        return new(poses, Point(0) with { HoldRotation = false },
            Point(poses.Length - 1) with { HoldRotation = false }, config.SwivelDegrees,
            members.IndexOf(tip), handle);
    }

    public FabrikTarget? CaptureFabrikTarget(IBone endpoint, IkTargetMode mode,
        BoneId? bone = null, SelectionId? entity = null)
    {
        var config = GetIkConfiguration(endpoint);
        if (config?.Solver is not (IkSolver.Fabrik or IkSolver.Rope)) return null;
        var source = endpoint;
        RefreshCache(source);
        var authored = GetIkModification(source) ?? Transform.Identity;
        var model = source.LastTransform;
        if (mode == IkTargetMode.Actor)
            return new(mode, model.Position, model.Rotation, authored.Position, authored.Rotation);
        if (source.Skeleton is not Skeleton skeleton) return null;
        var frame = skeleton.GetModelMatrix();
        var position = Vector3.Transform(model.Position, frame);
        var rotation = Quaternion.Normalize(Transform.FromMatrix(frame).Rotation * model.Rotation);
        Transform? anchor = mode switch
        {
            IkTargetMode.Bone when bone is { } id && _bindings.Resolve(id) is { Success: true, Value: { } live }
                => BoneWorld.Of(live),
            IkTargetMode.Entity when entity is { } id => ResolveIkEntityTransform(_bindings, id),
            _ => null,
        };
        if (mode is IkTargetMode.Bone or IkTargetMode.Entity && anchor == null) return null;
        if (anchor is { } followed)
        {
            position -= followed.Position;
            rotation = Quaternion.Normalize(Quaternion.Inverse(followed.Rotation) * rotation);
        }
        return new(mode, position, rotation, authored.Position, authored.Rotation, bone, entity);
    }

    private (Vector3 Position, Quaternion Rotation)? ResolveFabrikTarget(
        IBone bone, FabrikTarget target, bool movable)
    {
        var position = target.Position;
        var rotation = target.Rotation;
        if (target.Mode != IkTargetMode.Actor)
        {
            Transform? anchor = target.Mode switch
            {
                IkTargetMode.Bone when target.Bone is { } id
                    && _bindings.Resolve(id) is { Success: true, Value: { } live } => BoneWorld.Of(live),
                IkTargetMode.Entity when target.Entity is { } id => ResolveIkEntityTransform(_bindings, id),
                _ => null,
            };
            if (target.Mode is IkTargetMode.Bone or IkTargetMode.Entity && anchor == null) return null;
            if (anchor is { } followed)
            {
                position += followed.Position;
                rotation = Quaternion.Normalize(followed.Rotation * rotation);
            }
            if (bone.Skeleton is not Skeleton skeleton
                || !Matrix4x4.Invert(skeleton.GetModelMatrix(), out var inverse)) return null;
            position = Vector3.Transform(position, inverse);
            rotation = Quaternion.Normalize(Quaternion.Inverse(
                Transform.FromMatrix(skeleton.GetModelMatrix()).Rotation) * rotation);
        }
        var applied = ToApplySpace(bone, new Transform(position, rotation, Vector3.One));
        position = applied.Position;
        rotation = applied.Rotation;
        if (movable)
        {
            var authored = GetIkModification(bone) ?? Transform.Identity;
            position += authored.Position - target.AuthoredPosition;
            rotation = Quaternion.Normalize(rotation * Quaternion.Inverse(target.AuthoredRotation) * authored.Rotation);
        }
        return Domain.Transforms.TransformMath.IsFinite(position)
            && Domain.Transforms.TransformMath.IsFinite(rotation) && rotation.LengthSquared() > 1e-8f
            ? (position, rotation) : null;
    }

    private void ApplyFabrikControls(SkeletonKey key, Skeleton skeleton)
    {
        if (_ikImports.Contains(key.Actor)) return;
        foreach (var (identity, state) in _ikChains)
        {
            if (identity.Skeleton != key || state.Config is not
                { Enabled: true, Solver: IkSolver.Fabrik or IkSolver.Rope, Fabrik: { } control } config) continue;
            if (control.Bones.Length < 2) continue; // Both spans disabled; retain the authored target for re-enabling.
            var tip = skeleton.GetBone(identity.Partial, identity.Bone);
            if (tip == null) continue;
            var members = FabrikMembers(tip, config);
            if (members.Count != control.Bones.Length
                || members.Where((b, i) => b.BoneName != control.Bones[i].Name
                    || b.PartialId != control.Bones[i].Partial).Any()) continue;
            var rootTarget = ResolveFabrikTarget(members[0], control.Root, false);
            var tipTarget = ResolveFabrikTarget(members[^1], control.Tip, false);
            var handleTarget = ResolveFabrikTarget(tip, control.Handle, true);
            if (rootTarget is not { } root || tipTarget is not { } end || handleTarget is not { } handle) continue;
            _ikService.Solve(tip, new IkSolveRequest(handle.Position, handle.Rotation,
                config, state.Chain, root.Position, root.Rotation, end.Position, state.CollisionState));
        }
    }

    public IkChainConfig? SnapshotFabrik(IBone tip, bool modelSpace = false)
    {
        var config = GetIkConfiguration(tip);
        if (config?.Solver is not (IkSolver.Fabrik or IkSolver.Rope)) return config;
        var control = config.Fabrik;
        if (control == null) return config;
        // Capture the owned visible route for scenes/copies/previews; copying
        // the pre-collision seed would unwrap it in the new skeleton instance.
        if (config.Collisions && CaptureFabrikControl(tip, config) is { } visible)
            control = control with { Bones = visible.Bones, SwivelBaseline = visible.SwivelBaseline };
        var members = FabrikMembers(tip, config);
        FabrikTarget Capture(IBone bone, FabrikTarget target, bool movable)
        {
            var resolved = ResolveFabrikTarget(bone, target, movable);
            if (resolved is not { } value || bone.Skeleton is not Skeleton skeleton) return target;
            var visible = FromApplySpace(bone, new Transform(value.Position, value.Rotation, Vector3.One));
            var position = visible.Position;
            var rotation = visible.Rotation;
            if (!modelSpace && target.Mode != IkTargetMode.Actor)
            {
                var frame = skeleton.GetModelMatrix();
                position = Vector3.Transform(position, frame);
                rotation = Quaternion.Normalize(Transform.FromMatrix(frame).Rotation * rotation);
                Transform? anchor = target.Mode switch
                {
                    IkTargetMode.Bone when target.Bone is { } id && _bindings.Resolve(id)
                        is { Success: true, Value: { } live } => BoneWorld.Of(live),
                    IkTargetMode.Entity when target.Entity is { } id => ResolveIkEntityTransform(_bindings, id),
                    _ => null,
                };
                if (anchor is { } followed)
                {
                    position -= followed.Position;
                    rotation = Quaternion.Normalize(Quaternion.Inverse(followed.Rotation) * rotation);
                }
            }
            return target with { Mode = modelSpace ? IkTargetMode.Actor : target.Mode,
                Position = position, Rotation = rotation, AuthoredPosition = Vector3.Zero,
                AuthoredRotation = Quaternion.Identity, Bone = modelSpace ? null : target.Bone,
                Entity = modelSpace ? null : target.Entity };
        }
        return config with { Fabrik = control with
        {
            Root = Capture(members[0], control.Root, false),
            Tip = Capture(members[^1], control.Tip, false),
            Handle = Capture(tip, control.Handle, true),
        } };
    }

    public string? RestoreFabrik(IBone tip, IkChainConfig config)
    {
        if (config.Solver is not (IkSolver.Fabrik or IkSolver.Rope) || config.Fabrik is not { } control)
            return SetIkConfiguration(tip, config);
        var members = FabrikMembers(tip, config);
        if (members.Count != control.Bones.Length) return "The saved FABRIK chain does not match this skeleton.";
        FabrikTarget Baseline(FabrikTarget target, IBone bone)
        {
            var authored = GetIkModification(bone) ?? Transform.Zero;
            return target with { AuthoredPosition = authored.Position, AuthoredRotation = authored.Rotation };
        }
        return SetIkConfiguration(tip, config with { Fabrik = control with
            { Handle = Baseline(control.Handle, tip) } });
    }

    private Transform FromApplySpace(IBone bone, Transform applied)
    {
        var root = bone;
        while (!root.IsPartialRoot && root.ParentBone is { } parent && parent.PartialId == bone.PartialId)
            root = parent;
        if (!root.IsPartialRoot || root.IsSkeletonRoot) return applied;
        return _partialFrames.TryGetValue((SkeletonKey.Of(bone.Skeleton), bone.PartialId, root.BoneIndex), out var frame)
            && frame.Before.Scale.X != 0 && frame.Before.Scale.Y != 0 && frame.Before.Scale.Z != 0
                ? frame.ToDisplay(applied) : applied;
    }
}
