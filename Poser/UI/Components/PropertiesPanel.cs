using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Poser.Controllers;
using Poser.Core;
using Poser.Entities;
using Poser.History;
using Poser.Services;
using Poser.UI.Controls;

namespace Poser.UI.Components;

public enum PropertiesTab
{
    Transform,
    Animation
}

/// <summary>
/// Renders the Properties panel showing details of the selected entity.
/// Fixed height, non-collapsible panel anchored at the bottom.
/// </summary>
public class PropertiesPanel
{
    private const float TabBarWidth = 40f;
    private const float LabelWidth = 50f;

    private readonly IEditorState _editorState;
    private readonly IActorManager _actorManager;
    private readonly IPosingService _posingService;
    private readonly IBonePosingService _bonePosingService;
    private readonly IAnimationService _animationService;
    private readonly IHistoryService _historyService;
    private readonly IGazeService _gazeService;
    private readonly IPosingController _controller;
    private readonly TransformWidget _transformWidget;

    // Reusable animation selectors
    private readonly AnimationSelector _baseAnimationSelector;
    private readonly AnimationSelector _blendAnimationSelector;

    private PropertiesTab _activeTab = PropertiesTab.Transform;

    // Tracking for slider history (we create history on release, not every frame)
    private bool _isEditingSpeed;

    // Current animation state
    private ushort? _currentBaseId;

    private static readonly string[] GazeModeNames = { "None", "Forward", "Camera", "Entity" };

    public PropertiesPanel(
        IEditorState editorState,
        IActorManager actorManager,
        IPosingService posingService,
        IBonePosingService bonePosingService,
        IAnimationService animationService,
        IAnimationDataService animationDataService,
        IHistoryService historyService,
        IGazeService gazeService,
        IPosingController controller)
    {
        _editorState = editorState;
        _actorManager = actorManager;
        _posingService = posingService;
        _bonePosingService = bonePosingService;
        _animationService = animationService;
        _historyService = historyService;
        _gazeService = gazeService;
        _controller = controller;
        _transformWidget = new TransformWidget();

        // Create reusable animation selectors
        _baseAnimationSelector = new AnimationSelector(animationDataService);
        _blendAnimationSelector = new AnimationSelector(animationDataService);

        // Wire up transform history
        _transformWidget.OnTransformCommit += OnTransformCommit;
    }

    private void OnTransformCommit(Transform oldTransform, Transform newTransform)
    {
        // Handle transform commit for actors only (bones use different history)
        var actor = _editorState.SelectedActor;
        if (actor == null) return;

        var action = new TransformHistoryAction(_posingService, actor, oldTransform, newTransform);
        _historyService.Push(action);
    }

    public void Draw()
    {
        var primarySelection = _editorState.PrimarySelection;
        var selectedCategory = _editorState.SelectedCategory;

        // Check what we have selected
        bool hasActorSelected = _editorState.SelectedActor != null;
        bool hasBoneSelected = _editorState.SelectedBone != null;
        bool hasCategorySelected = selectedCategory != null;

        if (primarySelection == null && !hasCategorySelected)
        {
            ImGui.Text("Properties");
            ImGui.Spacing();
            ImGui.TextDisabled("No entity selected");
            return;
        }

        // Entity name header (centered)
        string headerText;
        if (hasCategorySelected)
        {
            headerText = $"Category: {selectedCategory}";
        }
        else if (primarySelection is IBone bone)
        {
            headerText = bone.DisplayName;
        }
        else if (primarySelection is IActor actor)
        {
            headerText = actor.Name;
        }
        else
        {
            headerText = primarySelection?.Name ?? "Unknown";
        }

        var headerWidth = ImGui.CalcTextSize(headerText).X;
        var availWidth = ImGui.GetContentRegionAvail().X;
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + (availWidth - headerWidth) * 0.5f);
        ImGui.Text(headerText);

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        // Layout: vertical tab bar on left, content on right
        float tabBarWidth = TabBarWidth * ImGuiHelpers.GlobalScale;
        float availHeight = ImGui.GetContentRegionAvail().Y;

        // Only show tab bar for actors (bones/categories only have transform)
        bool showAnimationTab = hasActorSelected && !hasBoneSelected && !hasCategorySelected;

        if (showAnimationTab)
        {
            // Draw vertical tab bar
            using (var tabChild = ImRaii.Child("tab_bar", new Vector2(tabBarWidth, availHeight), false))
            {
                if (tabChild.Success)
                {
                    DrawVerticalTabBar();
                }
            }

            ImGui.SameLine();
        }

        // Draw content area
        using (var contentChild = ImRaii.Child("tab_content", new Vector2(-1, availHeight), false))
        {
            if (contentChild.Success)
            {
                if (hasCategorySelected)
                {
                    DrawCategoryContent();
                }
                else if (hasBoneSelected)
                {
                    DrawBoneContent(_editorState.SelectedBone!);
                }
                else if (hasActorSelected)
                {
                    DrawTabContent(_editorState.SelectedActor!);
                }
            }
        }
    }

    private void DrawCategoryContent()
    {
        var categoryBones = _editorState.GetSelectedCategoryBones();
        ImGui.Text($"{categoryBones.Count} bones in category");
        ImGui.Spacing();

        ImPoser.SectionHeader("Transform");
        ImGui.TextDisabled("Use gizmo to transform category bones");
    }

    private void DrawBoneContent(IBone bone)
    {
        ImPoser.SectionHeader("Transform");

        // Get bone modification
        var modification = _bonePosingService.GetModification(bone);
        var transform = modification ?? new Transform { Position = Vector3.Zero, Rotation = Quaternion.Identity, Scale = Vector3.Zero };

        // Show current bone transform info
        ImGui.Text($"Position: {transform.Position:F3}");
        ImGui.Text($"Rotation: {QuaternionToEuler(transform.Rotation):F1}°");
        if (transform.Scale != Vector3.Zero)
        {
            ImGui.Text($"Scale: {transform.Scale:F3}");
        }

        ImGui.Spacing();
        ImGui.TextDisabled("Use gizmo to transform bones");
    }

    private static Vector3 QuaternionToEuler(Quaternion q)
    {
        // Convert quaternion to euler angles in degrees
        var sinr_cosp = 2 * (q.W * q.X + q.Y * q.Z);
        var cosr_cosp = 1 - 2 * (q.X * q.X + q.Y * q.Y);
        var roll = MathF.Atan2(sinr_cosp, cosr_cosp);

        var sinp = 2 * (q.W * q.Y - q.Z * q.X);
        var pitch = MathF.Abs(sinp) >= 1 ? MathF.CopySign(MathF.PI / 2, sinp) : MathF.Asin(sinp);

        var siny_cosp = 2 * (q.W * q.Z + q.X * q.Y);
        var cosy_cosp = 1 - 2 * (q.Y * q.Y + q.Z * q.Z);
        var yaw = MathF.Atan2(siny_cosp, cosy_cosp);

        return new Vector3(roll, pitch, yaw) * (180f / MathF.PI);
    }

    private void DrawVerticalTabBar()
    {
        float buttonSize = TabBarWidth * ImGuiHelpers.GlobalScale - ImGui.GetStyle().WindowPadding.X;
        var size = new Vector2(buttonSize, buttonSize);

        // Transform tab (move icon)
        bool isTransformActive = _activeTab == PropertiesTab.Transform;
        using (ImRaii.PushColor(ImGuiCol.Button, ImGui.GetColorU32(ImGuiCol.TabActive), isTransformActive))
        using (ImRaii.PushColor(ImGuiCol.ButtonHovered, ImGui.GetColorU32(ImGuiCol.TabHovered)))
        {
            if (ImPoser.CenteredIconButton("tab_transform", FontAwesomeIcon.ArrowsAlt, size, "Transform"))
            {
                _activeTab = PropertiesTab.Transform;
            }
        }

        ImGui.Spacing();

        // Animation tab (walking person icon)
        bool isAnimationActive = _activeTab == PropertiesTab.Animation;
        using (ImRaii.PushColor(ImGuiCol.Button, ImGui.GetColorU32(ImGuiCol.TabActive), isAnimationActive))
        using (ImRaii.PushColor(ImGuiCol.ButtonHovered, ImGui.GetColorU32(ImGuiCol.TabHovered)))
        {
            if (ImPoser.CenteredIconButton("tab_animation", FontAwesomeIcon.Walking, size, "Animation"))
            {
                _activeTab = PropertiesTab.Animation;
            }
        }
    }

    private void DrawTabContent(IActor actor)
    {
        bool isFrozen = _animationService.IsFrozen(actor);

        switch (_activeTab)
        {
            case PropertiesTab.Transform:
                DrawTransformTab(actor, isFrozen);
                break;
            case PropertiesTab.Animation:
                DrawAnimationTab(actor, isFrozen);
                break;
        }
    }

    private void DrawTransformTab(IActor actor, bool isFrozen)
    {
        var transform = _posingService.GetEffectiveTransform(actor);

        if (_transformWidget.Draw("transform", ref transform, !isFrozen))
        {
            _posingService.SetTransformOverride(actor, transform);
        }
    }

    private void DrawAnimationTab(IActor actor, bool isFrozen)
    {
        float labelWidth = LabelWidth * ImGuiHelpers.GlobalScale;

        // Check if this is a companion (pets, mounts, minions can't have animations changed)
        bool isCompanion = actor.IsCompanion;

        // === Animation Section ===
        ImPoser.SectionHeader("Animation");

        if (isCompanion)
        {
            ImGui.TextDisabled("Animation controls not available for companions");
        }
        else
        {
            DrawAnimationSection(actor, labelWidth);
        }

        // === Playback Section ===
        ImPoser.SectionSeparator("Playback");
        DrawSpeedSection(actor, labelWidth);
        ImGui.Spacing();
        DrawScrubSection(actor, isFrozen, labelWidth);

        // === Gaze Section ===
        ImPoser.SectionSeparator("Gaze");
        DrawGazeSection(actor, isFrozen, labelWidth);
    }

    private void DrawSpeedSection(IActor actor, float labelWidth)
    {
        ImPoser.Label("Speed", labelWidth);

        float speed = _animationService.GetSpeed(actor);

        // Start tracking when slider becomes active
        if (ImGui.IsItemActive() && !_isEditingSpeed)
        {
            _controller.BeginSpeedChange(actor);
            _isEditingSpeed = true;
        }

        ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X - 70 * ImGuiHelpers.GlobalScale);
        if (ImGui.SliderFloat("##speed", ref speed, 0f, 3f, "%.2fx"))
        {
            _animationService.SetSpeed(actor, speed);
        }

        // Create history action when slider is released
        if (_isEditingSpeed && ImGui.IsItemDeactivatedAfterEdit())
        {
            _isEditingSpeed = false;
            _controller.EndSpeedChange(actor, speed);
        }

        ImGui.SameLine();

        // Play/Pause toggle button
        bool isPlaying = speed > 0f;
        var playPauseIcon = isPlaying ? FontAwesomeIcon.Pause : FontAwesomeIcon.Play;
        var playPauseTooltip = isPlaying ? "Pause" : "Play";

        // Active state when paused (speed == 0)
        using (ImRaii.PushColor(ImGuiCol.Button, ImGui.GetColorU32(ImGuiCol.ButtonActive), !isPlaying))
        {
            if (ImPoser.CenteredIconButton("play_pause", playPauseIcon, null, playPauseTooltip))
            {
                if (isPlaying)
                    _controller.SetAnimationSpeed(actor, 0f);
                else
                    _controller.SetAnimationSpeed(actor, 1f);
            }
        }

        ImGui.SameLine();

        // Reset speed button
        if (ImPoser.CenteredIconButton("reset_speed", FontAwesomeIcon.Undo, null, "Reset Speed"))
        {
            _controller.SetAnimationSpeed(actor, 1f);
        }
    }

    private void DrawScrubSection(IActor actor, bool isFrozen, float labelWidth)
    {
        float? duration = _animationService.GetAnimationDuration(actor);
        float? currentTime = _animationService.GetAnimationTime(actor);

        ImPoser.Label("Time", labelWidth);

        // Always show slider to maintain consistent height
        float time = currentTime ?? 0f;
        float maxTime = duration ?? 1f;

        // Disable slider when not frozen or no animation
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

    private void DrawAnimationSection(IActor actor, float labelWidth)
    {
        bool hasOverride = _animationService.HasBaseOverride(actor);

        // Base Animation
        ImPoser.Label("Base", labelWidth);

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
            // Animation was selected
        }

        ImGui.SameLine();

        // Stop button
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
        ImPoser.Label("Blend", labelWidth);

        if (_blendAnimationSelector.Draw("blend_anim", null, id =>
        {
            _animationService.PlayBlendAnimation(actor, id);
            // Blend animations are one-shot, no undo needed
        }, selectorWidth))
        {
            // Animation was selected
        }
    }

    private void DrawGazeSection(IActor actor, bool isFrozen, float labelWidth)
    {
        var gazeState = _gazeService.GetGazeState(actor);

        // Gaze mode dropdown
        ImPoser.Label("Mode", labelWidth);
        ImGui.SetNextItemWidth(-1);
        int currentMode = (int)gazeState.Mode;
        if (ImGui.Combo("##gaze_mode", ref currentMode, GazeModeNames, GazeModeNames.Length))
        {
            _controller.SetGazeMode(actor, (GazeTargetMode)currentMode);
        }

        ImGui.Spacing();

        // Target type checkboxes (which body parts)
        ImPoser.Label("Affect", labelWidth);

        bool affectBody = gazeState.TargetType.HasFlag(GazeTargetType.Body);
        if (ImGui.Checkbox("Body", ref affectBody))
        {
            var newType = affectBody
                ? gazeState.TargetType | GazeTargetType.Body
                : gazeState.TargetType & ~GazeTargetType.Body;
            _controller.SetGazeTargetType(actor, newType);
        }

        ImGui.SameLine();
        bool affectHead = gazeState.TargetType.HasFlag(GazeTargetType.Head);
        if (ImGui.Checkbox("Head", ref affectHead))
        {
            var newType = affectHead
                ? gazeState.TargetType | GazeTargetType.Head
                : gazeState.TargetType & ~GazeTargetType.Head;
            _controller.SetGazeTargetType(actor, newType);
        }

        ImGui.SameLine();
        bool affectEyes = gazeState.TargetType.HasFlag(GazeTargetType.Eyes);
        if (ImGui.Checkbox("Eyes", ref affectEyes))
        {
            var newType = affectEyes
                ? gazeState.TargetType | GazeTargetType.Eyes
                : gazeState.TargetType & ~GazeTargetType.Eyes;
            _controller.SetGazeTargetType(actor, newType);
        }

        // Entity target list (only shown in Entity mode)
        if (gazeState.Mode == GazeTargetMode.Entity)
        {
            ImGui.Spacing();
            ImPoser.Label("Target", labelWidth);

            // Show combo with available entities
            var actors = _actorManager.Actors;
            string currentTargetName = gazeState.TargetEntity?.Name ?? "None";

            ImGui.SetNextItemWidth(-1);
            if (ImGui.BeginCombo("##gaze_target", currentTargetName))
            {
                foreach (var targetActor in actors)
                {
                    // Don't allow targeting self
                    if (targetActor == actor)
                        continue;

                    bool isSelected = gazeState.TargetEntity == targetActor;
                    if (ImGui.Selectable(targetActor.Name, isSelected))
                    {
                        _controller.SetGazeTarget(actor, targetActor);
                    }

                    if (isSelected)
                        ImGui.SetItemDefaultFocus();
                }
                ImGui.EndCombo();
            }
        }

        ImGui.Spacing();

        // Reset button
        if (ImGui.Button("Reset Gaze"))
        {
            _controller.ResetGaze(actor);
        }
    }
}
