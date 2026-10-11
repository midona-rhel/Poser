namespace Poser.Documents.Config;

/// <summary>
/// The world gizmo's own options: size, snapping, and whether the gizmo
/// survives a fully hidden armature. Every default is the behaviour Poser
/// already had.
/// </summary>
public class GizmoConfiguration
{
    /// <summary>
    /// Multiplies the gizmo's constant perceived size (Ktisis'
    /// <c>Gizmo.ScaleFactor</c>). 1.0 is the 80px handle span Poser has always
    /// drawn.
    /// </summary>
    public float GizmoScale { get; set; } = 1.0f;

    // ── snapping (Ktisis Gizmo.Manipulate / OverlayWindow.HandleShiftRaycast)

    /// <summary>Ktisis' <c>AllowHoldSnap</c>: hold Ctrl and the gesture's TOTAL
    /// delta quantises to <see cref="SnapRotationDegrees"/> /
    /// <see cref="SnapLinearStep"/>; add Shift and both divide by ten. Ktisis
    /// ships it on — Poser ships it off, because Ctrl already means "finer" and
    /// silently adding quantisation to an existing chord is exactly the
    /// surprise a stored config must not get.</summary>
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
    /// Brio's <c>GizmoStaysWhenAllBonesAreDisabled</c>. Poser's long-standing
    /// behaviour IS Brio's ON state — a selection anchor alone is enough to
    /// draw the gizmo — so this ships true and turning it off is what buys
    /// Brio's other state: hiding the armature hides the gizmo with it.
    /// </summary>
    public bool KeepGizmoWhenBonesHidden { get; set; } = true;

    /// <summary>The Universal tool's centre handle moves the target
    /// instead of scaling it uniformly (ruled 2026-09-03: the user picks).</summary>
    public bool UniversalCenterTranslates { get; set; }

    /// <summary>Hides the bone gizmo whenever the armature overlay is not
    /// drawn at all — the master-switch companion to
    /// <see cref="KeepGizmoWhenBonesHidden"/>, which answers for one
    /// hidden bone.</summary>
    public bool HideGizmoWithoutArmature { get; set; }
}
