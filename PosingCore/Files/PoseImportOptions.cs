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
    /// Import bone position data. Off by default: both references default
    /// pose import to rotation-only (Brio PosingService.cs:36
    /// DefaultImporterOptions, Ktisis FileConfig.cs:20 ImportPoseTransforms),
    /// and baked positions/scales in a file silently fight IK and C+ scaling.
    /// </summary>
    public bool ApplyPosition { get; set; }

    /// <summary>
    /// Import bone scale data. Off by default — see <see cref="ApplyPosition"/>.
    /// </summary>
    public bool ApplyScale { get; set; }

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
    /// Expression import — Brio's dance, ported LITERALLY after the
    /// single-phase "skip j_kao" rewrite proved wrong for cross-character
    /// files (the face is authored around the FILE's head; computing deltas
    /// against the target's head baked the head offset into every face
    /// position and flung the face). The plan applies Brio's ExpressionOptions
    /// scope INCLUDING the head, every component forced; the import engine
    /// then restores the head (+4 ticks, stack pop + position re-import) and
    /// reconciles the face subtree (+4 more), exactly Brio's
    /// PosingCapability phases.
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
    /// Keep the target actor's animation paused once the import has finished.
    /// The import always pauses the actor for its apply window; this flag
    /// skips the speed restore afterwards — Brio's "Freeze Actor" popup
    /// checkbox (FileUIHelpers.cs:478), which its ImportPose ORs with the
    /// Posing.FreezeActorOnPoseImport config; the facade applies the same OR
    /// against Poser's config default. The file service itself ignores this:
    /// animation is the facade's concern, never the plan builder's.
    /// </summary>
    public bool FreezeOnImport { get; set; }

    /// <summary>
    /// Default options: every slot, rotation-only, no model transform.
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
    /// Rest-pose preset — Brio's LoadResourcesPose(asBody: true): Character
    /// slot only, rotation-only, face and model transform untouched. Brio's
    /// category-level exclusions (head, ears, hair, ex, legacy) are baked
    /// into the <see cref="RestPoses"/> files at load, not expressed here.
    /// </summary>
    public static PoseImportOptions RestPose => new()
    {
        ApplyRotation = true,
        ApplyPosition = false,
        ApplyScale = false,
        ApplyBody = true,
        ApplyFace = false,
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
        ApplyPosition = true,
        ApplyScale = true,
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
            FreezeOnImport = FreezeOnImport,
        };
    }
}
