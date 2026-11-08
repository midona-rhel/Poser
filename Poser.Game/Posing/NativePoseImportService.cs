using Dalamud.Plugin.Services;
using Poser.Domain.Operations;
using Poser.Application.Posing;
using Poser.Domain.Identity;
using Poser.Domain.Posing;
using Poser.Documents.Files;
using Poser.Game.Entities;
using Poser.Game.Files;
using Poser.Game.Services;

namespace Poser.Game.Posing;

/// <summary>Resolves exact actor IDs before native pose planning and application.</summary>
public sealed class NativePoseImportService : IPoseImportCommands
{
    private readonly IEntityBindings _bindings;
    private readonly PoseImportCoordinator _imports;

    public NativePoseImportService(
        IEntityBindings bindings,
        PoseImportCoordinator imports,
        IPoseFileService poseFiles,
        ISkeletonService skeletons,
        IPluginLog log)
    {
        _bindings = bindings;
        _imports = imports;
        _poseFiles = poseFiles;
        _skeletons = skeletons;
        _log = log;
    }

    public bool IsImportBusy => _imports.IsImportBusy;

    public bool HasPosableSkeleton(ActorId actor) =>
        ResolveTarget(actor, out var current) is null && HasPosableSkeleton(current);

    public PoseImportInspection? InspectPose(ActorId actor, PoseFile pose)
    {
        if (ResolveTarget(actor, out var current) is not null
            || _skeletons.GetSkeleton(current) is not { } skeleton)
            return null;
        return new(
            Poser.Documents.Files.PoseFileClassification.IsExpressionOnly(pose),
            PoseFileService.IsBodyOnlyPose(pose),
            PoseFileService.IsDawntrailSkeleton(skeleton)
                && PoseFileService.IsLikelyDawntrailPose(pose),
            PoseFileService.CompareFaceGeneration(pose, skeleton));
    }

    public PoseEditResult ImportPose(ActorId actor, string path, PoseImportOptions options,
        IReadOnlyList<BoneId>? selectedBones = null, Action<OperationReceipt>? onReceipt = null) =>
        ResolveTarget(actor, out var current) is { } refusal ? refusal :
        ImportPose(current, path, options, selectedBones, onReceipt);

    public PoseEditResult ImportPose(ActorId actor, PoseFile poseFile, PoseImportOptions options,
        string description, Action<OperationReceipt>? onReceipt = null,
        IReadOnlyList<BoneId>? selectedBones = null) =>
        ResolveTarget(actor, out var current) is { } refusal ? refusal :
        ImportPose(current, poseFile, options, description, onReceipt, selectedBones);

    public PoseEditResult ApplyRestPose(ActorId actor, RestPose pose,
        Action<OperationReceipt>? onReceipt = null) =>
        ResolveTarget(actor, out var current) is { } refusal ? refusal :
        ApplyRestPose(current, pose, onReceipt);

    public PoseEditResult ApplyReferencePose(ActorId actor, Action<OperationReceipt>? onReceipt = null) =>
        ResolveTarget(actor, out var current) is { } refusal ? refusal :
        ApplyReferencePose(current, onReceipt);

    private PoseEditResult? ResolveTarget(ActorId id, out IActor actor)
    {
        actor = null!;
        // Planning reads native skeleton caches too, so a refused resolve
        // (including off the framework thread) stops before any planning.
        var resolved = _bindings.Resolve(id);
        if (!resolved.Success || resolved.Value is not { } current)
            return PoseEditResult.Fail(resolved.Detail ?? "The actor is no longer available.");
        actor = current;
        return null;
    }

    /// <summary>Checks whether the Character skeleton is available. Preview
    /// callers can wait for it before attempting to plan an import.</summary>
    private bool HasPosableSkeleton(IActor actor) =>
        _skeletons.GetSkeleton(actor) is not null;

    private readonly IPoseFileService _poseFiles;

    /// <summary>Builds a plan and submits it to the import coordinator. A successful
    /// return accepts the operation; its terminal receipt reports application or
    /// failure, including rollback problems. History may be suppressed.</summary>
    private PoseEditResult ImportPose(
        IActor actor,
        string path,
        PoseImportOptions options,
        IReadOnlyList<BoneId>? selectedBones = null,
        Action<OperationReceipt>? onReceipt = null)
    {
        if (ReduceSelectedScope(actor, selectedBones, ref options) is { } refused)
            return refused;

        var plan = _poseFiles.BuildImportPlan(_skeletons.GetSkeletons(actor), path, options);
        if (plan == null)
            return PoseEditResult.Fail("The pose file could not be read or failed validation.");
        return BeginImport(actor, plan, options,
            $"Import {System.IO.Path.GetFileName(path)}", onReceipt, asset: path);
    }

    /// <summary>In-memory variant of the file import — same plan builder,
    /// same pause bracket, same in-pass application, one history entry named
    /// <paramref name="description"/>. The rest-pose presets and the
    /// reference-pose action apply through here without a disk path.</summary>
    private PoseEditResult ImportPose(
        IActor actor,
        PoseFile poseFile,
        PoseImportOptions options,
        string description,
        Action<OperationReceipt>? onReceipt = null,
        IReadOnlyList<BoneId>? selectedBones = null)
    {
        if (ReduceSelectedScope(actor, selectedBones, ref options) is { } refused)
            return refused;

        var plan = _poseFiles.BuildImportPlan(
            _skeletons.GetSkeletons(actor), poseFile, options);
        if (plan == null)
            return PoseEditResult.Fail("The pose failed validation and was not imported.");
        return BeginImport(actor, plan, options, description, onReceipt);
    }

    /// <summary>
    /// Selected scope: the frozen BoneIds must all belong to the exact
    /// actor generation this import was opened for, and each must still
    /// resolve. Only then do they reduce to the slot-qualified filter —
    /// an empty, stale, or cross-actor selection fails instead of silently
    /// importing nothing or turning into a name-based selection on another
    /// actor. The reduction works on a clone; the caller's options object
    /// is never mutated. Returns the typed refusal, or null to proceed.
    /// </summary>
    private PoseEditResult? ReduceSelectedScope(
        IActor actor,
        IReadOnlyList<BoneId>? selectedBones,
        ref PoseImportOptions options)
    {
        if (selectedBones == null)
            return null;
        if (selectedBones.Count == 0)
            return PoseEditResult.Fail(
                "No bones are selected on this actor; select bones or turn off the selected-bones scope.");
        if (_bindings.GetActorId(actor) is not { } target)
            return PoseEditResult.Fail("The actor could not be resolved.");
        var filter = new HashSet<(PoseSlot Slot, string Name)>();
        foreach (var bone in selectedBones)
        {
            if (!bone.Skeleton.Actor.Equals(target))
                return PoseEditResult.Fail(
                    "The selection contains bones from a different actor than this import's target.");
            var resolvedBone = _bindings.Resolve(bone);
            if (!resolvedBone.Success)
                return PoseEditResult.Fail(
                    resolvedBone.Detail ?? $"Selected bone {bone.CanonicalName} is stale.");
            filter.Add((bone.Skeleton.Slot, bone.CanonicalName));
        }
        options = options.Clone();
        options.BoneFilter = filter;
        return null;
    }

    /// <summary>Applies the embedded body rest pose with rotation-only writes.
    /// Unlike Brio's resource import, this resets authored changes first, limited
    /// to names in the preset so unrelated head, ear, and hair edits remain.</summary>
    private PoseEditResult ApplyRestPose(
        IActor actor,
        RestPose pose,
        Action<OperationReceipt>? onReceipt = null)
    {
        var description = pose == RestPose.APose ? "A-pose" : "T-pose";
        var poseFile = RestPoses.Get(pose);
        var options = PoseImportOptions.RestPose;
        options.ResetBeforeImport = true;
        var filter = new HashSet<(PoseSlot Slot, string Name)>();
        foreach (var name in poseFile.Bones.Keys)
            filter.Add((PoseSlot.Character, name));
        options.BoneFilter = filter;
        return Report(description, ImportPose(
            actor, poseFile, options, description, onReceipt));
    }

    /// <summary>
    /// Ktisis' "Set to reference pose" (PosingManager.ApplyReferencePose:
    /// hkaPose::SetToReferencePose on every partial, ONE memento covering
    /// Position | Rotation): the skeleton's own rest pose, read from the
    /// native reference locals and applied through the same in-pass import
    /// engine as a single undoable edit. Scale stays untouched, exactly the
    /// Ktisis memento's transform mask; auxiliary slots keep their animation,
    /// matching Ktisis' per-skeleton scope.
    /// </summary>
    private PoseEditResult ApplyReferencePose(
        IActor actor, Action<OperationReceipt>? onReceipt = null)
    {
        const string description = "Reference pose";
        if (_skeletons.GetSkeleton(actor) is not { } character)
            return Report(description,
                PoseEditResult.Fail("The actor has no skeleton."));
        var reference = character.CaptureReferencePose();
        if (reference.Count == 0)
            return Report(description, PoseEditResult.Fail(
                "The skeleton's reference pose could not be read."));

        // The reference pose as a generated pose file: by-name absolute
        // model-space targets, so the import's instance expansion writes
        // every partial's copy of a bone (face and hair roots included)
        // exactly as a file import would.
        var poseFile = new PoseFile();
        foreach (var (bone, transform) in reference)
            poseFile.Bones.TryAdd(bone.BoneName, transform);
        var options = new PoseImportOptions
        {
            ApplyRotation = true,
            ApplyPosition = true,
            ApplyScale = false,
            ApplyBody = true,
            ApplyFace = true,
            ApplyMainHand = false,
            ApplyOffHand = false,
            ApplyProp = false,
            ApplyOrnament = false,
            ApplyModelTransform = false
        };
        return Report(description,
            ImportPose(actor, poseFile, options, description, onReceipt));
    }

    private PoseEditResult BeginImport(
        IActor actor, PoseImportPlan plan, PoseImportOptions options,
        string description, Action<OperationReceipt>? onReceipt = null, string? asset = null)
    {
        if (_bindings.GetActorId(actor) is not { } id ||
            _bindings.Resolve(id) is not { Success: true, Value: { } current } ||
            !ReferenceEquals(actor, current))
            return PoseEditResult.Fail("The actor could not be resolved.");
        return _imports.Begin(id, new PreparedPoseImport(plan), options, description, onReceipt, asset);
    }

    private readonly ISkeletonService _skeletons;

    private readonly IPluginLog _log;

    private PoseEditResult Report(string description, PoseEditResult result)
    {
        if (!result.Success)
            _log.Warning($"Pose edit '{description}' failed: {result.Detail}");
        else if (!string.IsNullOrEmpty(result.Detail))
            _log.Information($"Pose edit '{description}': {result.Detail}");
        return result;
    }

}
