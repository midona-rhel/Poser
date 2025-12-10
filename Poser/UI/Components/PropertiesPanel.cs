using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Poser.Core;
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
/// Can operate in live mode (follows selection) or frozen mode (locked to specific entities).
/// </summary>
public class PropertiesPanel : IDisposable
{
    private const float TabBarWidth = 40f;
    private const float LabelWidth = 50f;

    private readonly ISelectionService _selectionService;
    private readonly IActorManager _actorManager;
    private readonly IPosingService _posingService;
    private readonly IBonePosingService _bonePosingService;
    private readonly IAnimationService _animationService;
    private readonly IAnimationDataService _animationDataService;
    private readonly IHistoryService _historyService;
    private readonly IGazeService _gazeService;
    private readonly ICameraService _cameraService;
    private readonly TransformWidget _transformWidget;

    // Reusable animation selectors
    private readonly AnimationSelector _baseAnimationSelector;
    private readonly AnimationSelector _blendAnimationSelector;

    // Active tab - determined by entity capabilities
    private int _activeTabIndex = 0;

    // Tracking for slider history
    private float _speedBeforeEdit;
    private bool _isEditingSpeed;

    // Current animation state
    private ushort? _currentBaseId;

    // Gaze mode names for dropdown
    private static readonly string[] GazeModeNames = { "None", "Forward", "Camera", "Entity" };

    // Track bone transform frame-by-frame for incremental deltas (like gizmo)
    private IBone? _trackingBone;
    private Transform? _lastFrameTransform;

    // Placeholder for disabled UI
    private string _placeholderText = "";

    // Frozen mode: when set, panel shows these entities instead of live selection
    private List<IEntity>? _frozenEntities;

    /// <summary>
    /// Event fired when user clicks pop-out button. Passes current selection to create detached window.
    /// </summary>
    public event Action<IReadOnlyList<IEntity>>? OnPopOutRequested;

    public PropertiesPanel(
        ISelectionService selectionService,
        IActorManager actorManager,
        IPosingService posingService,
        IBonePosingService bonePosingService,
        IAnimationService animationService,
        IAnimationDataService animationDataService,
        IHistoryService historyService,
        IGazeService gazeService,
        ICameraService cameraService)
    {
        _selectionService = selectionService;
        _actorManager = actorManager;
        _posingService = posingService;
        _bonePosingService = bonePosingService;
        _animationService = animationService;
        _animationDataService = animationDataService;
        _historyService = historyService;
        _gazeService = gazeService;
        _cameraService = cameraService;
        _transformWidget = new TransformWidget();

        _baseAnimationSelector = new AnimationSelector(animationDataService);
        _blendAnimationSelector = new AnimationSelector(animationDataService);

        _transformWidget.OnTransformCommit += OnTransformCommit;
    }

    /// <summary>
    /// Freezes this panel to show specific entities instead of following live selection.
    /// Used for detached/pop-out windows.
    /// </summary>
    public void FreezeToEntities(IReadOnlyList<IEntity> entities)
    {
        _frozenEntities = entities.ToList();
    }

    /// <summary>
    /// Gets the entities this panel is currently showing (frozen or live selection).
    /// </summary>
    private IReadOnlyList<IEntity> GetCurrentEntities()
    {
        if (_frozenEntities != null)
            return _frozenEntities;
        return _selectionService.Selected;
    }

    /// <summary>
    /// Gets the primary entity this panel is currently showing.
    /// </summary>
    private IEntity? GetPrimaryEntity()
    {
        if (_frozenEntities != null)
            return _frozenEntities.FirstOrDefault();
        return _selectionService.Primary;
    }

    private void OnTransformCommit(Transform oldTransform, Transform newTransform)
    {
        var entity = GetPrimaryEntity();
        if (entity is IActor actor)
        {
            var action = new TransformActorAction(_posingService, actor, oldTransform, newTransform);
            _historyService.Push(action);
        }
        else if (entity is IBone bone)
        {
            // Use Record instead of Push - the transform was already applied during drag
            var action = new TransformBoneAction(_bonePosingService, bone, oldTransform, newTransform);
            _historyService.Record(action);
        }
    }

    public void Draw()
    {
        var entities = GetCurrentEntities();
        var entity = GetPrimaryEntity();

        if (entity == null || entities.Count == 0)
        {
            DrawEmptyHeader();
            ImGui.Spacing();
            ImGui.TextDisabled("No entity selected");
            return;
        }

        DrawEntity(entity, entities);
    }

    private void DrawEmptyHeader()
    {
        var headerText = "Properties";
        var headerWidth = ImGui.CalcTextSize(headerText).X;
        var availWidth = ImGui.GetContentRegionAvail().X;

        // Only show pop-out button if not already frozen (i.e., in main panel)
        float buttonWidth = _frozenEntities == null ? 24 * ImGuiHelpers.GlobalScale : 0;

        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + (availWidth - headerWidth) * 0.5f);
        ImGui.Text(headerText);
    }

    private void DrawEntity(IEntity primaryEntity, IReadOnlyList<IEntity> allEntities)
    {
        // Header with selection summary and pop-out button
        DrawSelectionHeader(allEntities);

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
                DrawTabBar(primaryEntity);
            }
        }
        ImGui.SameLine();

        // Draw content for active tab (only if tab is enabled)
        using (var contentChild = ImRaii.Child("tab_content", new Vector2(-1, availHeight), false))
        {
            if (contentChild.Success)
            {
                DrawActiveTabContent(primaryEntity);
            }
        }
    }

    /// <summary>
    /// Draws the selection header with smart formatting and pop-out button.
    /// </summary>
    private void DrawSelectionHeader(IReadOnlyList<IEntity> entities)
    {
        var headerText = FormatSelectionText(entities);
        var headerWidth = ImGui.CalcTextSize(headerText).X;
        var availWidth = ImGui.GetContentRegionAvail().X;

        // Pop-out button (only in live mode, not frozen)
        float buttonSize = 20 * ImGuiHelpers.GlobalScale;
        bool showPopOut = _frozenEntities == null && entities.Count > 0;

        if (showPopOut)
        {
            // Center text accounting for button on right
            float totalContentWidth = headerWidth + buttonSize + ImGui.GetStyle().ItemSpacing.X;
            float startX = ImGui.GetCursorPosX() + (availWidth - totalContentWidth) * 0.5f;

            ImGui.SetCursorPosX(startX);
            ImGui.AlignTextToFramePadding();
            ImGui.Text(headerText);

            ImGui.SameLine();

            if (ImPoser.CenteredIconButton("pop_out", FontAwesomeIcon.ExternalLinkAlt, new Vector2(buttonSize, buttonSize), "Pop out to separate window"))
            {
                OnPopOutRequested?.Invoke(entities);
            }
        }
        else
        {
            // Just center the text
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + (availWidth - headerWidth) * 0.5f);
            ImGui.Text(headerText);
        }
    }

    /// <summary>
    /// Formats selection text based on entity count and types.
    /// </summary>
    private static string FormatSelectionText(IReadOnlyList<IEntity> entities)
    {
        if (entities.Count == 0)
            return "Properties";

        if (entities.Count == 1)
            return PoserSettings.Instance.GetDisplayName(entities[0]);

        // Check if all entities are the same type
        // Note: Bone and VirtualBone are both IBone, BoneCategory is separate
        var first = entities[0];
        var firstType = GetEntityTypeCategory(first);

        bool allSameType = entities.All(e => GetEntityTypeCategory(e) == firstType);

        if (entities.Count == 2)
        {
            // Two entities: show both names
            if (allSameType)
                return $"{PoserSettings.Instance.GetDisplayName(entities[0])}, {PoserSettings.Instance.GetDisplayName(entities[1])}";
            else
                return $"{PoserSettings.Instance.GetDisplayName(entities[0])} + 1 entity";
        }

        // 3+ entities
        int otherCount = entities.Count - 1;
        string typeName = allSameType ? GetTypePluralName(firstType, otherCount) : "entities";

        return $"{PoserSettings.Instance.GetDisplayName(first)} + {otherCount} {typeName}";
    }

    /// <summary>
    /// Gets a category string for grouping entity types.
    /// </summary>
    private static string GetEntityTypeCategory(IEntity entity)
    {
        return entity switch
        {
            IBone => "bone",
            IActor => "actor",
            // BoneCategoryListItem selections would be VirtualBone
            _ => entity.GetType().Name
        };
    }

    /// <summary>
    /// Gets the plural name for an entity type category.
    /// </summary>
    private static string GetTypePluralName(string typeCategory, int count)
    {
        return typeCategory switch
        {
            "bone" => count == 1 ? "bone" : "bones",
            "actor" => count == 1 ? "actor" : "actors",
            _ => count == 1 ? "entity" : "entities"
        };
    }

    private void DrawTabBar(IEntity entity)
    {
        float buttonSize = TabBarWidth * ImGuiHelpers.GlobalScale - ImGui.GetStyle().WindowPadding.X;
        var size = new Vector2(buttonSize, buttonSize);

        // Determine which tabs are enabled for this entity
        bool transformEnabled = entity is ITransformable;
        bool animationEnabled = entity is IAnimatable animatable && animatable.CanControlAnimation;

        // Tab 0: Transform
        DrawTabButton(0, FontAwesomeIcon.ArrowsAlt, "Transform", size, transformEnabled);
        ImGui.Spacing();

        // Tab 1: Animation
        DrawTabButton(1, FontAwesomeIcon.Walking, "Animation", size, animationEnabled);
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
        // Determine which tabs are enabled for this entity
        bool transformEnabled = entity is ITransformable;
        bool animationEnabled = entity is IAnimatable animatable && animatable.CanControlAnimation;

        switch (_activeTabIndex)
        {
            case 0: // Transform
                using (ImRaii.Disabled(!transformEnabled))
                {
                    DrawTransformTab(entity);
                }
                break;
            case 1: // Animation
                using (ImRaii.Disabled(!animationEnabled))
                {
                    DrawAnimationTab(entity);
                }
                break;
        }
    }

    #region Transform Tab

    private void DrawTransformTab(IEntity entity)
    {
        // Get the current transform (use default if entity not transformable)
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
            // For bones, use last frame's transform if actively tracking (for incremental deltas)
            // Otherwise get fresh from bone
            transform = (_trackingBone == bone && _lastFrameTransform.HasValue)
                ? _lastFrameTransform.Value
                : bone.Transform;
            canEdit = true; // Bones are always editable
        }
        else if (entity is ITransformable)
        {
            // Generic ITransformable
            transform = entity.Transform;
            canEdit = false;
        }
        else
        {
            // Not transformable - show disabled placeholder
            transform = Transform.Identity;
            canEdit = false;
        }

        // Draw the unified transform widget
        if (_transformWidget.Draw("transform", ref transform, !canEdit))
        {
            if (entity is ITransformable)
            {
                ApplyTransform(entity, transform);
            }
        }
        else
        {
            // Widget returned false - drag ended or no change
            // Clear tracking when editing stops
            if (_trackingBone != null)
            {
                _trackingBone = null;
                _lastFrameTransform = null;
            }
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
            // Start tracking if not already
            if (_trackingBone != bone)
            {
                _trackingBone = bone;
                _lastFrameTransform = bone.Transform;
            }

            // Use gizmo-style incremental deltas: compare to last frame
            var lastObserved = _lastFrameTransform ?? bone.Transform;
            _bonePosingService.ApplyTransform(bone, transform, lastObserved);

            // Update last frame transform for next iteration
            _lastFrameTransform = transform;
        }
    }

    #endregion

    #region Animation Tab

    private void DrawAnimationTab(IEntity entity)
    {
        float labelWidth = LabelWidth * ImGuiHelpers.GlobalScale;

        // Animation Section
        ImGui.TextDisabled("Animation");
        ImGui.Spacing();

        if (entity is IActor actor)
        {
            bool isFrozen = _animationService.IsFrozen(actor);

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

            // Gaze Section
            ImGui.TextDisabled("Gaze");
            ImGui.Spacing();
            DrawGazeSection(actor, labelWidth);
        }
        else
        {
            // Placeholder UI for non-actor entities - matches full actor UI structure, all disabled
            float selectorWidth = ImGui.GetContentRegionAvail().X - 35 * ImGuiHelpers.GlobalScale;
            float sliderWidth = ImGui.GetContentRegionAvail().X - 70 * ImGuiHelpers.GlobalScale;

            // Current animation
            ImGui.Text("Current");
            ImGui.SameLine(labelWidth);
            ImGui.TextDisabled("None");

            // Base animation
            ImGui.Text("Base");
            ImGui.SameLine(labelWidth);
            ImGui.SetNextItemWidth(selectorWidth);
            ImGui.InputText("##base_ph", ref _placeholderText, 100, ImGuiInputTextFlags.ReadOnly);
            ImGui.SameLine();
            ImPoser.CenteredIconButton("stop_ph", FontAwesomeIcon.Stop, null, "Stop Animation");

            // Blend animation
            ImGui.Text("Blend");
            ImGui.SameLine(labelWidth);
            ImGui.SetNextItemWidth(selectorWidth);
            ImGui.InputText("##blend_ph", ref _placeholderText, 100, ImGuiInputTextFlags.ReadOnly);

            ImGui.Spacing();
            ImGui.Separator();

            // Playback Section
            ImGui.TextDisabled("Playback");
            ImGui.Spacing();

            // Speed
            ImGui.Text("Speed");
            ImGui.SameLine(labelWidth);
            float speed = 1f;
            ImGui.SetNextItemWidth(sliderWidth);
            ImGui.SliderFloat("##speed_ph", ref speed, 0f, 3f, "%.2fx");
            ImGui.SameLine();
            ImPoser.CenteredIconButton("play_ph", FontAwesomeIcon.Play, null, "Play");
            ImGui.SameLine();
            ImPoser.CenteredIconButton("reset_ph", FontAwesomeIcon.Undo, null, "Reset Speed");

            // Time
            ImGui.Spacing();
            ImGui.Text("Time");
            ImGui.SameLine(labelWidth);
            float time = 0f;
            ImGui.SetNextItemWidth(sliderWidth);
            ImGui.SliderFloat("##time_ph", ref time, 0f, 1f, "N/A");

            ImGui.Spacing();
            ImGui.Separator();

            // Gaze Section
            ImGui.TextDisabled("Gaze");
            ImGui.Spacing();

            // Enable
            ImGui.Text("Enable");
            ImGui.SameLine(labelWidth);
            bool enable = false;
            ImGui.Checkbox("##enable_ph", ref enable);
            ImGui.SameLine();
            ImPoser.IconButton("reset_gaze_ph", FontAwesomeIcon.Undo, null, "Reset gaze");

            ImGui.Spacing();

            // Mode
            ImGui.Text("Mode");
            ImGui.SameLine(labelWidth);
            ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X);
            int mode = 0;
            ImGui.Combo("##mode_ph", ref mode, GazeModeNames, GazeModeNames.Length);

            // Target
            ImGui.Text("Target");
            ImGui.SameLine(labelWidth);
            ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X);
            if (ImGui.BeginCombo("##target_ph", "Select..."))
            {
                ImGui.EndCombo();
            }

            ImGui.Spacing();

            // Track
            ImGui.Text("Track");
            ImGui.SameLine(labelWidth);
            bool track = false;
            ImGui.Checkbox("Eyes##track_ph1", ref track);
            ImGui.SameLine();
            ImGui.Checkbox("Head##track_ph2", ref track);
            ImGui.SameLine();
            ImGui.Checkbox("Body##track_ph3", ref track);

            // Lock
            ImGui.Text("Lock");
            ImGui.SameLine(labelWidth);
            ImPoser.IconButton("lock_eyes_ph", FontAwesomeIcon.Unlock, null, "Lock Eyes");
            ImGui.SameLine();
            ImGui.Text("Eyes");
            ImGui.SameLine();
            ImPoser.IconButton("lock_head_ph", FontAwesomeIcon.Unlock, null, "Lock Head");
            ImGui.SameLine();
            ImGui.Text("Head");
            ImGui.SameLine();
            ImPoser.IconButton("lock_body_ph", FontAwesomeIcon.Unlock, null, "Lock Body");
            ImGui.SameLine();
            ImGui.Text("Body");
        }
    }

    private void DrawAnimationSection(IActor actor, float labelWidth)
    {
        bool hasOverride = _animationService.HasBaseOverride(actor);

        // Use our override ID, or fall back to game's current animation
        var displayId = _currentBaseId ?? _animationService.GetCurrentBaseAnimation(actor);

        // Base Animation
        ImGui.AlignTextToFramePadding();
        ImGui.Text("Base");
        ImGui.SameLine(labelWidth);

        float selectorWidth = ImGui.GetContentRegionAvail().X - 35 * ImGuiHelpers.GlobalScale;

        if (_baseAnimationSelector.Draw("base_anim", displayId, id =>
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
        ImGui.AlignTextToFramePadding();
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
        ImGui.AlignTextToFramePadding();
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

        ImGui.AlignTextToFramePadding();
        ImGui.Text("Time");
        ImGui.SameLine(labelWidth);

        float time = currentTime ?? 0f;
        float maxTime = duration ?? 1f;
        bool canScrub = isFrozen && duration.HasValue && currentTime.HasValue;

        // Time slider uses full width (no trailing buttons)
        using (ImRaii.Disabled(!canScrub))
        {
            ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X);
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

        // Enable toggle
        ImGui.AlignTextToFramePadding();
        ImGui.Text("Enable");
        ImGui.SameLine(labelWidth);
        if (ImGui.Checkbox("##enable_gaze", ref gazeEnabled))
        {
            if (gazeEnabled)
                _gazeService.EnableGaze(actor);
            else
                _gazeService.DisableGaze(actor);
        }

        // Reset button on same line
        ImGui.SameLine();
        if (ImPoser.IconButton("gaze_reset", FontAwesomeIcon.Undo, null, "Reset gaze to game default"))
        {
            _gazeService.ResetGaze(actor);
        }

        ImGui.Spacing();

        // All remaining controls always visible, disabled when gaze is not enabled
        using (ImRaii.Disabled(!gazeEnabled))
        {
            // Mode + Target on same row
            ImGui.AlignTextToFramePadding();
            ImGui.Text("Mode");
            ImGui.SameLine(labelWidth);

            // Calculate widths: each dropdown gets half the remaining space
            float availableWidth = ImGui.GetContentRegionAvail().X;
            float targetLabelWidth = ImGui.CalcTextSize("Target").X + ImGui.GetStyle().ItemSpacing.X;
            float comboWidth = (availableWidth - targetLabelWidth) / 2 - ImGui.GetStyle().ItemSpacing.X;

            int modeIndex = (int)gazeState.Mode;
            ImGui.SetNextItemWidth(comboWidth);
            if (ImGui.Combo("##gaze_mode", ref modeIndex, GazeModeNames, GazeModeNames.Length))
            {
                _gazeService.SetGazeMode(actor, (GazeTargetMode)modeIndex);
            }

            ImGui.SameLine();
            ImGui.AlignTextToFramePadding();
            ImGui.Text("Target");
            ImGui.SameLine();

            using (ImRaii.Disabled(gazeState.Mode != GazeTargetMode.Entity))
            {
                var actors = _actorManager.Actors;
                var currentTarget = gazeState.TargetEntity;

                ImGui.SetNextItemWidth(comboWidth);
                string targetDisplayName = currentTarget != null ? PoserSettings.Instance.GetDisplayName(currentTarget) : "Select...";
                if (ImGui.BeginCombo("##gaze_target", targetDisplayName))
                {
                    foreach (var targetActor in actors)
                    {
                        if (targetActor == actor) continue; // Can't look at self
                        bool isSelected = currentTarget == targetActor;
                        if (ImGui.Selectable(PoserSettings.Instance.GetDisplayName(targetActor), isSelected))
                        {
                            _gazeService.SetGazeTarget(actor, targetActor);
                        }
                    }
                    ImGui.EndCombo();
                }
            }

            ImGui.Spacing();

            // Track + Lock combined on single row: [Eyes ✓][🔒] [Head ✓][🔒] [Body ✓][🔒]
            // Spread evenly across available width
            float groupWidth = ImGui.GetContentRegionAvail().X / 3;

            DrawGazePartGroup("Eyes", actor, gazeState, GazeTargetType.Eyes, groupWidth);
            ImGui.SameLine();
            DrawGazePartGroup("Head", actor, gazeState, GazeTargetType.Head, groupWidth);
            ImGui.SameLine();
            DrawGazePartGroup("Body", actor, gazeState, GazeTargetType.Body, groupWidth);
        }
    }

    private void DrawGazePartGroup(string label, IActor actor, GazeState gazeState, GazeTargetType targetType, float groupWidth)
    {
        // Draw checkbox + lock button for one body part
        bool isTracking = gazeState.TargetType.HasFlag(targetType);
        bool isLocked = _gazeService.IsPartLocked(actor, targetType);

        // Checkbox for tracking
        if (ImGui.Checkbox($"{label}##track_{label}", ref isTracking))
        {
            GazeTargetType newTargetType;
            if (isTracking)
                newTargetType = gazeState.TargetType | targetType;
            else
                newTargetType = gazeState.TargetType & ~targetType;

            _gazeService.SetGazeTargetType(actor, newTargetType);
        }

        ImGui.SameLine();

        // Lock button
        var icon = isLocked ? FontAwesomeIcon.Lock : FontAwesomeIcon.Unlock;
        using (ImRaii.PushColor(ImGuiCol.Button, ImGui.GetColorU32(ImGuiCol.ButtonActive), isLocked))
        {
            if (ImPoser.IconButton($"lock_{label}", icon, null, isLocked ? $"Unlock {label}" : $"Lock {label}"))
            {
                var cameraPos = _cameraService.GetCameraPosition();
                _gazeService.SetTargetLock(actor, !isLocked, targetType, cameraPos);
            }
        }
    }

    #endregion

    public void Dispose()
    {
        _transformWidget.OnTransformCommit -= OnTransformCommit;
        GC.SuppressFinalize(this);
    }
}
