using Poser.Entities;
using Poser.Services;

namespace Poser.Core;

/// <summary>
/// Tracks editor-wide state like pivot mode, tool selection, etc.
/// </summary>
public class EditorState : IEditorState
{
    private readonly IActorManager _actorManager;
    private readonly IEventBus _eventBus;
    private IBone? _selectedBone;

    public TransformPivot TransformPivot { get; set; } = TransformPivot.Individual;
    public TransformOrientation TransformOrientation { get; set; } = TransformOrientation.Local;
    public TransformTool TransformTool { get; set; } = TransformTool.Rotate;
    public bool DebugMode { get; set; } = false;
    public BoneDisplayMode BoneDisplayMode { get; set; } = BoneDisplayMode.Category;

    public EditorState(IActorManager actorManager, IEventBus eventBus)
    {
        _actorManager = actorManager;
        _eventBus = eventBus;
    }

    public IBone? SelectedBone
    {
        get => _selectedBone;
        set
        {
            if (_selectedBone != value)
            {
                _selectedBone = value;
                _eventBus.Publish(new BoneSelectionChangedEvent(value));
            }
        }
    }

    public void SelectBone(IBone? bone)
    {
        SelectedBone = bone;
    }

    public void ClearBoneSelection()
    {
        SelectedBone = null;
    }

    public GizmoTargetType GetGizmoTargetType()
    {
        // If a bone is selected, gizmo targets bone
        if (SelectedBone != null)
            return GizmoTargetType.Bone;

        // If any actor is selected, gizmo targets actor(s)
        if (_actorManager.PrimarySelectedActor != null)
            return GizmoTargetType.Actor;

        return GizmoTargetType.None;
    }

    public void ToggleEditMode(IActor actor)
    {
        actor.IsEditMode = !actor.IsEditMode;

        // If disabling edit mode and a bone from this actor is selected, clear it
        if (!actor.IsEditMode && SelectedBone?.Skeleton.Actor == actor)
        {
            ClearBoneSelection();
        }
    }
}
