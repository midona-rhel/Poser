using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
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

    private static readonly string[] GazeModeNames = { "None", "Forward", "Camera", "Entity" };

    public PropertiesPanel(
        ISelectionService selectionService,
        IActorManager actorManager,
        IPosingService posingService,
        IBonePosingService bonePosingService,
        IAnimationService animationService,
        IAnimationDataService animationDataService,
        IHistoryService historyService,
        IGazeService gazeService)
    {
        _selectionService = selectionService;
        _actorManager = actorManager;
        _posingService = posingService;
        _bonePosingService = bonePosingService;
        _animationService = animationService;
        _historyService = historyService;
        _gazeService = gazeService;
        _transformWidget = new TransformWidget();

        _baseAnimationSelector = new AnimationSelector(animationDataService);
        _blendAnimationSelector = new AnimationSelector(animationDataService);

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
        // TODO: Add TransformBoneAction for bones
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

        // Build list of available tabs based on capabilities
        var tabs = GetAvailableTabs(entity);

        if (tabs.Count == 0)
        {
            ImGui.TextDisabled($"Entity type: {entity.EntityType}");
            return;
        }

        // Clamp active tab index
        if (_activeTabIndex >= tabs.Count)
            _activeTabIndex = 0;

        float tabBarWidth = TabBarWidth * ImGuiHelpers.GlobalScale;
        float availHeight = ImGui.GetContentRegionAvail().Y;

        // Only show tab bar if multiple tabs
        if (tabs.Count > 1)
        {
            using (var tabChild = ImRaii.Child("tab_bar", new Vector2(tabBarWidth, availHeight), false))
            {
                if (tabChild.Success)
                {
                    DrawTabBar(tabs);
                }
            }
            ImGui.SameLine();
        }

        // Draw content
        using (var contentChild = ImRaii.Child("tab_content", new Vector2(-1, availHeight), false))
        {
            if (contentChild.Success)
            {
                tabs[_activeTabIndex].Draw(entity);
            }
        }
    }

    private List<EntityTab> GetAvailableTabs(IEntity entity)
    {
        var tabs = new List<EntityTab>();

        // Transform tab - for ITransformable
        if (entity is ITransformable)
        {
            tabs.Add(new EntityTab("Transform", FontAwesomeIcon.ArrowsAlt, DrawTransformTab));
        }

        // Animation tab - for IAnimatable
        if (entity is IAnimatable animatable && animatable.CanControlAnimation)
        {
            tabs.Add(new EntityTab("Animation", FontAwesomeIcon.Walking, DrawAnimationTab));
        }

        return tabs;
    }

    private void DrawTabBar(List<EntityTab> tabs)
    {
        float buttonSize = TabBarWidth * ImGuiHelpers.GlobalScale - ImGui.GetStyle().WindowPadding.X;
        var size = new Vector2(buttonSize, buttonSize);

        for (int i = 0; i < tabs.Count; i++)
        {
            var tab = tabs[i];
            bool isActive = _activeTabIndex == i;

            using (ImRaii.PushColor(ImGuiCol.Button, ImGui.GetColorU32(ImGuiCol.TabActive), isActive))
            using (ImRaii.PushColor(ImGuiCol.ButtonHovered, ImGui.GetColorU32(ImGuiCol.TabHovered)))
            {
                if (ImPoser.CenteredIconButton($"tab_{i}", tab.Icon, size, tab.Name))
                {
                    _activeTabIndex = i;
                }
            }

            if (i < tabs.Count - 1)
                ImGui.Spacing();
        }
    }

    #region Transform Tab

    private void DrawTransformTab(IEntity entity)
    {
        if (entity is IActor actor)
        {
            DrawActorTransform(actor);
        }
        else if (entity is IBone bone)
        {
            DrawBoneTransform(bone);
        }
        else if (entity is ITransformable transformable)
        {
            DrawGenericTransform(transformable, entity);
        }
    }

    private void DrawActorTransform(IActor actor)
    {
        bool isFrozen = _animationService.IsFrozen(actor);
        var transform = _posingService.GetEffectiveTransform(actor);

        if (_transformWidget.Draw("transform", ref transform, !isFrozen))
        {
            _posingService.SetTransformOverride(actor, transform);
        }
    }

    private void DrawBoneTransform(IBone bone)
    {
        var transform = bone.Transform;

        ImGui.TextDisabled("Transform");
        ImGui.Spacing();
        ImGui.Text($"Position: {transform.Position.X:F3}, {transform.Position.Y:F3}, {transform.Position.Z:F3}");

        var euler = QuaternionToEuler(transform.Rotation);
        ImGui.Text($"Rotation: {euler.X:F1}, {euler.Y:F1}, {euler.Z:F1}");

        if (transform.Scale != Vector3.One)
        {
            ImGui.Text($"Scale: {transform.Scale.X:F3}, {transform.Scale.Y:F3}, {transform.Scale.Z:F3}");
        }

        ImGui.Spacing();
        ImGui.TextDisabled("Use gizmo to transform bones");
    }

    private void DrawGenericTransform(ITransformable transformable, IEntity entity)
    {
        var transform = entity.Transform;

        ImGui.TextDisabled("Transform");
        ImGui.Spacing();
        ImGui.Text($"Position: {transform.Position.X:F3}, {transform.Position.Y:F3}, {transform.Position.Z:F3}");

        var euler = QuaternionToEuler(transform.Rotation);
        ImGui.Text($"Rotation: {euler.X:F1}, {euler.Y:F1}, {euler.Z:F1}");
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
        var gazeState = _gazeService.GetGazeState(actor);
        var oldState = gazeState.Clone();

        // Mode dropdown
        ImGui.Text("Mode");
        ImGui.SameLine(labelWidth);
        ImGui.SetNextItemWidth(-1);
        int currentMode = (int)gazeState.Mode;
        if (ImGui.Combo("##gaze_mode", ref currentMode, GazeModeNames, GazeModeNames.Length))
        {
            var newState = gazeState.Clone();
            newState.Mode = (GazeTargetMode)currentMode;
            _gazeService.SetGazeState(actor, newState);
            RecordGazeChange(actor, oldState, newState);
        }

        ImGui.Spacing();

        // Target type checkboxes
        ImGui.Text("Affect");
        ImGui.SameLine(labelWidth);

        DrawGazeCheckbox("Body", GazeTargetType.Body, actor, gazeState, oldState);
        ImGui.SameLine();
        DrawGazeCheckbox("Head", GazeTargetType.Head, actor, gazeState, oldState);
        ImGui.SameLine();
        DrawGazeCheckbox("Eyes", GazeTargetType.Eyes, actor, gazeState, oldState);

        // Entity target (Entity mode only)
        if (gazeState.Mode == GazeTargetMode.Entity)
        {
            ImGui.Spacing();
            ImGui.Text("Target");
            ImGui.SameLine(labelWidth);

            string currentTargetName = gazeState.TargetEntity?.Name ?? "None";
            ImGui.SetNextItemWidth(-1);
            if (ImGui.BeginCombo("##gaze_target", currentTargetName))
            {
                foreach (var targetActor in _actorManager.Actors)
                {
                    if (targetActor == actor) continue;

                    bool isSelected = gazeState.TargetEntity == targetActor;
                    if (ImGui.Selectable(targetActor.Name, isSelected))
                    {
                        var newState = gazeState.Clone();
                        newState.TargetEntity = targetActor;
                        _gazeService.SetGazeState(actor, newState);
                        RecordGazeChange(actor, oldState, newState);
                    }
                    if (isSelected) ImGui.SetItemDefaultFocus();
                }
                ImGui.EndCombo();
            }
        }

        ImGui.Spacing();

        if (ImGui.Button("Reset Gaze"))
        {
            var newState = new GazeState();
            _gazeService.SetGazeState(actor, newState);
            RecordGazeChange(actor, oldState, newState);
        }
    }

    private void DrawGazeCheckbox(string label, GazeTargetType type, IActor actor, GazeState gazeState, GazeState oldState)
    {
        bool isSet = gazeState.TargetType.HasFlag(type);
        if (ImGui.Checkbox(label, ref isSet))
        {
            var newType = isSet
                ? gazeState.TargetType | type
                : gazeState.TargetType & ~type;
            var newState = gazeState.Clone();
            newState.TargetType = newType;
            _gazeService.SetGazeState(actor, newState);
            RecordGazeChange(actor, oldState, newState);
        }
    }

    private void RecordGazeChange(IActor actor, GazeState oldState, GazeState newState)
    {
        var action = new GazeHistoryAction(_gazeService, actor, oldState, newState);
        _historyService.Record(action);
    }

    #endregion

    #region Helpers

    private static Vector3 QuaternionToEuler(Quaternion q)
    {
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
