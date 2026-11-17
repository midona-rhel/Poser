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
    /// <summary>Switching End rotation moves nothing: the switch records the
    /// forearm, the end bone and the user's end rotation, and the solve goes on
    /// from them. A chain enabled again with a record retakes it, since the
    /// limb may have moved while it was off.</summary>
    private IkChainConfig PrepareEndAnchor(IBone endpoint, IkChainConfig config, IkChainConfig? previous)
    {
        if (config.Solver != IkSolver.TwoJoint || IkChains.ForEndpoint(endpoint.BoneName) is not { } definition)
            return config.EndAnchor == null ? config : config with { EndAnchor = null };
        bool switched = previous != null && previous.EnforceEndRotation != config.EnforceEndRotation;
        bool reenabled = previous is { Enabled: false } && config.EndAnchor != null;
        if (!config.Enabled || !(switched || reenabled))
            return config;
        var chain = ResolveChain(endpoint, definition);
        return chain.TwoJointAvailable && _held.CaptureEndAnchor(endpoint, chain.SecondJoint) is { } anchor
            ? config with { EndAnchor = anchor } : config;
    }

    public IkChainConfig PrepareIkConfiguration(IBone endpoint, IkChainConfig config)
    {
        var previous = GetIkConfiguration(endpoint);
        config = PrepareEndAnchor(endpoint, config, previous);
        // While this chain is on, the skeleton is in the pose pass, so the raw caches hold the
        // pose the last solve left.
        bool solving = previous is { Enabled: true };
        bool fromFabrik = solving && previous!.Solver is IkSolver.Fabrik or IkSolver.Rope;
        if (config.Solver is not (IkSolver.Fabrik or IkSolver.Rope))
        {
            // This solver will move the limb, so the shape FABRIK kept no longer describes
            // it. With it dropped here, switching back to FABRIK takes the shape afresh,
            // while undo, redo and a loaded scene bring their own saved shape and keep it.
            if (config.Enabled && config.Fabrik != null)
                config = config with { Fabrik = null };
            // FABRIK and Rope keep no held target. The new target is the end bone where they
            // left it, so the switch moves nothing.
            if (config.Enabled && fromFabrik)
                config = _held.AnchorOnSolvedEnd(endpoint, config);
            else if (config.Enabled && previous is { Enabled: true } && previous.TargetMode != config.TargetMode
                && _ikChains.TryGetValue(ChainKey(endpoint), out var state))
                config = _held.ChangeTargetMode(state, endpoint, config);
            // Prepare before the value journal captures its after-state too:
            // changing CCD depth changes which parent owns the target frame.
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
            // The raw caches are only current while the chain is solving. Otherwise this
            // capture is a placeholder, marked so the pass takes it again.
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

    /// <summary>Captures the chain from the raw caches: the pose the last solve left, before
    /// Customize+. FABRIK writes what it captured straight back into that pose, so capturing
    /// the pose on screen would apply Customize+'s edits a second time.</summary>
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
        FabrikTarget Point(int index) => new(IkTargetMode.Actor,
            members[index].LastRawTransform.Position, members[index].LastRawTransform.Rotation,
            poses[index].AuthoredPosition, poses[index].AuthoredRotation);
        var targetBone = GetIkBoneTarget(tip) is { } anchor ? _bindings.GetBoneId(anchor) : null;
        var handle = config.Fabrik?.Handle
            ?? FabrikTargetAt(tip, tip.LastRawTransform, config.TargetMode, targetBone, GetIkEntityTarget(tip))
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
            // Capture at most 50 parents + handle + 50 children once, before
            // solving. In particular Rope's arc samples have shorter chords:
            // recapturing them on a depth edit silently shortens the rest span.
            var potential = FabrikMembers(tip, config with { ParentDepth = IkChainConfig.MaxDepth, ChildDepth = 0 })
                .Concat(FabrikMembers(tip, config with { ParentDepth = 0, ChildDepth = IkChainConfig.MaxDepth }).Skip(1))
                .ToArray();
            var reference = potential.Select(bone =>
            {
                var displayed = bone.LastRawTransform;
                var applied = _stacks.ToApplySpace(bone, displayed);
                var authored = _stacks.GetIkModification(bone) ?? Transform.Identity;
                var saved = control?.Bones.FirstOrDefault(b => b.Name == bone.BoneName && b.Partial == bone.PartialId);
                var pose = saved ?? new FabrikBonePose(bone.BoneName, bone.PartialId,
                    applied.Position, applied.Rotation, authored.Position, authored.Rotation);
                if (saved != null)
                    displayed = _stacks.FromApplySpace(bone, new Transform(saved.Position, saved.Rotation, Vector3.One));
                var anchor = new FabrikTarget(IkTargetMode.Actor, displayed.Position, displayed.Rotation,
                    pose.AuthoredPosition, pose.AuthoredRotation, HoldRotation: false);
                // Old saved controls have no reference span: retain their exact
                // authored endpoints and seed rather than recapturing solved bones.
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
        var model = endpoint.LastRawTransform;
        if (config.Enabled && config.Fabrik is { } control
            && ResolveFabrikTarget(endpoint, control.Handle, true) is { } held)
            model = _stacks.FromApplySpace(endpoint, new Transform(held.Position, held.Rotation, model.Scale));
        return FabrikTargetAt(endpoint, model, mode, bone, entity);
    }

    /// <summary>A FABRIK target at this model-space point, stored the way its mode needs:
    /// as is for Actor, as a world point for World, as an offset from the followed bone or
    /// entity otherwise. Null when the followed bone or entity can't be found.</summary>
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
        // Rotation/scale gestures must not pay off a stale translation target.
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
        // Reach is measured in the solver's pre-reparent frame. Only the
        // distance limit feeds back into authored coordinates, never contact lag.
        var displayed = _stacks.FromApplySpace(bone, new Transform(handle.Value.Position, Quaternion.Identity, Vector3.One));
        // A collision-solved bone can be well short of its authored target.
        // Mouse input starts at the gizmo, not at that hidden target. Preserve
        // the gesture's input frame; never re-read the moving solved endpoint.
        // The gizmo sits on the bone as shown, after Customize+. The chain lives in the pose
        // the solver works on, so the input is moved back by Customize+'s edit of this bone.
        _ = bone.LastTransform;
        RefreshCache(bone);
        var input = displayed with
        {
            Position = inputPosition is { } shown
                ? shown - (bone.LastTransform.Position - bone.LastRawTransform.Position)
                : displayed.Position,
        };
        var inputApplied = _stacks.ToApplySpace(bone, input);
        var requested = _stacks.ToApplySpace(bone, input with { Position = input.Position + delta });
        var source = control.Bones.Select(b => b.Position).ToArray();
        var limited = FabrikSolver.MoveHandle(source, control.HandleIndex, root.Value.Position, tip.Value.Position,
            inputApplied.Position, requested.Position - inputApplied.Position);
        // Pose stacks store an offset from the held target; presentation stores
        // an offset from the input position. They agree on the accepted point,
        // not on the delta when contact has displaced the visible bone.
        var start = fromAuthoredBaseline ? displayed.Position : input.Position;
        return _stacks.FromApplySpace(bone, requested with { Position = limited }).Position - start;
    }

    public void ApplyFabrikControls(SkeletonKey key, Skeleton skeleton)
    {
        if (_ikImports.Contains(key.Actor)) return;
        foreach (var (identity, state) in _ikChains)
        {
            if (identity.Skeleton != key || state.Config is not
                { Enabled: true, Solver: IkSolver.Fabrik or IkSolver.Rope, Fabrik: { } control } config) continue;
            if (control.Bones.Length < 2) continue; // Both spans disabled; retain the authored target for re-enabling.
            var tip = skeleton.GetBone(identity.Partial, identity.Bone);
            if (tip == null) continue;
            if (control.Unseated)
            {
                // The first frame leaves the chain unsolved, so the pass refreshes the raw
                // caches with the pose FABRIK must start from. The next frame captures it.
                if (!state.FabrikWaited)
                {
                    state.FabrikWaited = true;
                    continue;
                }
                if (CaptureFabrikReference(tip, config with { Fabrik = null }) is not { } seated)
                    continue;
                // A picked bone or entity stays what the handle follows.
                var follows = control.Handle;
                if (follows.Mode is IkTargetMode.Bone or IkTargetMode.Entity
                    && FabrikTargetAt(tip, tip.LastRawTransform, follows.Mode, follows.Bone, follows.Entity) is { } followed)
                    seated = seated with { Handle = followed };
                // The waiting frame moved the end bone by its own position edits, as with IK
                // off. They are where the handle is, not part of the limb's shape, so the
                // captured bone goes back to where the pass found it. Left in, the move
                // would count as bone length. The pass only records that for a bone with
                // edits; an older record of a bone without them is out of date.
                if (_stacks.HasModifications(tip))
                {
                    var found = _stacks.GetAnimatedBaseline(tip).Position;
                    seated = seated with
                    {
                        Bones = seated.Bones.Select((bone, i) => i == seated.HandleIndex
                            ? bone with { Position = found } : bone).ToArray(),
                        ReferenceBones = seated.ReferenceBones?.Select(reference =>
                            reference.Pose.Name == tip.BoneName && reference.Pose.Partial == tip.PartialId
                                ? reference with { Pose = reference.Pose with { Position = found } }
                                : reference).ToArray(),
                    };
                }
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
            ReferenceBones = null, // A copy/preview starts a new skeleton-local reference capture.
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
