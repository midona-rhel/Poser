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
    /// Import model position/rotation (actor transform).
    /// </summary>
    public bool ApplyModelTransform { get; set; } = false;

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
            ApplyModelTransform = ApplyModelTransform
        };
    }
}
