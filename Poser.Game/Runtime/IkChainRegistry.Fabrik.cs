using System.Numerics;
using Poser.Domain.Identity;
using Poser.Domain.Posing;
using Poser.Domain.Transforms;
using Poser.Game.Entities;

using static Poser.Game.IkChainShapes;
using static Poser.Game.IkHeldTargets;

namespace Poser.Game;

internal sealed unsafe partial class IkChainRegistry
{
    /// <summary>Switching End rotation moves nothing. The switch saves the
    /// forearm, the end bone and the chosen end rotation as they are, and IK goes
    /// on from there. When IK is turned on again for a chain with such a save,
    /// the save is taken again, because the limb may have moved while IK was
    /// off.</summary>
    private IkChainConfig PrepareEndAnchor(IBone endpoint, IkChainConfig config, IkChainConfig? previous)
    {
        if (config.Solver != IkSolver.TwoJoint || IkChains.ForEndpoint(endpoint.BoneName) == null)
            return config.EndAnchor == null ? config : config with { EndAnchor = null };
        bool switched = previous != null && previous.EnforceEndRotation != config.EnforceEndRotation;
        bool reenabled = previous is { Enabled: false } && config.EndAnchor != null;
        if (!config.Enabled || !(switched || reenabled))
            return config;
        var chain = ResolveChain(endpoint);
        return chain.TwoJointAvailable && _held.CaptureEndAnchor(endpoint, chain.SecondJoint) is { } anchor
            ? config with { EndAnchor = anchor } : config;
    }

    public IkChainConfig PrepareIkConfiguration(IBone endpoint, IkChainConfig config)
    {
        _held.CustomizePlus.Prepare(endpoint);
        var previous = GetIkConfiguration(endpoint);
        config = PrepareEndAnchor(endpoint, config, previous);
        // While IK is on for this chain, Poser poses the skeleton every frame, so the bone
        // positions Poser keeps hold the pose IK last produced.
        bool solving = previous is { Enabled: true };
        bool fromFabrik = solving && previous!.Solver is IkSolver.Fabrik or IkSolver.Rope;
        if (config.Solver is not (IkSolver.Fabrik or IkSolver.Rope))
        {
            // This solver will move the limb, so the shape FABRIK kept no longer matches
            // it. It is dropped here, so switching back to FABRIK reads the shape again.
            // Undo, redo and a loaded scene bring their own saved shape and keep it.
            if (config.Enabled && config.Fabrik != null)
                config = config with { Fabrik = null };
            // FABRIK and Rope keep no held target. The new target is the end bone where they
            // left it, so the switch moves nothing.
            if (config.Enabled && fromFabrik)
                config = _held.AnchorOnSolvedEnd(endpoint, config);
            else if (config.Enabled && previous is { Enabled: true } && previous.TargetMode != config.TargetMode
                && TryGetChain(endpoint, out var state))
                config = _held.ChangeTargetMode(state, endpoint, config);
            // This also runs before the change is recorded for undo, so undo keeps
            // the result. Changing the CCD depth changes which parent bone the
            // target is measured from.
            return config.Enabled && config.TargetMode == IkTargetMode.Actor
                && (config.ActorAnchor == null || previous != null && config.ActorAnchor == previous.ActorAnchor
                    && (config.Solver != previous.Solver || config.CcdDepth != previous.CcdDepth))
                ? config with { ActorAnchor = _held.CaptureActorAnchor(endpoint, config) } : config;
        }
        if (config.Fabrik is { } control && config.TargetMode is IkTargetMode.Actor or IkTargetMode.World
            && control.Handle.Mode != config.TargetMode
            && CaptureFabrikTarget(endpoint, config.TargetMode) is { } handle)
            config = config with { Fabrik = control with { Handle = handle } };
        if (config.Fabrik == null && config.Enabled)
            // The bone positions Poser keeps are only up to date while IK runs on this chain.
            // Otherwise this reading is a placeholder, marked so a later frame reads it again.
            return CaptureFabrikReference(endpoint, config) is { } captured
                ? config with { Fabrik = captured with { Unseated = !solving } } : config;
        if (config.Fabrik != null && previous != null && ReferenceEquals(config.Fabrik, previous.Fabrik)
            && (config.ParentDepth != previous.ParentDepth || config.ChildDepth != previous.ChildDepth))
            return CaptureFabrikReference(endpoint, config) is { } selected
                ? config with { Fabrik = selected } : config;
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

    /// <summary>Where Customize+ shows a bone posed at <paramref name="posed"/> (by default
    /// where the last pass posed it), in the space FABRIK targets keep. FABRIK targets are
    /// where a bone is shown; the chain itself is kept as posed.</summary>
    private Transform Shown(IBone bone, Transform? posed = null)
    {
        var at = posed ?? _stacks.ToApplySpace(bone, bone.LastRawTransform);
        var shift = _held.CustomizePlus.For(bone);
        return _stacks.FromApplySpace(bone,
            new Transform(shift.Shown(at.Position), shift.Shown(at.Rotation), at.Scale));
    }

    /// <summary>Reads the chain from the bone positions Poser keeps: the pose IK last produced,
    /// before Customize+ changes it. FABRIK writes what it reads straight back into that pose,
    /// so reading the pose on screen would apply Customize+'s changes a second time.</summary>
    private FabrikControl? CaptureFabrikControl(IBone tip, IkChainConfig config)
    {
        var members = FabrikMembers(tip, config);
        if (members.Count == 0) return null;
        var poses = members.Select(bone =>
        {
            var pose = _stacks.ToApplySpace(bone, bone.LastRawTransform);
            var authored = _stacks.GetIkModification(bone) ?? Transform.Identity;
            return new FabrikBonePose(bone.BoneName, bone.PartialId, pose.Position, pose.Rotation,
                authored.Position, authored.Rotation);
        }).ToArray();
        FabrikTarget Point(int index)
        {
            var shown = Shown(members[index]);
            return new(IkTargetMode.Actor, shown.Position, shown.Rotation,
                poses[index].AuthoredPosition, poses[index].AuthoredRotation);
        }
        var targetBone = GetIkBoneTarget(tip) is { } anchor ? _bindings.GetBoneId(anchor) : null;
        var handle = config.Fabrik?.Handle
            ?? FabrikTargetAt(tip, Shown(tip), config.TargetMode, targetBone, GetIkEntityTarget(tip))
            ?? Point(members.IndexOf(tip));
        return new(poses, Point(0) with { HoldRotation = false },
            Point(poses.Length - 1) with { HoldRotation = false }, config.SwivelDegrees,
            members.IndexOf(tip), handle);
    }

    private FabrikControl? CaptureFabrikReference(IBone tip, IkChainConfig config)
    {
        var members = FabrikMembers(tip, config);
        if (members.Count == 0) return null;
        var control = config.Fabrik;
        if (control?.ReferenceBones == null)
        {
            // Read up to 50 parents, the handle bone and up to 50 children once,
            // before IK moves them. Rope bends the chain into an arc, so its bones
            // sit closer together: reading them again after a depth change would
            // quietly make the chain shorter.
            var potential = FabrikMembers(tip, config with { ParentDepth = IkChainConfig.MaxDepth, ChildDepth = 0 })
                .Concat(FabrikMembers(tip, config with { ParentDepth = 0, ChildDepth = IkChainConfig.MaxDepth }).Skip(1))
                .ToArray();
            var reference = potential.Select(bone =>
            {
                var applied = _stacks.ToApplySpace(bone, bone.LastRawTransform);
                var displayed = Shown(bone, applied);
                var authored = _stacks.GetIkModification(bone) ?? Transform.Identity;
                var saved = control?.Bones.FirstOrDefault(b => b.Name == bone.BoneName && b.Partial == bone.PartialId);
                var pose = saved ?? new FabrikBonePose(bone.BoneName, bone.PartialId,
                    applied.Position, applied.Rotation, authored.Position, authored.Rotation);
                if (saved != null)
                    displayed = Shown(bone, new Transform(saved.Position, saved.Rotation, Vector3.One));
                var anchor = new FabrikTarget(IkTargetMode.Actor, displayed.Position, displayed.Rotation,
                    pose.AuthoredPosition, pose.AuthoredRotation, HoldRotation: false);
                // Chains saved by older versions have no reference bones. Keep their
                // saved end points as they are, rather than reading bones IK already moved.
                if (saved != null && ReferenceEquals(saved, control!.Bones[0])) anchor = control.Root;
                if (saved != null && ReferenceEquals(saved, control!.Bones[^1])) anchor = control.Tip;
                return new FabrikReferenceBone(pose, anchor);
            }).ToArray();
            control ??= CaptureFabrikControl(tip, config);
            if (control == null) return null;
            control = control with { ReferenceBones = reference };
        }
        return control.TrySelectSpan(members.Select(b => (b.BoneName, b.PartialId)).ToArray(), members.IndexOf(tip), out var selected)
            ? selected : null;
    }

    public FabrikTarget? CaptureFabrikTarget(IBone endpoint, IkTargetMode mode,
        BoneId? bone = null, SelectionId? entity = null)
    {
        var config = GetIkConfiguration(endpoint);
        if (config?.Solver is not (IkSolver.Fabrik or IkSolver.Rope)) return null;
        var model = Shown(endpoint);
        if (config.Enabled && config.Fabrik is { } control
            && ResolveFabrikTarget(endpoint, control.Handle, true) is { } held)
            model = _stacks.FromApplySpace(endpoint, new Transform(held.Position, held.Rotation, model.Scale));
        return FabrikTargetAt(endpoint, model, mode, bone, entity);
    }

    /// <summary>A FABRIK target at this point, given relative to the actor, and stored the
    /// way its mode needs: as is for Actor, as a world position for World, and as an offset
    /// from the followed bone or object otherwise. Null when the followed bone or object
    /// can't be found.</summary>
    private FabrikTarget? FabrikTargetAt(IBone source, Transform model, IkTargetMode mode,
        BoneId? bone, SelectionId? entity)
    {
        var authored = _stacks.GetIkModification(source) ?? Transform.Identity;
        if (mode == IkTargetMode.Actor)
            return new(mode, model.Position, model.Rotation, authored.Position, authored.Rotation);
        if (source.Skeleton is not Skeleton skeleton) return null;
        var frame = skeleton.GetModelTransform();
        var position = Vector3.Transform(model.Position, frame.ToMatrix());
        var rotation = Quaternion.Normalize(frame.Rotation * model.Rotation);
        Transform? anchor = mode switch
        {
            IkTargetMode.Bone when bone is { } id && _held.FindBone(id) is { } live
                => BoneWorld.Of(live),
            IkTargetMode.Entity when entity is { } id => ResolveIkEntityTransform(_bindings, id),
            _ => null,
        };
        if (mode is IkTargetMode.Bone or IkTargetMode.Entity && anchor == null) return null;
        if (anchor is { } followed)
            (position, rotation) = RelativeTo(followed, position, rotation);
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
                    && _held.FindBone(id) is { } live => BoneWorld.Of(live),
                IkTargetMode.Entity when target.Entity is { } id => ResolveIkEntityTransform(_bindings, id),
                _ => null,
            };
            if (target.Mode is IkTargetMode.Bone or IkTargetMode.Entity && anchor == null) return null;
            if (anchor is { } followed)
                (position, rotation) = FromRelative(followed, position, rotation);
            if (bone.Skeleton is not Skeleton skeleton) return null;
            var frame = skeleton.GetModelTransform();
            if (!Matrix4x4.Invert(frame.ToMatrix(), out var inverse)) return null;
            position = Vector3.Transform(position, inverse);
            rotation = Quaternion.Normalize(Quaternion.Inverse(frame.Rotation) * rotation);
        }
        var applied = _stacks.ToApplySpace(bone, new Transform(position, rotation, Vector3.One));
        position = applied.Position;
        rotation = applied.Rotation;
        if (movable)
        {
            var authored = _stacks.GetIkModification(bone) ?? Transform.Identity;
            position += authored.Position - target.AuthoredPosition;
            rotation = Quaternion.Normalize(rotation * Quaternion.Inverse(target.AuthoredRotation) * authored.Rotation);
        }
        return Domain.Transforms.TransformMath.IsFinite(position)
            && Domain.Transforms.TransformMath.IsFinite(rotation) && rotation.LengthSquared() > 1e-8f
            ? (position, rotation) : null;
    }

    public Vector3 ClampIkTranslation(IBone bone, Vector3 delta, bool fromAuthoredBaseline = false, Vector3? inputPosition = null)
    {
        // A rotate or scale drag does not move the bone, so it must not pull the bone toward an old move target.
        if (delta == Vector3.Zero) return delta;
        if (GetIkConfiguration(bone) is { Enabled: true, Solver: IkSolver.TwoJoint or IkSolver.Ccd } native)
            return ClampNativeIkTranslation(bone, native, delta, fromAuthoredBaseline);
        if (GetIkConfiguration(bone) is not
            { Enabled: true, Solver: IkSolver.Fabrik or IkSolver.Rope, Fabrik: { } control } config
            || control.Bones.Length < 2) return delta;
        var members = FabrikMembers(bone, config);
        if (members.Count != control.Bones.Length) return delta;
        var root = ResolveFabrikTarget(members[0], control.Root, false);
        var tip = ResolveFabrikTarget(members[^1], control.Tip, false);
        var handle = ResolveFabrikTarget(bone, control.Handle, true);
        if (root == null || tip == null || handle == null) return delta;
        // How far the chain can reach is measured in the pose IK works on. Only
        // that reach limit is passed back to the move, never a lag from collisions.
        // The targets and the gizmo are where the bones are shown, so the move is converted
        // to the pose IK works in and back.
        var displayed = _stacks.FromApplySpace(bone, new Transform(handle.Value.Position, Quaternion.Identity, Vector3.One));
        // With collisions on, a bone can stop well short of where it was dragged.
        // The drag starts at the gizmo, not at that hidden target, so the drag keeps
        // its own start point and never re-reads the end bone while IK moves it.
        var input = displayed with { Position = inputPosition ?? displayed.Position };
        var shift = _held.CustomizePlus.For(bone);
        var inputPosed = shift.Posed(_stacks.ToApplySpace(bone, input).Position);
        var requestedPosed = shift.Posed(_stacks.ToApplySpace(bone, input with { Position = input.Position + delta }).Position);
        var source = control.Bones.Select(b => b.Position).ToArray();
        var limited = FabrikSolver.MoveHandle(source, control.HandleIndex,
            _held.CustomizePlus.For(members[0]).Posed(root.Value.Position),
            _held.CustomizePlus.For(members[^1]).Posed(tip.Value.Position),
            inputPosed, requestedPosed - inputPosed);
        // The pose stores the move as an offset from the held target; the drag
        // stores it as an offset from where the drag started. Both reach the same
        // point, but their offsets differ when a collision has pushed the bone.
        var start = fromAuthoredBaseline ? displayed.Position : input.Position;
        return _stacks.FromApplySpace(bone, new Transform(shift.Shown(limited), Quaternion.Identity, Vector3.One)).Position - start;
    }

    public void ApplyFabrikControls(SkeletonKey key, Skeleton skeleton)
    {
        if (_ikImports.Contains(key.Actor)) return;
        foreach (var (identity, state) in _ikChains)
        {
            if (identity.Skeleton != key || state.Config is not
                { Enabled: true, Solver: IkSolver.Fabrik or IkSolver.Rope, Fabrik: { } control } config) continue;
            if (control.Bones.Length < 2) continue; // Both depths are zero; keep the target for when one is raised.
            var tip = skeleton.GetBoneByName(identity.Name, identity.Partial);
            if (tip == null) continue;
            UseCurrentBuild(state, tip);
            if (control.Unseated)
            {
                // On the first frame IK leaves the chain alone, so the bone positions Poser
                // keeps catch up with the pose FABRIK must start from. The next frame reads
                // that pose exactly as it stands, so switching FABRIK on moves nothing.
                if (!state.Build.FabrikWaited)
                {
                    state.Build.FabrikWaited = true;
                    continue;
                }
                if (CaptureFabrikReference(tip, config with { Fabrik = null }) is not { } seated)
                    continue;
                // A picked bone or scene object stays what the handle follows.
                var follows = control.Handle;
                if (follows.Mode is IkTargetMode.Bone or IkTargetMode.Entity
                    && FabrikTargetAt(tip, Shown(tip), follows.Mode, follows.Bone, follows.Entity) is { } followed)
                    seated = seated with { Handle = followed };
                state.Config = config = config with { Fabrik = control = seated };
            }
            var members = FabrikMembers(tip, config);
            if (members.Count != control.Bones.Length
                || members.Where((b, i) => b.BoneName != control.Bones[i].Name
                    || b.PartialId != control.Bones[i].Partial).Any()) continue;
            var rootTarget = ResolveFabrikTarget(members[0], control.Root, false);
            var tipTarget = ResolveFabrikTarget(members[^1], control.Tip, false);
            var handleTarget = ResolveFabrikTarget(tip, control.Handle, true);
            if (rootTarget is not { } root || tipTarget is not { } end || handleTarget is not { } handle) continue;
            // The targets are where the bones are shown; the solver needs them before Customize+.
            var rootShift = ShiftFor(state, members[0]);
            var handleShift = ShiftFor(state, tip);
            _ikService.Solve(tip, new IkSolveRequest(handleShift.Posed(handle.Position), handleShift.Posed(handle.Rotation),
                config, state.Build.Chain, rootShift.Posed(root.Position), rootShift.Posed(root.Rotation),
                ShiftFor(state, members[^1]).Posed(end.Position), state.Build.CollisionState));
        }
    }

    public IkChainConfig? SnapshotFabrik(IBone tip, bool modelSpace = false)
    {
        var config = GetIkConfiguration(tip);
        if (config?.Solver is not (IkSolver.Fabrik or IkSolver.Rope)) return config;
        var control = config.Fabrik;
        if (control == null) return config;
        // Scenes, copies and previews take the chain as it is shown. Copying the
        // shape from before collisions would undo its bend around the body on the
        // new skeleton.
        if (config.Collisions && CaptureFabrikControl(tip, config) is { } visible)
            control = control with { Bones = visible.Bones, SwivelBaseline = visible.SwivelBaseline };
        var members = FabrikMembers(tip, config);
        FabrikTarget Capture(IBone bone, FabrikTarget target, bool movable)
        {
            var resolved = ResolveFabrikTarget(bone, target, movable);
            if (resolved is not { } value || bone.Skeleton is not Skeleton skeleton) return target;
            var visible = _stacks.FromApplySpace(bone, new Transform(value.Position, value.Rotation, Vector3.One));
            var position = visible.Position;
            var rotation = visible.Rotation;
            if (!modelSpace && target.Mode != IkTargetMode.Actor)
            {
                var frame = skeleton.GetModelTransform();
                position = Vector3.Transform(position, frame.ToMatrix());
                rotation = Quaternion.Normalize(frame.Rotation * rotation);
                Transform? anchor = target.Mode switch
                {
                    IkTargetMode.Bone when target.Bone is { } id
                        && _held.FindBone(id) is { } live => BoneWorld.Of(live),
                    IkTargetMode.Entity when target.Entity is { } id => ResolveIkEntityTransform(_bindings, id),
                    _ => null,
                };
                if (anchor is { } followed)
                    (position, rotation) = RelativeTo(followed, position, rotation);
            }
            return target with { Mode = modelSpace ? IkTargetMode.Actor : target.Mode,
                Position = position, Rotation = rotation, AuthoredPosition = Vector3.Zero,
                AuthoredRotation = Quaternion.Identity, Bone = modelSpace ? null : target.Bone,
                Entity = modelSpace ? null : target.Entity };
        }
        return config with { Fabrik = control with
        {
            ReferenceBones = null, // A copy or preview reads its own reference bones on its new skeleton.
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
            var authored = _stacks.GetIkModification(bone) ?? Transform.Zero;
            return target with { AuthoredPosition = authored.Position, AuthoredRotation = authored.Rotation };
        }
        return SetIkConfiguration(tip, config with { Fabrik = control with
            { Handle = Baseline(control.Handle, tip) } });
    }
}
