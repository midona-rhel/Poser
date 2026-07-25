using System;

namespace Poser.Files;

/// <summary>
/// Options for importing pose files. Controls which components are applied.
/// </summary>
[Serializable]
public class PoseImportOptions
{
    /// <summary>
    /// Import bone rotation data.
    /// </summary>
    public bool ApplyRotation { get; set; } = true;

    /// <summary>
    /// Import bone position data.
    /// </summary>
    public bool ApplyPosition { get; set; } = true;

    /// <summary>
    /// Import bone scale data.
    /// </summary>
    public bool ApplyScale { get; set; } = true;

    /// <summary>
    /// Import body/main skeleton bones.
    /// </summary>
    public bool ApplyBody { get; set; } = true;

    /// <summary>
    /// Import face bones.
    /// </summary>
    public bool ApplyFace { get; set; } = true;

    /// <summary>
    /// Import main hand weapon bones.
    /// </summary>
    public bool ApplyMainHand { get; set; } = true;

    /// <summary>
    /// Import off hand weapon bones.
    /// </summary>
    public bool ApplyOffHand { get; set; } = true;

    /// <summary>
    /// Import prop (system weapon slot) bones.
    /// </summary>
    public bool ApplyProp { get; set; } = true;

    /// <summary>
    /// Import ornament bones.
    /// </summary>
    public bool ApplyOrnament { get; set; } = true;

    /// <summary>
    /// Import model position/rotation (actor transform).
    /// </summary>
    public bool ApplyModelTransform { get; set; } = false;

    /// <summary>
    /// Reset existing bone modifications (and, when ApplyModelTransform is set, the actor
    /// transform override) before applying the file. Mirrors Brio's `reset` import flag,
    /// which its interactive import path passes as false: file bones are absolute targets,
    /// so a full-skeleton file determines the pose without wiping unrelated edits first.
    /// </summary>
    public bool ResetBeforeImport { get; set; } = false;

    /// <summary>
    /// Expression import: apply ONLY face bones and EXCLUDE the head bone (j_kao),
    /// so a face pose lands without turning the posed head. Single-phase rewrite of
    /// Brio's two-phase apply-then-restore (which needs a 4-tick resync hack and
    /// "stil breaks IK" per its own comment): skipping j_kao up front reaches the
    /// same end state — face bones take the file's absolute orientations, the head
    /// keeps the current pose — with no tick delays.
    /// </summary>
    public bool AsExpression { get; set; } = false;

    /// <summary>
    /// When set, only these slot-qualified bones are applied (selective
    /// import — Ktisis/Anamnesis parity). A filter entry names the bone's
    /// EXACT slot; a name alone can never match across slots.
    /// </summary>
    public System.Collections.Generic.ISet<(Poser.Domain.Identity.PoseSlot Slot, string Name)>? BoneFilter { get; set; }

    /// <summary>Extend <see cref="BoneFilter"/> to every descendant of the filtered bones.</summary>
    public bool FilterIncludesDescendants { get; set; }

    /// <summary>
    /// Default options that import everything except model transform.
    /// </summary>
    public static PoseImportOptions Default => new();

    /// <summary>
    /// Options that only import rotation (for expression application).
    /// </summary>
    public static PoseImportOptions RotationOnly => new()
    {
        ApplyRotation = true,
        ApplyPosition = false,
        ApplyScale = false
    };

    /// <summary>Expression preset — face only, head excluded, no model transform.</summary>
    public static PoseImportOptions Expression => new()
    {
        AsExpression = true,
        ApplyBody = true,
        ApplyFace = true,
        ApplyMainHand = false,
        ApplyOffHand = false,
        ApplyProp = false,
        ApplyOrnament = false,
        ApplyModelTransform = false
    };

    /// <summary>
    /// Options that import everything including model transform.
    /// </summary>
    public static PoseImportOptions All => new()
    {
        ApplyModelTransform = true
    };

    /// <summary>
    /// Creates a copy of these options.
    /// </summary>
    public PoseImportOptions Clone()
    {
        return new PoseImportOptions
        {
            ApplyRotation = ApplyRotation,
            ApplyPosition = ApplyPosition,
            ApplyScale = ApplyScale,
            ApplyBody = ApplyBody,
            ApplyFace = ApplyFace,
            ApplyMainHand = ApplyMainHand,
            ApplyOffHand = ApplyOffHand,
            ApplyProp = ApplyProp,
            ApplyOrnament = ApplyOrnament,
            ApplyModelTransform = ApplyModelTransform,
            ResetBeforeImport = ResetBeforeImport,
            AsExpression = AsExpression,
            BoneFilter = BoneFilter == null
                ? null
                : new System.Collections.Generic.HashSet<(Poser.Domain.Identity.PoseSlot Slot, string Name)>(BoneFilter),
            FilterIncludesDescendants = FilterIncludesDescendants,
        };
    }
}
