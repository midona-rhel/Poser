using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Poser.Core;
using Poser.Data;
using Poser.Data.Config;
using Poser.Entities;
using Poser.Entities.Capabilities;
using Poser.History;
using Poser.Services;
using Poser.UI.Controls;

namespace Poser.UI.Components;

/// <summary>
/// Renders the Properties panel showing details of the selected entity.
/// Uses capability interfaces to determine what UI to show.
/// </summary>
public class PropertiesPanel
{
    private const float TabBarWidth = 40f;
    private const float LabelWidth = 50f;

    private readonly ISelectionService _selectionService;
    private readonly IActorManager _actorManager;
    private readonly IPosingService _posingService;
    private readonly IBonePosingService _bonePosingService;
    private readonly IAnimationService _animationService;
    private readonly IHistoryService _historyService;
    private readonly IGazeService _gazeService;
    private readonly ISkeletonService _skeletonService;
    private readonly ICameraService _cameraService;
    private readonly IEditorState _editorState;
    private readonly TransformWidget _transformWidget;

    // Reusable animation selectors
    private readonly AnimationSelector _baseAnimationSelector;
    private readonly AnimationSelector _blendAnimationSelector;

    // Category config for skeleton tab
    private readonly CategoryConfig _categoryConfig;

    // Cached category items - rebuilt when skeleton changes
    private readonly List<BoneCategoryListItem> _categoryItems = new();
    private Skeleton? _lastSkeleton;

    // Active tab - determined by entity capabilities
    private int _activeTabIndex = 0;

    // Tracking for slider history
    private float _speedBeforeEdit;
    private bool _isEditingSpeed;

    // Current animation state
    private ushort? _currentBaseId;

    // Gaze mode names for dropdown
    private static readonly string[] GazeModeNames = { "None", "Forward", "Camera", "Entity" };

    public PropertiesPanel(
        ISelectionService selectionService,
        IActorManager actorManager,
        IPosingService posingService,
        IBonePosingService bonePosingService,
        IAnimationService animationService,
        IAnimationDataService animationDataService,
        IHistoryService historyService,
        IGazeService gazeService,
        ISkeletonService skeletonService,
        ICameraService cameraService,
        IEditorState editorState)
    {
        _selectionService = selectionService;
        _actorManager = actorManager;
        _posingService = posingService;
        _bonePosingService = bonePosingService;
        _animationService = animationService;
        _historyService = historyService;
        _gazeService = gazeService;
        _skeletonService = skeletonService;
        _cameraService = cameraService;
        _editorState = editorState;
        _transformWidget = new TransformWidget();

        _baseAnimationSelector = new AnimationSelector(animationDataService);
        _blendAnimationSelector = new AnimationSelector(animationDataService);

        _categoryConfig = CategoryReader.ReadEmbeddedResource();

        _transformWidget.OnTransformCommit += OnTransformCommit;
    }

    private void OnTransformCommit(Transform oldTransform, Transform newTransform)
    {
        var entity = _selectionService.Primary;
        if (entity is IActor actor)
        {
            var action = new TransformActorAction(_posingService, actor, oldTransform, newTransform);
            _historyService.Push(action);
        }
        else if (entity is IBone bone)
        {
            var action = new TransformBoneAction(_bonePosingService, bone, oldTransform, newTransform);
            _historyService.Push(action);
        }
    }

    public void Draw()
    {
        var entity = _selectionService.Primary;

        if (entity == null)
        {
            ImGui.Text("Properties");
            ImGui.Spacing();
            ImGui.TextDisabled("No entity selected");
            return;
        }

        DrawEntity(entity);
    }

    private void DrawEntity(IEntity entity)
    {
        // Header with entity name
        var headerText = entity.Name;
        var headerWidth = ImGui.CalcTextSize(headerText).X;
        var availWidth = ImGui.GetContentRegionAvail().X;
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + (availWidth - headerWidth) * 0.5f);
        ImGui.Text(headerText);

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        float tabBarWidth = TabBarWidth * ImGuiHelpers.GlobalScale;
        float availHeight = ImGui.GetContentRegionAvail().Y;

        // Always show tab bar with all 3 tabs
        using (var tabChild = ImRaii.Child("tab_bar", new Vector2(tabBarWidth, availHeight), false))
        {
            if (tabChild.Success)
            {
                DrawTabBar(entity);
            }
        }
        ImGui.SameLine();

        // Draw content for active tab (only if tab is enabled)
        using (var contentChild = ImRaii.Child("tab_content", new Vector2(-1, availHeight), false))
        {
            if (contentChild.Success)
            {
                DrawActiveTabContent(entity);
            }
        }
    }

    private void DrawTabBar(IEntity entity)
    {
        float buttonSize = TabBarWidth * ImGuiHelpers.GlobalScale - ImGui.GetStyle().WindowPadding.X;
        var size = new Vector2(buttonSize, buttonSize);

        // Determine which tabs are enabled for this entity
        bool transformEnabled = entity is ITransformable;
        bool animationEnabled = entity is IAnimatable animatable && animatable.CanControlAnimation;
        bool skeletonEnabled = entity is ISkeletonOwner;

        // Tab 0: Transform
        DrawTabButton(0, FontAwesomeIcon.ArrowsAlt, "Transform", size, transformEnabled);
        ImGui.Spacing();

        // Tab 1: Animation
        DrawTabButton(1, FontAwesomeIcon.Walking, "Animation", size, animationEnabled);
        ImGui.Spacing();

        // Tab 2: Skeleton
        DrawTabButton(2, FontAwesomeIcon.CircleNodes, "Skeleton", size, skeletonEnabled);
    }

    private void DrawTabButton(int index, FontAwesomeIcon icon, string tooltip, Vector2 size, bool enabled)
    {
        bool isActive = _activeTabIndex == index;

        using (ImRaii.Disabled(!enabled))
        using (ImRaii.PushColor(ImGuiCol.Button, ImGui.GetColorU32(ImGuiCol.TabActive), isActive && enabled))
        using (ImRaii.PushColor(ImGuiCol.ButtonHovered, ImGui.GetColorU32(ImGuiCol.TabHovered)))
        {
            if (ImPoser.CenteredIconButton($"tab_{index}", icon, size, tooltip, enabled))
            {
                if (enabled)
                {
                    _activeTabIndex = index;
                }
            }
        }
    }

    private void DrawActiveTabContent(IEntity entity)
    {
        switch (_activeTabIndex)
        {
            case 0: // Transform
                if (entity is ITransformable)
                    DrawTransformTab(entity);
                else
                    ImGui.TextDisabled("Transform not available for this entity");
                break;
            case 1: // Animation
                if (entity is IAnimatable animatable && animatable.CanControlAnimation)
                    DrawAnimationTab(entity);
                else
                    ImGui.TextDisabled("Animation not available for this entity");
                break;
            case 2: // Skeleton
                if (entity is ISkeletonOwner)
                    DrawSkeletonTab(entity);
                else
                    ImGui.TextDisabled("Skeleton not available for this entity");
                break;
        }
    }

    #region Transform Tab

    private void DrawTransformTab(IEntity entity)
    {
        // Unified transform tab for any ITransformable entity
        if (entity is not ITransformable)
            return;

        // Get the current transform
        Transform transform;
        bool canEdit;

        if (entity is IActor actor)
        {
            // For actors, get effective transform and check if frozen
            transform = _posingService.GetEffectiveTransform(actor);
            canEdit = _animationService.IsFrozen(actor);
        }
        else if (entity is IBone bone)
        {
            // For bones, get from bone transform (uses LastTransform cache)
            transform = bone.Transform;
            canEdit = true; // Bones are always editable
        }
        else
        {
            // Generic ITransformable
            transform = entity.Transform;
            canEdit = false;
        }

        // Draw the unified transform widget
        if (_transformWidget.Draw("transform", ref transform, !canEdit))
        {
            ApplyTransform(entity, transform);
        }
    }

    private void ApplyTransform(IEntity entity, Transform transform)
    {
        if (entity is IActor actor)
        {
            _posingService.SetTransformOverride(actor, transform);
        }
        else if (entity is IBone bone)
        {
            // For bones, apply through bone posing service
            // Note: Gaze is NOT auto-locked - bone posing works additively on top of gaze (like Brio)
            _bonePosingService.ApplyTransform(bone, transform);
        }
    }

    #endregion

    #region Skeleton Tab

    private void DrawSkeletonTab(IEntity entity)
    {
        if (entity is not IActor actor)
            return;

        var skeleton = _skeletonService.GetSkeleton(actor) as Skeleton;
        if (skeleton == null || !skeleton.IsValid)
        {
            ImGui.TextDisabled("No skeleton available");
            return;
        }

        // Rebuild category items if skeleton changed
        if (skeleton != _lastSkeleton)
        {
            _categoryItems.Clear();
            foreach (var category in _categoryConfig.RootCategories)
            {
                if (category.IsNsfw)
                    continue;

                var categoryItem = new BoneCategoryListItem(category, skeleton, 0, _selectionService);
                if (categoryItem.HasContent)
                {
                    _categoryItems.Add(categoryItem);
                }
            }
            _lastSkeleton = skeleton;
        }

        var tabHovered = ImPoser.GetTabHoveredColor();
        var tabActive = ImPoser.GetTabActiveColor();

        // Draw categories directly in a table
        using (var child = ImRaii.Child("bone_list", new Vector2(-1, -1), false))
        {
            if (child.Success)
            {
                var tableFlags = ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY;
                using (var table = ImRaii.Table("skeleton_table", 3, tableFlags))
                {
                    if (table.Success)
                    {
                        // Column setup: Name (stretchy), Freeze (fixed), Visibility (fixed)
                        ImGui.TableSetupColumn("Name", ImGuiTableColumnFlags.WidthStretch);
                        ImGui.TableSetupColumn("F", ImGuiTableColumnFlags.WidthFixed, 24 * ImGuiHelpers.GlobalScale);
                        ImGui.TableSetupColumn("V", ImGuiTableColumnFlags.WidthFixed, 24 * ImGuiHelpers.GlobalScale);

                        // Draw categories directly (no skeleton row in properties)
                        foreach (var categoryItem in _categoryItems)
                        {
                            categoryItem.Draw(tabHovered, tabActive, _selectionService);
                        }
                    }
                }
            }
        }
    }

    #endregion

    #region Animation Tab

    private void DrawAnimationTab(IEntity entity)
    {
        if (entity is not IActor actor)
            return;

        float labelWidth = LabelWidth * ImGuiHelpers.GlobalScale;
        bool isFrozen = _animationService.IsFrozen(actor);

        // Animation Section
        ImGui.TextDisabled("Animation");
        ImGui.Spacing();
        DrawAnimationSection(actor, labelWidth);

        ImGui.Spacing();
        ImGui.Separator();

        // Playback Section
        ImGui.TextDisabled("Playback");
        ImGui.Spacing();
        DrawSpeedSection(actor, labelWidth);
        ImGui.Spacing();
        DrawScrubSection(actor, isFrozen, labelWidth);

        ImGui.Spacing();
        ImGui.Separator();

        // Gaze Section (actors are IGazeable)
        if (entity is IGazeable)
        {
            ImGui.TextDisabled("Gaze");
            ImGui.Spacing();
            DrawGazeSection(actor, labelWidth);
        }
    }

    private void DrawAnimationSection(IActor actor, float labelWidth)
    {
        bool hasOverride = _animationService.HasBaseOverride(actor);

        // Base Animation
        ImGui.Text("Base");
        ImGui.SameLine(labelWidth);

        float selectorWidth = ImGui.GetContentRegionAvail().X - 35 * ImGuiHelpers.GlobalScale;

        if (_baseAnimationSelector.Draw("base_anim", _currentBaseId, id =>
        {
            ushort? oldId = hasOverride ? _currentBaseId : null;
            _animationService.ApplyBaseAnimation(actor, id, true);
            _currentBaseId = id;

            var action = new BaseAnimationAction(_animationService, actor, oldId, id);
            _historyService.Record(action);
        }, selectorWidth))
        {
        }

        ImGui.SameLine();

        using (ImRaii.Disabled(!hasOverride))
        {
            if (ImPoser.CenteredIconButton("stop_base", FontAwesomeIcon.Stop, null, "Stop Animation"))
            {
                ushort? oldId = _currentBaseId;
                _animationService.StopBaseAnimation(actor);

                var action = new BaseAnimationAction(_animationService, actor, oldId, null);
                _historyService.Record(action);

                _currentBaseId = null;
            }
        }

        // Blend Animation
        ImGui.Text("Blend");
        ImGui.SameLine(labelWidth);

        if (_blendAnimationSelector.Draw("blend_anim", null, id =>
        {
            _animationService.PlayBlendAnimation(actor, id);
        }, selectorWidth))
        {
        }
    }

    private void DrawSpeedSection(IActor actor, float labelWidth)
    {
        ImGui.Text("Speed");
        ImGui.SameLine(labelWidth);

        float speed = _animationService.GetSpeed(actor);

        if (ImGui.IsItemActive() && !_isEditingSpeed)
        {
            _speedBeforeEdit = speed;
            _isEditingSpeed = true;
        }

        ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X - 70 * ImGuiHelpers.GlobalScale);
        if (ImGui.SliderFloat("##speed", ref speed, 0f, 3f, "%.2fx"))
        {
            _animationService.SetSpeed(actor, speed);
        }

        if (_isEditingSpeed && ImGui.IsItemDeactivatedAfterEdit())
        {
            _isEditingSpeed = false;
            if (MathF.Abs(_speedBeforeEdit - speed) > 0.001f)
            {
                var action = new SpeedChangeAction(_animationService, actor, _speedBeforeEdit, speed);
                _historyService.Record(action);
            }
        }

        ImGui.SameLine();

        bool isPlaying = speed > 0f;
        var playPauseIcon = isPlaying ? FontAwesomeIcon.Pause : FontAwesomeIcon.Play;

        using (ImRaii.PushColor(ImGuiCol.Button, ImGui.GetColorU32(ImGuiCol.ButtonActive), !isPlaying))
        {
            if (ImPoser.CenteredIconButton("play_pause", playPauseIcon, null, isPlaying ? "Pause" : "Play"))
            {
                float oldSpeed = _animationService.GetSpeed(actor);
                float newSpeed = isPlaying ? 0f : 1f;
                _animationService.SetSpeed(actor, newSpeed);

                var action = new SpeedChangeAction(_animationService, actor, oldSpeed, newSpeed);
                _historyService.Record(action);
            }
        }

        ImGui.SameLine();

        if (ImPoser.CenteredIconButton("reset_speed", FontAwesomeIcon.Undo, null, "Reset Speed"))
        {
            float oldSpeed = _animationService.GetSpeed(actor);
            _animationService.ResetSpeed(actor);

            var action = new SpeedChangeAction(_animationService, actor, oldSpeed, 1f);
            _historyService.Record(action);
        }
    }

    private void DrawScrubSection(IActor actor, bool isFrozen, float labelWidth)
    {
        float? duration = _animationService.GetAnimationDuration(actor);
        float? currentTime = _animationService.GetAnimationTime(actor);

        ImGui.Text("Time");
        ImGui.SameLine(labelWidth);

        float time = currentTime ?? 0f;
        float maxTime = duration ?? 1f;
        bool canScrub = isFrozen && duration.HasValue && currentTime.HasValue;

        using (ImRaii.Disabled(!canScrub))
        {
            ImGui.SetNextItemWidth(-1);
            string format = canScrub ? "%.2fs" : (isFrozen ? "N/A" : "Freeze to scrub");
            if (ImGui.SliderFloat("##time", ref time, 0f, maxTime, format))
            {
                if (canScrub)
                {
                    _animationService.SetAnimationTime(actor, time);
                }
            }
        }
    }

    private void DrawGazeSection(IActor actor, float labelWidth)
    {
        // Get current gaze state from service
        var gazeState = _gazeService.GetGazeState(actor);
        bool gazeEnabled = _gazeService.IsGazeEnabled(actor);

        // Enable Face Control toggle (like Brio)
        ImGui.Text("Enable");
        ImGui.SameLine(labelWidth);
        if (ImGui.Checkbox("##enable_gaze", ref gazeEnabled))
        {
            if (gazeEnabled)
                _gazeService.EnableGaze(actor);
            else
                _gazeService.DisableGaze(actor);
        }

        // Only show rest of UI if gaze is enabled
        if (!gazeEnabled)
            return;

        ImGui.Spacing();

        // Mode selector (like Brio's selector strip)
        ImGui.Text("Mode");
        ImGui.SameLine(labelWidth);
        int modeIndex = (int)gazeState.Mode;
        ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X);
        if (ImGui.Combo("##gaze_mode", ref modeIndex, GazeModeNames, GazeModeNames.Length))
        {
            _gazeService.SetGazeMode(actor, (GazeTargetMode)modeIndex);
        }

        // Entity selector for Entity mode
        if (gazeState.Mode == GazeTargetMode.Entity)
        {
            ImGui.Text("Target");
            ImGui.SameLine(labelWidth);
            ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X);

            var actors = _actorManager.Actors;
            var currentTarget = gazeState.TargetEntity;

            if (ImGui.BeginCombo("##gaze_target", currentTarget?.Name ?? "Select..."))
            {
                foreach (var targetActor in actors)
                {
                    if (targetActor == actor) continue; // Can't look at self
                    bool isSelected = currentTarget == targetActor;
                    if (ImGui.Selectable(targetActor.Name, isSelected))
                    {
                        _gazeService.SetGazeTarget(actor, targetActor);
                    }
                }
                ImGui.EndCombo();
            }
        }

        ImGui.Spacing();

        // ToggleLock controls for Camera mode (like Brio)
        // Show Eyes, Head, Body with lock buttons
        DrawGazeLockToggle("Eyes", actor, GazeTargetType.Eyes);
        ImGui.SameLine();
        DrawGazeLockToggle("Head", actor, GazeTargetType.Head);
        ImGui.SameLine();
        DrawGazeLockToggle("Body", actor, GazeTargetType.Body);
    }

    private void DrawGazeLockToggle(string label, IActor actor, GazeTargetType targetType)
    {
        bool isLocked = _gazeService.IsPartLocked(actor, targetType);

        ImGui.Text(label);
        ImGui.SameLine();

        var icon = isLocked ? FontAwesomeIcon.Lock : FontAwesomeIcon.Unlock;
        if (ImPoser.IconButton($"gaze_lock_{label}", icon, null, isLocked ? "Unlock" : "Lock at camera"))
        {
            var cameraPos = _cameraService.GetCameraPosition();
            _gazeService.SetTargetLock(actor, !isLocked, targetType, cameraPos);
        }
    }

    #endregion


    /// <summary>
    /// Represents a tab in the properties panel.
    /// </summary>
    private class EntityTab
    {
        public string Name { get; }
        public FontAwesomeIcon Icon { get; }
        public Action<IEntity> Draw { get; }

        public EntityTab(string name, FontAwesomeIcon icon, Action<IEntity> draw)
        {
            Name = name;
            Icon = icon;
            Draw = draw;
        }
    }
}
