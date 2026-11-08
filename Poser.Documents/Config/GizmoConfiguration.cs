namespace Poser.Documents.Config;

/// <summary>
/// Controls gizmo size, snapping, scaling, and visibility.
/// </summary>
public class GizmoConfiguration
{
    /// <summary>
    /// Multiplies the gizmo's constant perceived size (Ktisis'
    /// <c>Gizmo.ScaleFactor</c>). 1.0 is the 80px handle span Poser has always
    /// drawn.
    /// </summary>
    public float GizmoScale { get; set; } = 1.0f;

    /// <summary>Enables snapping while Ctrl is held. Rotation and linear changes
    /// use the configured increments; holding Shift divides them by ten.</summary>
    public bool AllowHoldSnap { get; set; } = false;

    /// <summary>What scaling a multi-selection does: grow the members and
    /// their spacing, or the spacing alone.</summary>
    public Poser.Domain.Transforms.GroupScaleMode GroupScale { get; set; } =
        Poser.Domain.Transforms.GroupScaleMode.SizesAndSpacing;

    /// <summary>Ktisis' rotate increment: 5°, and 0.5° with Shift.</summary>
    public float SnapRotationDegrees { get; set; } = 5.0f;

    /// <summary>Ktisis' translate/scale increment: 0.1, and 0.01 with
    /// Shift.</summary>
    public float SnapLinearStep { get; set; } = 0.1f;

    /// <summary>Ktisis' <c>AllowRaySnap</c>: hold Shift during a translate drag
    /// and the target lands wherever the pointer's view ray hits the
    /// world.</summary>
    public bool AllowRaySnap { get; set; } = false;

    /// <summary>
    /// Whether a selected bone keeps its gizmo when every bone in the
    /// armature overlay is hidden.
    /// </summary>
    public bool KeepGizmoWhenBonesHidden { get; set; } = true;

    /// <summary>Whether the Universal tool's center handle translates instead
    /// of applying uniform scale.</summary>
    public bool UniversalCenterTranslates { get; set; }

    /// <summary>Hides the bone gizmo whenever the armature overlay is not
    /// drawn at all — the master-switch companion to
    /// <see cref="KeepGizmoWhenBonesHidden"/>, which answers for one
    /// hidden bone.</summary>
    public bool HideGizmoWithoutArmature { get; set; }
}
