using System;
using Poser.Entities;
using Poser.Services;

namespace Poser.Core;

/// <summary>
/// Tracks editor-wide state like pivot mode, tool selection, etc.
/// </summary>
public class EditorState : IEditorState
{
    private IBone? _selectedBone;

    public PivotMode PivotMode { get; set; } = PivotMode.Local;
    public bool DebugMode { get; set; } = false;
    public BoneDisplayMode BoneDisplayMode { get; set; } = BoneDisplayMode.Category;

    public IBone? SelectedBone
    {
        get => _selectedBone;
        set
        {
            if (_selectedBone != value)
            {
                _selectedBone = value;
                OnBoneSelectionChanged?.Invoke(value);
            }
        }
    }

    public event Action<IBone?>? OnBoneSelectionChanged;

    public void SelectBone(IBone? bone)
    {
        SelectedBone = bone;
    }

    public void ClearBoneSelection()
    {
        SelectedBone = null;
    }
}
