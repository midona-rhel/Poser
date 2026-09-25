using System.Collections.Generic;
using Poser.Core;
using Poser.Domain.Identity;
using Poser.Domain.Posing;

namespace Poser.Files;

/// <summary>One bone the import's chosen scope resets, named the way the
/// pose store is addressed: slot, partial, bone name — never an instance
/// (issue #78).</summary>
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
/// Everything one pose-file import would change, computed WITHOUT mutating
/// anything: the exact bones the chosen scope resets, the file's absolute
/// transforms verbatim with their per-bone delta masks, and the owning
/// actor's model transform when enabled. Every target appears at most once
/// per role, so the edit gives each exactly one deterministic final state.
/// The plan is pure data — bones by (slot, partial, name), never skeleton
/// or bone instances — so it stays valid across the ticks between arming
/// and application, redraws included (issue #78). A write's basis is NOT
/// part of the plan: the apply pass supplies its own just-refreshed
/// <c>bone.LastRawTransform</c> (Brio PoseImporter.cs:35).
/// </summary>
public sealed class PoseImportPlan
{
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
