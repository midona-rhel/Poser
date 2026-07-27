using System.Collections.Generic;
using Poser.Core;
using Poser.Entities;

namespace Poser.Files;

/// <summary>
/// Everything one pose-file import would change, computed WITHOUT mutating
/// anything: the exact bones the chosen scope resets, the absolute
/// raw-basis writes (file bones first, then the face-reconcile writes),
/// and the owning actor's model transform when enabled. The stable pose
/// edit path applies the whole plan as ONE atomic, undoable edit.
/// </summary>
public sealed class PoseImportPlan
{
    public List<IBone> Resets { get; } = new();
    public List<(IBone Bone, Transform Desired)> Writes { get; } = new();

    /// <summary>The owning actor and its desired absolute model transform;
    /// null when the import does not touch the model transform.</summary>
    public IActor? ModelActor { get; set; }
    public Transform ModelTransform { get; set; }

    /// <summary>File bones applied (excludes face-reconcile writes).</summary>
    public int FileBoneCount { get; set; }

    public bool IsEmpty =>
        Resets.Count == 0 && Writes.Count == 0 && ModelActor == null;
}
