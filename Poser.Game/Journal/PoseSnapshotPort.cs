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
public sealed class PoseSnapshotPort : IPoseSnapshotPort
{
    private readonly SceneSession _scene;
    private readonly StableBindingRegistry _bindings;
    private readonly ISkeletonService _skeletons;
    private readonly IPoseFileService _poseFiles;
    private readonly IBonePosingService _posing;
    private readonly CleanPoseFacade _poseFacade;
    private readonly IPluginLog _log;

    public PoseSnapshotPort(
        SceneSession scene,
        StableBindingRegistry bindings,
        ISkeletonService skeletons,
        IPoseFileService poseFiles,
        IBonePosingService posing,
        CleanPoseFacade poseFacade,
        IPluginLog log)
    {
        _scene = scene;
        _bindings = bindings;
        _skeletons = skeletons;
        _poseFiles = poseFiles;
        _posing = posing;
        _poseFacade = poseFacade;
        _log = log;
    }

    private IActor? Live(Guid lineage)
    {
        if (_scene.Snapshot.FindActor(lineage) is not { } descriptor)
            return null;
        var resolved = _bindings.Resolve(descriptor.Id);
        return resolved.Success ? resolved.Value : null;
    }

    public ActorSnapshot? Capture(Guid lineage)
    {
        if (Live(lineage) is not { } actor)
            return null;
        var slots = _skeletons.GetSkeletons(actor);
        if (slots.Count == 0)
            return null;
        PoseFile pose;
        try
        {
            pose = _poseFiles.CreatePoseFile(slots);
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
                    chains.Add(new IkChainSnapshot(endpoint, chain.Config));
        return new ActorSnapshot(lineage, pose, chains);
    }

    public bool Restore(ActorSnapshot snapshot, Action<bool> finished)
    {
        if (Live(snapshot.Lineage) is not { } actor || snapshot.Pose is not PoseFile pose)
            return false;
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
        var begun = _poseFacade.ImportPose(
            actor, pose, options, "Restore pose",
            onReceipt: receipt =>
            {
                if (done || receipt.State == OperationReceiptState.Pending)
                    return;
                done = true;
                bool ok = receipt.State == OperationReceiptState.Applied;
                if (ok)
                    Rearm(snapshot);
                finished(ok);
            });
        if (!begun.Success && !done)
        {
            done = true;
            _log.Warning($"Journal restore refused: {begun.Detail}");
            return false;
        }
        return true;
    }

    private void Rearm(ActorSnapshot snapshot)
    {
        foreach (var chain in snapshot.IkChains)
        {
            var bone = _bindings.Resolve(chain.Endpoint);
            if (!bone.Success || bone.Value is not { } endpoint)
                continue;
            if (_posing.SetIkConfiguration(endpoint, chain.Config) is { } refusal)
                _log.Warning($"Journal restore could not re-arm {chain.Endpoint.CanonicalName}: {refusal}");
        }
    }
}
