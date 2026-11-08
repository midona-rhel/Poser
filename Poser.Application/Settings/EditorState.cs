using Poser.Domain.Preferences;
using Poser.Documents.Config;

namespace Poser.Application.Settings;

/// <summary>
/// Editor tool settings shared by UI controls and shortcuts. The selected tool,
/// skeleton view mode, and selected-bones filter persist through configuration.
/// Orientation, symmetry, and rotation pivot are held in memory. Selection itself
/// belongs to SelectionSession.
/// </summary>
public class EditorState
{
    private readonly ConfigurationService _configuration;

    public EditorState(ConfigurationService configuration)
        => _configuration = configuration;

    public TransformOrientation TransformOrientation { get; set; } = TransformOrientation.Local;
    /// <summary>The tool outlives the session: it is read from the config
    /// once and written back on every change.</summary>
    private TransformTool? _tool;
    public TransformTool TransformTool
    {
        get => _tool ??= System.Enum.TryParse<TransformTool>(
                _configuration.Config.UI.LastTransformTool, out var saved)
            ? saved
            : TransformTool.Rotate;
        set
        {
            if (_tool == value)
                return;
            _tool = value;
            _configuration.Config.UI.LastTransformTool = value.ToString();
            _configuration.ApplyChange();
        }
    }

    public SkeletonViewMode SkeletonViewMode
    {
        get => _configuration.Config.Skeleton.SkeletonViewMode;
        set
        {
            var skeleton = _configuration.Config.Skeleton;
            if (skeleton.SkeletonViewMode == value)
                return;
            skeleton.SkeletonViewMode = value;
            // Persist shortcut changes as well as changes from Settings.
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
