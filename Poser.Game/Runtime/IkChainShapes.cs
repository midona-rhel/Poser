using System;
using System.Collections.Generic;
using System.Numerics;
using System.Linq;
using Dalamud.Game;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using FFXIVClientStructs.Havok.Animation.Rig;
using FFXIVClientStructs.Havok.Common.Base.Math.QsTransform;
using FFXIVClientStructs.Havok.Common.Base.Math.Quaternion;
using FFXIVClientStructs.Havok.Common.Base.Math.Vector;
using Poser.Core;
using Poser.Domain.Posing;
using Poser.Domain.Transforms;
using Poser.Entities;
using Poser.Domain.Identity;
using Poser.Services;

using GameSkeleton = FFXIVClientStructs.FFXIV.Client.Graphics.Render.Skeleton;

namespace Poser.Game;

/// <summary>
/// The shape of an IK chain on a skeleton, read off bone topology alone:
/// which bones each solver moves, whether a bone can anchor CCD, and a scene
/// target's validated world transform. Pure; holds nothing.
/// </summary>
internal static class IkChainShapes
{
    /// <summary>Brio's <c>EligibleForIK</c> — a parent for the solver to walk
    /// into, and not a hidden one (<c>Brio/Game/Posing/Skeletons/Bone.cs:68</c>).
    /// Native CCD also requires that parent to belong to the same Havok pose,
    /// matching the partial/skeleton boundary in its parent traversal.</summary>
    internal static bool IsCcdEligible(IBone bone) =>
        bone is not VirtualBone &&
        bone.ParentBone is { IsHiddenBone: false } parent &&
        parent.PartialId == bone.PartialId && ReferenceEquals(parent.Skeleton, bone.Skeleton);

    /// <summary>Which bones the configured solver actually moves. CCD walks
    /// the endpoint's own parents to the configured depth — the same walk
    /// IKService.GetBonesToDepth and IkBakeCapture.AffectedBones make, because
    /// the chain is not declared anywhere to read it from.</summary>
    internal static IReadOnlyList<string> ChainMemberNames(
        IBone endpoint,
        Poser.Domain.Posing.IkChainConfig config)
    {
        if (config is { Solver: IkSolver.Fabrik or IkSolver.Rope, Fabrik: { } control })
            return control.Bones.Select(b => b.Name).ToArray();
        if (config.Solver is IkSolver.Fabrik or IkSolver.Rope)
            return FabrikMembers(endpoint, config).Select(b => b.BoneName).ToArray();
        var names = new List<string> { endpoint.BoneName };
        if (config.Solver != Poser.Domain.Posing.IkSolver.TwoJoint)
            return NativeIkMembers(endpoint, config).AsEnumerable().Reverse().Select(b => b.BoneName).ToArray();

        if (Poser.Domain.Posing.IkChains.ForEndpoint(endpoint.BoneName)
            is not { } definition)
            return names;
        names.Add(definition.Endpoint);
        names.Add(definition.FirstJoint);
        names.Add(definition.SecondJoint);
        if (definition.FirstTwist != null)
            names.Add(definition.FirstTwist);
        if (definition.SecondTwist != null)
            names.Add(definition.SecondTwist);
        return names;
    }

    internal static Transform? ResolveIkEntityTransform(IEntityBindings bindings, SelectionId target)
    {
        Transform? transform = target switch
        {
            { Prop: { } prop } => bindings.Resolve(prop) is { Success: true, Value: { } live }
                ? live.Transform : (Transform?)null,
            { WorldObject: { } world } => bindings.Resolve(world) is { Success: true, Value: { } live }
                ? live.Transform : (Transform?)null,
            { Light: { } light } => bindings.Resolve(light) is { Success: true, Value: { } live }
                ? live.Transform : (Transform?)null,
            _ => null,
        };
        // Resolve the exact stable generation each time; never retain a native handle.
        return transform is { } value
            && Domain.Transforms.TransformMath.IsFinite(value.Position)
            && Domain.Transforms.TransformMath.IsFinite(value.Rotation)
            && value.Rotation.LengthSquared() > 1e-6f
                ? value : (Transform?)null;
    }

    internal static bool HasFabrikChildren(IBone bone) => bone.ChildBones.Any(b =>
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
}
