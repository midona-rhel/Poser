using System.Numerics;
using Poser.Application.Animation;
using Poser.Application.Integration;
using Poser.Application.Presentation;
using Poser.Domain.Animation;
using Poser.Domain.Companions;
using Poser.Domain.Identity;
using Poser.Domain.Posing;
using Poser.Domain.Presentation;
using Poser.Entities;
using Poser.Services;

namespace Poser.Game.Scene;

internal sealed record ActorRuntimeState(
    ActorId? OriginalId, int ModelId, ActorAppearanceSnapshot? Appearance,
    PresentationOverrides? Presentation, ActorAnimationReading? Animation,
    AnimationOverrides AnimationOverrides, CompanionAttachment? Companion,
    ActorState? CompanionState, GazeState Gaze, ActorId? GazeTarget,
    bool EyesLocked, bool HeadLocked, bool BodyLocked,
    IReadOnlyList<LifecycleIk> Ik)
{
    public CompanionKind? SpawnedKind { get; init; }
    public Integration.SpawnCollectionSnapshot? InheritedCollection { get; init; }
}

internal sealed record LifecycleIk(PoseSlot Slot, int Partial, string Bone,
    IkChainConfig Config, BoneId? TargetBone, SelectionId? TargetEntity);

internal sealed partial class ActorServiceLifecycle
{
    private readonly ActorPresentationSession _presentation;
    private readonly AnimationSession _animation;
    private readonly Integration.ISpawnCollectionPort _collections;

    public IActor? Recreate(ActorState state) => state.Runtime?.SpawnedKind is { } kind
        ? _spawns.SpawnCatalogActor(new(kind, 0, "", "", 0, state.Runtime.ModelId))
        : _spawns.SpawnNewActor(reserveCompanionSlot: true);

    private ActorRuntimeState CaptureRuntime(IActor actor)
    {
        var id = _bindings.GetActorId(actor);
        var appearance = id is { } bound ? _integration.CaptureHistory(bound) : null;
        var collection = _collections.CaptureInheritedCollection(actor.Address);
        if (!collection.Success) Note($"'{actor.Name}': {collection.Detail}");
        if (collection.Value is not null && appearance is not null)
            appearance = appearance with { Collection = null };
        if (appearance?.StateJson is null && appearance?.McdfPath is null)
            Note($"'{actor.Name}': appearance could not be captured for lifecycle history.");
        var companion = actor.IsCompanion ? null : _spawns.GetCompanionInfo(actor);
        var child = companion is null ? null : _spawns.GetCompanionActor(actor);
        var gaze = _gaze.GetGazeState(actor);
        var targetAddress = _gaze.GetGazeTargetAddress(actor);
        var target = _actorManager.Actors.FirstOrDefault(x => x.Address == targetAddress);
        var chains = new List<LifecycleIk>();
        foreach (var skeleton in _skeletons.GetSkeletons(actor))
            foreach (var chain in _bonePosing.GetIkChains(skeleton))
                chains.Add(new(skeleton.Slot, chain.Endpoint.PartialId, chain.Endpoint.BoneName,
                    chain.Config, _bonePosing.GetIkBoneTarget(chain.Endpoint) is { } bone
                        ? _bindings.GetBoneId(bone) : null,
                    _bonePosing.GetIkEntityTarget(chain.Endpoint)));
        return new ActorRuntimeState(id, _spawns.GetModelCharaId(actor), appearance,
            id is { } p ? _presentation.OverridesFor(p) : null,
            id is { } a ? _animation.Read(a) : null,
            id is { } o ? _animation.OverridesFor(o) : AnimationOverrides.None,
            companion, child is null ? null : Read(child),
            new GazeState { Mode = gaze.Mode, TargetType = gaze.TargetType,
                Position = gaze.Position, EyesPosition = gaze.EyesPosition,
                HeadPosition = gaze.HeadPosition, BodyPosition = gaze.BodyPosition },
            target is null ? null : _bindings.GetActorId(target),
            _gaze.IsPartLocked(actor, GazeTargetType.Eyes),
            _gaze.IsPartLocked(actor, GazeTargetType.Head),
            _gaze.IsPartLocked(actor, GazeTargetType.Body), chains)
        { SpawnedKind = _spawns.GetSpawnedKind(actor), InheritedCollection = collection.Value };
    }

    private void PrepareRuntime(IActor actor, ActorState state, int attempts,
        Func<bool>? current, int phase = 0)
    {
        if (actor.Address == 0 || current?.Invoke() == false) return;
        if (attempts <= 0) { Note($"'{actor.Name}': lifecycle restore never became ready."); return; }
        var runtime = state.Runtime!;
        void Next(int nextPhase, int ticks = 1) => _framework.RunOnTick(
            () => PrepareRuntime(actor, state, attempts - 1, current, nextPhase), delayTicks: ticks);
        if (_bindings.GetActorId(actor) is not { } id || !_poses.HasPosableSkeleton(actor)
            || _poses.IsImportBusy || _integration.McdfBusy)
        { Next(phase); return; }
        // A redraw can publish a skeleton before its stable bone bindings.
        var skeletons = _skeletons.GetSkeletons(actor);
        if (!SceneRuntimeAdapter.HasCharacterSkeleton(skeletons) || skeletons.Any(s =>
            s.RootBone is not { } root || _bindings.GetBoneId(root) is null))
        { Next(phase); return; }
        if (phase == 0)
        {
            if (runtime.InheritedCollection is { } collection)
            {
                var result = _collections.RestoreInheritedCollection(actor.Address, collection);
                if (!result.Success) Note($"'{actor.Name}': {result.Detail}");
            }
            if (runtime.Appearance is { } appearance)
            {
                var result = _integration.RestoreHistory(id, appearance);
                if (!result.Success) Note($"'{actor.Name}': {result.Detail}");
            }
            // Appearance/collection application may queue its redraw for the next tick.
            Next(1, 3);
            return;
        }
        if (phase == 1)
        {
            if (_spawns.GetModelCharaId(actor) != runtime.ModelId)
                _spawns.SetModelCharaId(actor, runtime.ModelId);
            if (!actor.IsCompanion && _spawns.GetCompanionInfo(actor) != runtime.Companion)
                if (!_spawns.SetCompanion(actor, runtime.Companion))
                    Note($"'{actor.Name}': companion attachment could not be restored.");
            Next(2, 3);
            return;
        }
        if (runtime.Animation is { } animation)
        {
            var result = _animation.RestoreHistory(id, animation, runtime.AnimationOverrides);
            if (!result.Success) Note($"'{actor.Name}': {result.Detail}");
        }
        Schedule(actor, state, ReadyAttempts, current);
    }

    private void RestoreRuntime(IActor target, ActorRuntimeState? state, Func<bool>? current)
    {
        if (state is null || target.Address == 0 || _bindings.GetActorId(target) is not { } id) return;
        var presentation = _presentation.RestoreOverrides(id, state.Presentation);
        if (!presentation.Success) Note($"'{target.Name}': {presentation.Detail}");
        if (state.Animation is { } animation)
        {
            var time = _animation.RestoreHistoryTimes(id, animation);
            if (!time.Success) Note($"'{target.Name}': {time.Detail}");
            // Pose import freezes the body; lifecycle undo restores its captured playback afterwards.
            var playback = _animation.RestoreHistoryPlayback(id, animation);
            if (!playback.Success) Note($"'{target.Name}': {playback.Detail}");
        }
        var gaze = state.Gaze;
        _gaze.SetGazeMode(target, gaze.Mode);
        if (state.GazeTarget is { } targetId &&
            (targetId == state.OriginalId ? target : _bindings.Resolve(targetId).Value) is { } gazeActor)
            _gaze.SetGazeTarget(target, gazeActor);
        if (gaze.Mode != GazeTargetMode.None)
            _gaze.SetGazeParts(target, gaze.TargetType);
        _gaze.SetGazePosition(target, gaze.Position);
        _gaze.SetPartPosition(target, GazeTargetType.Eyes, gaze.EyesPosition);
        _gaze.SetPartPosition(target, GazeTargetType.Head, gaze.HeadPosition);
        _gaze.SetPartPosition(target, GazeTargetType.Body, gaze.BodyPosition);
        _gaze.SetPartLock(target, GazeTargetType.Eyes, state.EyesLocked);
        _gaze.SetPartLock(target, GazeTargetType.Head, state.HeadLocked);
        _gaze.SetPartLock(target, GazeTargetType.Body, state.BodyLocked);
        IBone? LocalBone(PoseSlot slot, int partial, string name) => _skeletons.GetSkeletons(target)
            .Where(x => x.Slot == slot).SelectMany(x => x.Bones)
            .FirstOrDefault(x => x.PartialId == partial && x.BoneName == name);
        foreach (var saved in state.Ik)
        {
            if (LocalBone(saved.Slot, saved.Partial, saved.Bone) is not { } endpoint) continue;
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
