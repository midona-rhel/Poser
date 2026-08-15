using Poser.Config;
using Poser.Services;

namespace Poser.Core;

/// <summary>
/// Tracks editor-wide state: gizmo settings.
/// UI components call methods directly.
///
/// NOTE: Selection is handled by the application SelectionSession, not here.
/// This class only tracks editor tool settings.
///
/// <para>Two of these are NOT session state and are not held here: the shape
/// the armature is drawn in and the selected-bones-only filter are standing
/// preferences, so they live in <see cref="SkeletonConfiguration"/> and the
/// properties below are views onto it. ONE store, so the settings page, the
/// keybinds and the overlay can never disagree — and so a chord thrown at
/// either of them outlives the session.</para>
/// </summary>
public class EditorState : IEditorState
{
    private readonly ConfigurationService _configuration;

    public EditorState(ConfigurationService configuration)
        => _configuration = configuration;

    public TransformOrientation TransformOrientation { get; set; } = TransformOrientation.Local;
    public TransformTool TransformTool { get; set; } = TransformTool.Rotate;

    public SkeletonViewMode SkeletonViewMode
    {
        get => _configuration.Config.Skeleton.SkeletonViewMode;
        set
        {
            var skeleton = _configuration.Config.Skeleton;
            if (skeleton.SkeletonViewMode == value)
                return;
            skeleton.SkeletonViewMode = value;
            // A write here is a keybind press or a settings row, never a
            // frame, so saving on the spot is what makes the chord stick.
            _configuration.ApplyChange();
        }
    }

    public bool ShowSelectedBonesOnly
    {
        get => _configuration.Config.Skeleton.ShowSelectedBonesOnly;
        set
        {
            var skeleton = _configuration.Config.Skeleton;
            if (skeleton.ShowSelectedBonesOnly == value)
                return;
            skeleton.ShowSelectedBonesOnly = value;
            _configuration.ApplyChange();
        }
    }

    public SymmetryMode SymmetryMode { get; set; } = SymmetryMode.Off;

    public RotationPivot RotationPivot { get; set; } = RotationPivot.Self;
}
