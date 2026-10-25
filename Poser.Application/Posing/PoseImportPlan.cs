using System.Collections.Generic;
using Poser.Domain.Identity;
using Poser.Domain.Posing;
using Poser.Domain.Transforms;

namespace Poser.Application.Posing;

/// <summary>One bone the import's chosen scope resets, named the way the
/// pose store is addressed: slot, partial, and bone name rather than a live instance.</summary>
public readonly record struct PoseImportReset(
    PoseSlot Slot, int Partial, string Bone);

/// <summary>One file bone to apply: the file's absolute transform verbatim
/// with its per-bone delta mask, addressed by (slot, partial, bone name).
/// The live bone is resolved at the write moment, not planned.</summary>
public readonly record struct PoseImportWrite(
    PoseSlot Slot,
    int Partial,
    string Bone,
    Transform File,
    TransformComponents Components);

/// <summary>
/// Planned bone resets, absolute file transforms with component masks, and an
/// optional actor model transform. Entries identify bones by slot, partial,
/// and name so the apply pass can resolve current bones after a redraw.
/// Builders must avoid duplicate targets within each list. The runtime still
/// validates the operation and supplies the current raw transform used as the
/// basis for each bone write.
/// </summary>
public sealed class PoseImportPlan
{
    // Name-only files need legacy face reconciliation. An exact live copy
    // already supplies each partial's displayed target in the apply frame.
    public bool ReconcileFace { get; init; } = true;
    public List<PoseImportReset> Resets { get; } = new();
    public List<PoseImportWrite> Writes { get; } = new();

    /// <summary>Whether the import carries a model transform for the target
    /// actor. The actor itself is the import's admitted target — the plan
    /// never names an actor instance.</summary>
    public bool HasModelTransform { get; set; }
    public Transform ModelTransform { get; set; }

    /// <summary>File bones applied.</summary>
    public int FileBoneCount { get; set; }

    public bool IsEmpty =>
        Resets.Count == 0 && Writes.Count == 0 && !HasModelTransform;
}
