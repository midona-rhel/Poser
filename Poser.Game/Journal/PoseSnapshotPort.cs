using Poser.Application.Posing;
using Poser.Application.Lifecycle;
using Poser.Domain.Identity;
using Poser.Domain.Posing;
using Dalamud.Plugin.Services;
using Poser.Domain.Operations;
using Poser.Application.Scene;
using Poser.Application.Transforms;
using Poser.Entities;
using Poser.Files;
using Poser.Game.Bindings;
using Poser.Game.Posing;
using Poser.Services;

namespace Poser.Game.Journal;

/// <summary>
/// The journal's snapshots on the live runtime: a capture is the actor's
/// pose file plus its armed IK chains; a restore is a full-scope pose
/// import with history suppressed (it IS an undo), and the chains are
/// re-armed once the import has landed.
/// </summary>
public sealed class PoseSnapshotPort : IPoseSnapshotPort, IDisposable
{
    private readonly SceneSession _scene;
    private readonly StableBindingRegistry _bindings;
    private readonly ISkeletonService _skeletons;
    private readonly IPoseFileService _poseFiles;
    private readonly IBonePosingService _posing;
    private readonly IPoseImportCommands _imports;
    private readonly IPluginLog _log;
    private readonly IFramework _framework;
    private readonly ISessionGenerationSource _sessions;
    private PendingRestore? _pending;
    private bool _disposed;

    private sealed record PendingRestore(ActorId Actor, SessionGeneration Session,
        ActorSnapshot Snapshot, Func<bool> StillCurrent, Action<bool> Finished, long Started);

    public PoseSnapshotPort(
        SceneSession scene,
        StableBindingRegistry bindings,
        ISkeletonService skeletons,
        IPoseFileService poseFiles,
        IBonePosingService posing,
        IPoseImportCommands imports,
        IPluginLog log,
        IFramework framework,
        ISessionGenerationSource sessions)
    {
        _scene = scene;
        _bindings = bindings;
        _skeletons = skeletons;
        _poseFiles = poseFiles;
        _posing = posing;
        _imports = imports;
        _log = log;
        _framework = framework;
        _sessions = sessions;
        _framework.Update += OnUpdate;
    }

    private IActor? Live(Guid lineage)
    {
        if (_scene.Snapshot.FindActor(lineage) is not { } descriptor)
            return null;
        var resolved = _bindings.Resolve(descriptor.Id);
        return resolved.Success ? resolved.Value : null;
    }

    public ActorSnapshot? Capture(Guid lineage)
        => Capture(lineage, authoredOnly: false);

    public ActorSnapshot? CaptureAuthored(Guid lineage)
        => Capture(lineage, authoredOnly: true);

    private ActorSnapshot? Capture(Guid lineage, bool authoredOnly)
    {
        if (Live(lineage) is not { } actor)
            return null;
        var slots = _skeletons.GetSkeletons(actor);
        if (!ActorPoseReadiness.IsReady(slots, _bindings))
            return null;
        // Only the bones the user authored, plus the roots. A restore clears
        // every layer and re-authors these alone, so a bone the animation
        // was driving goes back to the animation instead of freezing at the
        // pose it happened to hold when the snapshot was taken.
        object pose;
        try
        {
            pose = authoredOnly ? AuthoredPoseState.Capture(slots, _posing)
                : _poseFiles.CreatePoseFile(slots, bone => bone.IsSkeletonRoot || _posing.HasModifications(bone));
        }
        catch (Exception ex)
        {
            _log.Warning($"Journal snapshot failed: {ex.Message}");
            return null;
        }
        var chains = new List<IkChainSnapshot>();
        foreach (var slot in slots)
            foreach (var chain in _posing.GetIkChains(slot))
                if (chain.Config.Enabled && _bindings.GetBoneId(chain.Endpoint) is { } endpoint)
                    chains.Add(new IkChainSnapshot(endpoint, chain.Config.Fabrik != null
                        ? _posing.SnapshotFabrik(chain.Endpoint) ?? chain.Config : chain.Config));
        return new ActorSnapshot(lineage, pose, chains);
    }

    public bool Restore(ActorSnapshot snapshot, Action<bool> finished) =>
        Restore(snapshot, () => true, finished);

    public bool Restore(ActorSnapshot snapshot, Func<bool> stillCurrent, Action<bool> finished)
    {
        if (_disposed || !_framework.IsInFrameworkUpdateThread || _pending != null || !stillCurrent()
            || _sessions.ActiveSessionGeneration is not { IsValid: true } session
            || Live(snapshot.Lineage) is not { } actor || _bindings.GetActorId(actor) is not { } actorId
            || snapshot.Pose is not (PoseFile or AuthoredPoseState))
            return false;

        // Model changes tear down draw objects synchronously, but replacement
        // skeletons and their registry publication arrive on later frames.
        // Keep IDs, not the old native skeleton, while waiting for that edge.
        _pending = new PendingRestore(actorId, session, snapshot, stillCurrent, finished,
            System.Environment.TickCount64);
        return true;
    }

    private void OnUpdate(IFramework framework)
    {
        if (_pending is not { } pending) return;
        try
        {
            if (!pending.StillCurrent() || _sessions.ActiveSessionGeneration != pending.Session
                || _bindings.Resolve(pending.Actor) is not { Success: true, Value: { } actor })
            {
                Complete(false);
                return;
            }
            if (System.Environment.TickCount64 - pending.Started >= 10_000)
            {
                _log.Warning("Journal restore timed out waiting for the actor's skeleton.");
                Complete(false);
                return;
            }
            var skeletons = _skeletons.GetSkeletons(actor);
            if (!ActorPoseReadiness.IsReady(skeletons, _bindings)) return;
            if (pending.Snapshot.Pose is AuthoredPoseState authored)
            {
                if (!authored.CanRestore(skeletons)) { Complete(false); return; }
                authored.Restore(skeletons, _posing);
                Complete(Rearm(pending.Actor, pending.Snapshot));
                return;
            }
            _pending = null;
            BeginImport(pending);
        }
        catch (Exception ex)
        {
            _log.Warning($"Journal restore failed: {ex.Message}");
            _pending = null;
            pending.Finished(false);
        }
    }

    private void Complete(bool ok)
    {
        var pending = _pending;
        _pending = null;
        pending?.Finished(ok);
    }

    private void BeginImport(PendingRestore pending)
    {
        var options = new PoseImportOptions
        {
            ApplyRotation = true,
            ApplyPosition = true,
            ApplyScale = true,
            ApplyBody = true,
            ApplyFace = true,
            ApplyMainHand = true,
            ApplyOffHand = true,
            ApplyProp = true,
            ApplyOrnament = true,
            ApplyModelTransform = true,
            ResetBeforeImport = true,
            SuppressHistory = true,
        };
        bool done = false;
        var begun = _imports.ImportPose(
            pending.Actor, (PoseFile)pending.Snapshot.Pose, options, "Restore pose",
            onReceipt: receipt =>
            {
                if (done || receipt.State == OperationReceiptState.Pending)
                    return;
                done = true;
                bool ok = !_disposed && pending.StillCurrent()
                    && _sessions.ActiveSessionGeneration == pending.Session
                    && receipt.State == OperationReceiptState.Applied;
                if (ok)
                    ok = Rearm(pending.Actor, pending.Snapshot);
                pending.Finished(ok);
            });
        if (!begun.Success && !done)
        {
            done = true;
            _log.Warning($"Journal restore refused: {begun.Detail}");
            pending.Finished(false);
        }
    }

    public void Dispose()
    {
        _disposed = true;
        _framework.Update -= OnUpdate;
        Complete(false);
    }

    private bool Rearm(ActorId actorId, ActorSnapshot snapshot)
    {
        if (_bindings.Resolve(actorId) is not { Success: true, Value: { } actor }) return false;
        var slots = _skeletons.GetSkeletons(actor);
        IBone? LocalBone(BoneId saved) => slots.Where(s => s.Slot == saved.Slot)
            .SelectMany(s => s.Bones).FirstOrDefault(b =>
                b.PartialId == saved.PartialId && b.BoneName == saved.CanonicalName);
        bool restored = true;
        foreach (var chain in snapshot.IkChains)
        {
            // A snapshot is intentionally portable across this actor's redraw;
            // its old BoneIds cannot resolve the replacement skeleton.
            if (LocalBone(chain.Endpoint) is not { } endpoint)
            {
                _log.Warning($"Journal restore could not find IK endpoint {chain.Endpoint.CanonicalName}.");
                restored = false;
                continue;
            }
            var config = chain.Config;
            if (config.Fabrik is { } control)
            {
                FabrikTarget Rebind(FabrikTarget point) => point.Bone is { } bone
                    && bone.Skeleton.Actor.LogicalId == snapshot.Lineage
                    ? point with { Bone = LocalBone(bone) is { } live ? _bindings.GetBoneId(live) : null }
                    : point;
                config = config with { Fabrik = control with
                    { Root = Rebind(control.Root), Tip = Rebind(control.Tip), Handle = Rebind(control.Handle) } };
            }
            if ((config.Fabrik != null ? _posing.RestoreFabrik(endpoint, config)
                : _posing.SetIkConfiguration(endpoint, config)) is { } refusal)
            {
                _log.Warning($"Journal restore could not re-arm {chain.Endpoint.CanonicalName}: {refusal}");
                restored = false;
            }
        }
        return restored;
    }
}
