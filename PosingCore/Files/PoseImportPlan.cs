using System.Collections.Generic;
using Poser.Core;
using Poser.Entities;

namespace Poser.Files;

/// <summary>
/// Everything one pose-file import would change, computed WITHOUT mutating
/// anything: the exact bones the chosen scope resets, the file's absolute
/// transforms verbatim with their per-bone delta masks, and the owning
/// actor's model transform when enabled. Every target appears at most once
/// per role, so the edit gives each exactly one deterministic final state.
/// A write's basis is NOT part of the plan: the apply pass supplies its own
/// just-refreshed <c>bone.LastRawTransform</c> (Brio PoseImporter.cs:35).
/// </summary>
public sealed class PoseImportPlan
{
    public List<IBone> Resets { get; } = new();
    public List<(IBone Bone, Transform File, TransformComponents Components)> Writes { get; } = new();

    /// <summary>The owning actor and its desired absolute model transform;
    /// null when the import does not touch the model transform.</summary>
    public IActor? ModelActor { get; set; }
    public Transform ModelTransform { get; set; }

    /// <summary>File bones applied.</summary>
    public int FileBoneCount { get; set; }

    public bool IsEmpty =>
        Resets.Count == 0 && Writes.Count == 0 && ModelActor == null;
}
