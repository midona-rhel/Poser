using System.Collections.Generic;
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
    private readonly List<IBone> _selectedBones = new();

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

    public IBone? SelectedBone => _selectedBones.Count > 0 ? _selectedBones[0] : null;

    IBone? IEditorState.SelectedBone
    {
        get => SelectedBone;
        set => SelectBone(value);
    }

    public IReadOnlyList<IBone> SelectedBones => _selectedBones.AsReadOnly();

    public void SelectBone(IBone? bone)
    {
        _selectedBones.Clear();
        if (bone != null)
        {
            _selectedBones.Add(bone);
        }
        _eventBus.Publish(new BoneSelectionChangedEvent(bone));
    }

    public void SelectBones(IEnumerable<IBone> bones)
    {
        _selectedBones.Clear();
        _selectedBones.AddRange(bones);
        _eventBus.Publish(new BoneSelectionChangedEvent(SelectedBone));
    }

    public void AddBoneToSelection(IBone bone)
    {
        if (!_selectedBones.Contains(bone))
        {
            _selectedBones.Add(bone);
            _eventBus.Publish(new BoneSelectionChangedEvent(SelectedBone));
        }
    }

    public void RemoveBoneFromSelection(IBone bone)
    {
        if (_selectedBones.Remove(bone))
        {
            _eventBus.Publish(new BoneSelectionChangedEvent(SelectedBone));
        }
    }

    public void ToggleBoneSelection(IBone bone)
    {
        if (_selectedBones.Contains(bone))
        {
            RemoveBoneFromSelection(bone);
        }
        else
        {
            AddBoneToSelection(bone);
        }
    }

    public bool IsBoneSelected(IBone bone) => _selectedBones.Contains(bone);

    public void ClearBoneSelection()
    {
        _selectedBones.Clear();
        _eventBus.Publish(new BoneSelectionChangedEvent(null));
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
