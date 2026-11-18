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
using Poser.Domain.Posing;
using Poser.Domain.Transforms;
using Poser.Domain.Identity;
using Poser.Game.Entities;
using Poser.Game.Services;

using GameSkeleton = FFXIVClientStructs.FFXIV.Client.Graphics.Render.Skeleton;

namespace Poser.Game;

/// <summary>
/// Works out an IK chain from how a skeleton's bones are connected: which
/// bones each IK method moves, which bones the game's own CCD IK can use, and
/// where a target object in the scene is right now.
/// </summary>
internal static class IkChainShapes
{
    /// <summary>Whether the game's own CCD IK can use this bone. As in Brio,
    /// the bone needs a parent that is not hidden. That parent must also be in
    /// the same part of the skeleton (body, face, hair and so on), because the
    /// game's IK cannot move bones across parts.</summary>
    internal static bool IsCcdEligible(IBone bone) =>
        bone is not VirtualBone &&
        bone.ParentBone is { IsHiddenBone: false } parent &&
        parent.PartialId == bone.PartialId && ReferenceEquals(parent.Skeleton, bone.Skeleton);

    /// <summary>Which bones the chosen IK method moves. For CCD this walks up
    /// from the end bone through its parents, as many as the depth setting
    /// allows. Other IK code repeats the same walk, because no list of a
    /// chain's bones is stored anywhere.</summary>
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

    /// <summary>Where a world point sits as seen from the bone or entity it follows. Kept this
    /// way, the point moves and turns with what it follows, like a child of it.</summary>
    internal static (Vector3 Position, Quaternion Rotation) RelativeTo(
        Transform followed, Vector3 position, Quaternion rotation)
    {
        var back = Quaternion.Inverse(followed.Rotation);
        return (Vector3.Transform(position - followed.Position, back), Quaternion.Normalize(back * rotation));
    }

    /// <summary>The world point for an offset kept by <see cref="RelativeTo"/>.</summary>
    internal static (Vector3 Position, Quaternion Rotation) FromRelative(
        Transform followed, Vector3 position, Quaternion rotation) =>
        (followed.Position + Vector3.Transform(position, followed.Rotation),
            Quaternion.Normalize(followed.Rotation * rotation));

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
        // Look the object up every time instead of keeping a pointer to it: the game can remove or replace it.
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
            // The game's pose data cannot reach bones in a different part of the skeleton.
            if (bone != tip && bone.IsHiddenBone || bone.PartialId != tip.PartialId || !ReferenceEquals(bone.Skeleton, tip.Skeleton)) break;
            result.Add(bone);
        }
        result.Reverse();
        var child = tip;
        for (int i = 0; i < config.ChildDepth; i++)
        {
            var children = child.ChildBones.Where(b => !b.IsHiddenBone
                && b.PartialId == tip.PartialId && ReferenceEquals(b.Skeleton, tip.Skeleton)).ToArray();
            // Where the bones branch, a depth cannot say which way to go, so stop instead of guessing.
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
