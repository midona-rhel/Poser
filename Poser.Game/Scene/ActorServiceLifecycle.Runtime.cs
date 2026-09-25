using System.Numerics;
using Poser.Application.Integration;
using Poser.Application.Posing;
using Poser.Domain.Companions;
using Poser.Domain.Identity;
using Poser.Domain.Posing;
using Poser.Domain.Presentation;
using Poser.Entities;
using Poser.Services;

using Poser.Domain.Scene;

namespace Poser.Game.Scene;

internal sealed record ActorRuntimeState(
    ActorId? OriginalId, ActorPropertiesSnapshot Properties, CompanionAttachment? Companion,
    ActorState? CompanionState,
    IReadOnlyList<LifecycleIk> Ik)
{
    public int ModelId => Properties.ModelId;
    public CompanionKind? SpawnedKind { get; init; }
    public Posing.AuthoredPoseState Pose { get; init; } = new([]);
    public Integration.SpawnCollectionSnapshot? InheritedCollection { get; init; }
}

internal sealed record LifecycleIk(PoseSlot Slot, int Partial, string Bone,
    IkChainConfig Config, BoneId? TargetBone, SelectionId? TargetEntity);

internal sealed partial class ActorServiceLifecycle
{
    private readonly ActorStateSnapshots _actorStates;
    private readonly Integration.ISpawnCollectionPort _collections;

    public void CopyBodyProfile(IActor source, IActor target)
    {
        if (_bindings.GetActorId(source) is not { } sourceId) return;
        var captured = _integration.CaptureBodyProfile(sourceId);
        if (!captured.Success)
        {
            Note($"'{target.Name}': {captured.Detail}");
            return;
        }
        if (captured.Value is not { } profile) return;
        WhenPosable(target, ready =>
        {
            if (_bindings.GetActorId((IActor)ready) is not { } targetId) return;
            var result = _integration.ApplyBodyProfileJson(targetId, profile, "Copied profile");
            if (!result.Success) Note($"'{target.Name}': {result.Detail}");
        });
    }

    public IActor? Recreate(ActorState state) => state.Runtime?.SpawnedKind is { } kind
        ? _spawns.SpawnCatalogActor(new(kind, 0, "", "", 0, state.Runtime.ModelId))
        : _spawns.SpawnNewActor(reserveCompanionSlot: true);

    private ActorRuntimeState CaptureRuntime(IActor actor)
    {
        var id = _bindings.GetActorId(actor);
        if (id is not { } bound) throw new InvalidOperationException("The actor has no stable identity.");
        var collection = _integration.OverridesFor(bound).Mcdf == null
            ? _collections.CaptureInheritedCollection(actor.Address)
            : Poser.Domain.Integration.IntegrationValue<Integration.SpawnCollectionSnapshot?>.Ok(null);
        if (!collection.Success) throw new InvalidOperationException(collection.Detail);
        var captured = _actorStates.CaptureProperties(bound, captureCollection: collection.Value == null);
        if (!captured.Success || captured.Value is not { } properties)
            throw new InvalidOperationException(captured.Detail ?? "Actor state capture failed.");
        var companion = actor.IsCompanion ? null : _spawns.GetCompanionInfo(actor);
        var child = companion is null ? null : _spawns.GetCompanionActor(actor);
        var chains = new List<LifecycleIk>();
        foreach (var skeleton in _skeletons.GetSkeletons(actor))
            foreach (var chain in _bonePosing.GetIkChains(skeleton))
                chains.Add(new(skeleton.Slot, chain.Endpoint.PartialId, chain.Endpoint.BoneName,
                    chain.Config.Fabrik != null ? _bonePosing.SnapshotFabrik(chain.Endpoint) ?? chain.Config : chain.Config,
                    _bonePosing.GetIkBoneTarget(chain.Endpoint) is { } bone
                        ? _bindings.GetBoneId(bone) : null,
                    _bonePosing.GetIkEntityTarget(chain.Endpoint)));
        return new ActorRuntimeState(id, properties, companion, child is null ? null : Read(child), chains)
        {
            SpawnedKind = _spawns.GetSpawnedKind(actor), InheritedCollection = collection.Value,
            Pose = Posing.AuthoredPoseState.Capture(_skeletons.GetSkeletons(actor), _bonePosing),
        };
    }

    private void PrepareRuntime(IActor actor, ActorState state, int attempts,
        Func<bool>? current)
    {
        if (actor.Address == 0 || current?.Invoke() == false) return;
        if (attempts <= 0) { Note($"'{actor.Name}': lifecycle restore never became ready."); return; }
        var runtime = state.Runtime!;
        void Next() => _framework.RunOnTick(
            () => PrepareRuntime(actor, state, attempts - 1, current), delayTicks: 1);
        if (_bindings.GetActorId(actor) is not { } id || !_poses.HasPosableSkeleton(id)
            || _poses.IsImportBusy || _integration.McdfBusy)
        { Next(); return; }
        // A redraw can publish a skeleton before its stable bone bindings.
        var skeletons = _skeletons.GetSkeletons(actor);
        if (!Poser.Game.Posing.ActorPoseReadiness.IsReady(skeletons, _bindings))
        { Next(); return; }
        if (runtime.InheritedCollection is { } collection)
        {
            var result = _collections.RestoreInheritedCollection(actor.Address, collection);
            if (!result.Success) { Note($"'{actor.Name}': {result.Detail}"); return; }
        }
        _ = PrepareAppearance();

        async Task PrepareAppearance()
        {
            bool Current() => actor.Address != 0 && current?.Invoke() != false && _bindings.GetActorId(actor) == id;
            try
            {
                var ready = await _actorStates.RestoreAppearance(id, runtime.Properties, Current, CancellationToken.None);
                await _framework.RunOnFrameworkThread(() =>
                {
                    if (!Current()) return;
                    if (!ready.Success) { Note($"'{actor.Name}': {ready.Detail}"); return; }
                    if (!actor.IsCompanion && _spawns.GetCompanionInfo(actor) != runtime.Companion
                        && !_spawns.SetCompanion(actor, runtime.Companion))
                    { Note($"'{actor.Name}': companion attachment could not be restored."); return; }
                    Schedule(actor, state, ReadyAttempts, Current);
                });
            }
            catch (Exception ex) { Note($"Lifecycle appearance restore failed: {ex.Message}"); }
        }
    }

    private void RestoreRuntime(IActor target, ActorRuntimeState? state, Func<bool>? current)
    {
        if (state is null || target.Address == 0 || _bindings.GetActorId(target) is not { } id) return;
        var properties = state.Properties;
        if (properties.Gaze is { Target: { } gazeTarget } savedGaze && gazeTarget == state.OriginalId)
            properties = properties with { Gaze = savedGaze with { Target = id } };
        var restored = _actorStates.RestoreProperties(id, properties);
        if (!restored.Success) Note($"'{target.Name}': {restored.Detail}");
        IBone? LocalBone(PoseSlot slot, int partial, string name) => _skeletons.GetSkeletons(target)
            .Where(x => x.Slot == slot).SelectMany(x => x.Bones)
            .FirstOrDefault(x => x.PartialId == partial && x.BoneName == name);
        foreach (var saved in state.Ik)
        {
            if (LocalBone(saved.Slot, saved.Partial, saved.Bone) is not { } endpoint) continue;
            if (saved.Config is { Solver: Poser.Domain.Posing.IkSolver.Fabrik or Poser.Domain.Posing.IkSolver.Rope, Fabrik: { } control })
            {
                Poser.Domain.Posing.FabrikTarget Rebind(Poser.Domain.Posing.FabrikTarget point) =>
                    point.Bone is { } anchor && anchor.Skeleton.Actor == state.OriginalId
                    ? point with { Bone = LocalBone(anchor.Slot, anchor.PartialId, anchor.CanonicalName) is { } live
                        ? _bindings.GetBoneId(live) : null } : point;
                _bonePosing.RestoreFabrik(endpoint, saved.Config with { Fabrik = control with
                    { Handle = Rebind(control.Handle) } });
                continue;
            }
            _bonePosing.SetIkConfiguration(endpoint, saved.Config);
            if (saved.TargetBone is { } boneId)
            {
                var bone = boneId.Skeleton.Actor == state.OriginalId
                    ? LocalBone(boneId.Slot, boneId.PartialId, boneId.CanonicalName)
                    : _bindings.Resolve(boneId).Value;
                if (bone is not null) _bonePosing.SetIkBoneTarget(endpoint, bone);
            }
            if (saved.TargetEntity is { } entity)
                _bonePosing.SetIkEntityTarget(endpoint, state.OriginalId is { } original && entity.Actor == original
                    ? SelectionId.ForActor(id) : entity);
        }
        // Owner placement/pose comes first: mounting changes the child's reference frame.
        if (state.CompanionState is { } childState && _spawns.GetCompanionActor(target) is { } child)
            Restore(child, childState, current);
    }
}
