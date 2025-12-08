using System.Collections.Generic;
using System.Linq;
using Poser.Data;
using Poser.Data.Config;
using Poser.Entities;
using Poser.Services;

namespace Poser.Core;

/// <summary>
/// Tracks editor-wide state like pivot mode, tool selection, etc.
/// </summary>
public class EditorState : IEditorState
{
    private readonly IAnimationService _animationService;
    private readonly IGazeService _gazeService;
    private readonly IEventBus _eventBus;
    private readonly List<IEntity> _selectedEntities = new();

    // For shift+click range selection, we need to track all entities in display order
    private IEntity? _lastSelectedEntity;

    // Category selection (separate from entity selection)
    private string? _selectedCategory;
    private ISkeleton? _selectedCategorySkeleton;
    private readonly CategoryConfig _categoryConfig;

    public TransformPivot TransformPivot { get; set; } = TransformPivot.Individual;
    public TransformOrientation TransformOrientation { get; set; } = TransformOrientation.Local;
    public TransformTool TransformTool { get; set; } = TransformTool.Rotate;
    public bool DebugMode { get; set; } = false;
    public BoneDisplayMode BoneDisplayMode { get; set; } = BoneDisplayMode.Category;
    public bool IsPosingMode { get; private set; } = false;

    // Lazy inject to avoid circular dependency
    private IActorManager? _actorManager;
    public void SetActorManager(IActorManager actorManager) => _actorManager = actorManager;

    public EditorState(IAnimationService animationService, IGazeService gazeService, IEventBus eventBus)
    {
        _animationService = animationService;
        _gazeService = gazeService;
        _eventBus = eventBus;
        _categoryConfig = CategoryReader.ReadEmbeddedResource();

        _eventBus.Subscribe<GPoseStateChangedEvent>(OnGPoseStateChanged);
    }

    private void OnGPoseStateChanged(GPoseStateChangedEvent e)
    {
        if (!e.IsGPosing && IsPosingMode)
        {
            ExitPosingMode();
        }
    }

    #region Posing Mode

    public void EnterPosingMode()
    {
        if (IsPosingMode || _actorManager == null)
            return;

        IsPosingMode = true;

        foreach (var actor in _actorManager.Actors)
        {
            // Freeze animation
            if (!_animationService.IsFrozen(actor))
            {
                _animationService.Freeze(actor);
            }

            // Lock gaze to prevent head/eyes from tracking
            _gazeService.LockGaze(actor, GazeTargetType.All);
        }

        _eventBus.Publish(new PosingModeChangedEvent(true));
    }

    public void ExitPosingMode()
    {
        if (!IsPosingMode || _actorManager == null)
            return;

        IsPosingMode = false;
        ClearSelection();

        foreach (var actor in _actorManager.Actors)
        {
            // Unfreeze animation
            if (_animationService.IsFrozen(actor))
            {
                _animationService.Unfreeze(actor);
            }

            // Unlock gaze to allow normal tracking
            _gazeService.UnlockGaze(actor);
        }

        _eventBus.Publish(new PosingModeChangedEvent(false));
    }

    public void TogglePosingMode()
    {
        if (IsPosingMode)
            ExitPosingMode();
        else
            EnterPosingMode();
    }

    #endregion

    #region Unified Selection

    public IReadOnlyList<IEntity> SelectedEntities => _selectedEntities.AsReadOnly();

    public IEntity? PrimarySelection => _selectedEntities.Count > 0 ? _selectedEntities[0] : null;

    public void Select(IEntity entity)
    {
        // Clear previous selection state
        foreach (var e in _selectedEntities)
        {
            e.IsSelected = false;
        }
        _selectedEntities.Clear();

        // Clear category selection when selecting an entity
        ClearCategorySelection();

        // Select new entity
        _selectedEntities.Add(entity);
        entity.IsSelected = true;
        _lastSelectedEntity = entity;

        // Auto-enter posing mode when selecting a bone
        if (entity is IBone && !IsPosingMode)
        {
            EnterPosingMode();
        }

        PublishSelectionChanged();
    }

    public void AddToSelection(IEntity entity)
    {
        if (_selectedEntities.Contains(entity))
            return;

        _selectedEntities.Add(entity);
        entity.IsSelected = true;
        _lastSelectedEntity = entity;

        // Auto-enter posing mode when selecting a bone
        if (entity is IBone && !IsPosingMode)
        {
            EnterPosingMode();
        }

        PublishSelectionChanged();
    }

    public void RemoveFromSelection(IEntity entity)
    {
        if (_selectedEntities.Remove(entity))
        {
            entity.IsSelected = false;
            PublishSelectionChanged();
        }
    }

    public void ToggleSelection(IEntity entity)
    {
        if (_selectedEntities.Contains(entity))
        {
            RemoveFromSelection(entity);
        }
        else
        {
            AddToSelection(entity);
        }
    }

    public void SelectRange(IEntity from, IEntity to)
    {
        // For range selection, we need to find all entities between 'from' and 'to'
        // in the tree traversal order. This requires traversing the entity tree.
        // For now, just select both endpoints - full range selection needs tree context.

        if (!_selectedEntities.Contains(from))
        {
            _selectedEntities.Add(from);
            from.IsSelected = true;
        }

        if (!_selectedEntities.Contains(to))
        {
            _selectedEntities.Add(to);
            to.IsSelected = true;
        }

        _lastSelectedEntity = to;

        if ((from is IBone || to is IBone) && !IsPosingMode)
        {
            EnterPosingMode();
        }

        PublishSelectionChanged();
    }

    public bool IsSelected(IEntity entity) => _selectedEntities.Contains(entity);

    public void ClearSelection()
    {
        foreach (var e in _selectedEntities)
        {
            e.IsSelected = false;
        }
        _selectedEntities.Clear();
        _lastSelectedEntity = null;

        PublishSelectionChanged();
    }

    private void PublishSelectionChanged()
    {
        _eventBus.Publish(new SelectionChangedEvent(_selectedEntities.OfType<IActor>().ToList()));
        _eventBus.Publish(new BoneSelectionChangedEvent(SelectedBone));
    }

    #endregion

    #region Convenience Accessors

    public IEnumerable<T> GetSelected<T>() where T : IEntity
    {
        return _selectedEntities.OfType<T>();
    }

    public IBone? SelectedBone => _selectedEntities.OfType<IBone>().FirstOrDefault();

    public IActor? SelectedActor => _selectedEntities.OfType<IActor>().FirstOrDefault();

    public GizmoTargetType GetGizmoTargetType()
    {
        // Category selection takes priority for bone manipulation
        if (_selectedCategory != null)
            return GizmoTargetType.Bone;

        if (SelectedBone != null)
            return GizmoTargetType.Bone;

        if (SelectedActor != null)
            return GizmoTargetType.Actor;

        return GizmoTargetType.None;
    }

    #endregion

    #region Category Selection

    public string? SelectedCategory => _selectedCategory;
    public ISkeleton? SelectedCategorySkeleton => _selectedCategorySkeleton;

    public void SelectCategory(string categoryId, ISkeleton skeleton)
    {
        // Clear entity selection when selecting a category
        foreach (var e in _selectedEntities)
        {
            e.IsSelected = false;
        }
        _selectedEntities.Clear();
        _lastSelectedEntity = null;

        _selectedCategory = categoryId;
        _selectedCategorySkeleton = skeleton;

        // Auto-enter posing mode when selecting a category
        if (!IsPosingMode)
        {
            EnterPosingMode();
        }

        PublishSelectionChanged();
    }

    public void ClearCategorySelection()
    {
        _selectedCategory = null;
        _selectedCategorySkeleton = null;
    }

    public bool IsCategorySelected(string categoryId)
    {
        return _selectedCategory == categoryId;
    }

    public IReadOnlyList<IBone> GetSelectedCategoryBones()
    {
        if (_selectedCategory == null || _selectedCategorySkeleton == null)
            return new List<IBone>();

        var skeleton = _selectedCategorySkeleton as Skeleton;
        if (skeleton == null)
            return new List<IBone>();

        var category = FindCategory(_selectedCategory, _categoryConfig.RootCategories);
        if (category == null)
            return new List<IBone>();

        var bonesByName = new Dictionary<string, Bone>();
        GatherBones(skeleton, bonesByName);

        return GetAllBonesInCategoryRecursive(category, bonesByName);
    }

    private BoneCategory? FindCategory(string categoryId, IEnumerable<BoneCategory> categories)
    {
        foreach (var cat in categories)
        {
            if (cat.Id == categoryId)
                return cat;

            var found = FindCategory(categoryId, cat.Children);
            if (found != null)
                return found;
        }
        return null;
    }

    private void GatherBones(Skeleton skeleton, Dictionary<string, Bone> bonesByName)
    {
        void ProcessEntity(IEntity entity)
        {
            if (entity is Bone bone && !bone.IsHiddenBone)
            {
                if (!bonesByName.ContainsKey(bone.BoneName))
                {
                    bonesByName[bone.BoneName] = bone;
                }
            }

            foreach (var child in entity.Children)
            {
                ProcessEntity(child);
            }
        }

        foreach (var child in skeleton.Children)
        {
            ProcessEntity(child);
        }
    }

    private List<IBone> GetAllBonesInCategoryRecursive(BoneCategory category, Dictionary<string, Bone> bonesByName)
    {
        var result = new List<IBone>();

        foreach (var boneName in category.Bones)
        {
            if (bonesByName.TryGetValue(boneName, out var bone))
            {
                result.Add(bone);
            }
        }

        foreach (var child in category.Children)
        {
            if (!child.IsNsfw)
            {
                result.AddRange(GetAllBonesInCategoryRecursive(child, bonesByName));
            }
        }

        return result;
    }

    #endregion
}
