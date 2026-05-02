using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using Poser.Config;
using Poser.Entities;
using Poser.Entities.Capabilities;
using Poser.Services;

namespace Poser.UI.Controls;

/// <summary>
/// Widget for controlling gaze settings (eye/head/body tracking and locking).
/// </summary>
public class GazeWidget
{
    private static readonly string[] GazeModeNames = { "None", "Forward", "Camera", "Entity" };

    private readonly IGazeService _gazeService;
    private readonly IActorManager _actorManager;
    private readonly ICameraService _cameraService;

    public GazeWidget(IGazeService gazeService, IActorManager actorManager, ICameraService cameraService)
    {
        _gazeService = gazeService;
        _actorManager = actorManager;
        _cameraService = cameraService;
    }

    /// <summary>
    /// Draws the gaze widget for an actor.
    /// </summary>
    /// <param name="actor">The actor to control gaze for (null renders disabled dummy UI).</param>
    public void Draw(IActor? actor)
    {
        bool enabled = actor != null;
        var gazeState = actor != null ? _gazeService.GetGazeState(actor) : default;
        bool gazeEnabled = actor != null && _gazeService.IsGazeEnabled(actor);

        // Enable row with checkbox
        using (ImRaii.Disabled(!enabled))
        {
            using var row = Flex.Row(gap: Flex.ItemGap);
            row.Label("Enable:");
            row.Fixed(Crystarium.CheckboxSize / PoserUI.Scale, (w, h) =>
            {
                float offsetY = (h - Crystarium.CheckboxSize) / 2f;
                if (offsetY > 0) ImGui.SetCursorPosY(ImGui.GetCursorPosY() + offsetY);
                if (Crystarium.Checkbox("##enable_gaze", ref gazeEnabled))
                {
                    if (actor != null)
                    {
                        if (gazeEnabled)
                            _gazeService.EnableGaze(actor);
                        else
                            _gazeService.DisableGaze(actor);
                    }
                }
            });
        }

        // Determine disabled states
        bool controlsDisabled = !enabled || !gazeEnabled;
        bool targetDisabled = controlsDisabled || gazeState?.Mode != GazeTargetMode.Entity;

        // Mode and Target on same row
        using (ImRaii.Disabled(controlsDisabled))
        {
            using var row = Flex.Row(gap: Flex.ItemGap);
            row.Label("Mode:");

            int modeIndex = (int)(gazeState?.Mode ?? GazeTargetMode.None);
            row.Fixed(100, (w, h) =>
            {
                if (Crystarium.Dropdown("##gaze_mode", GazeModeNames, ref modeIndex, new DropdownProps { Style = new DropdownStyle { Width = Sizing.Fixed(w / PoserUI.Scale) } }))
                {
                    if (actor != null)
                        _gazeService.SetGazeMode(actor, (GazeTargetMode)modeIndex);
                }
            });

            // Target with separate disabled state
            var actors = _actorManager.Actors.Where(a => a != actor).ToList();
            var actorNames = actors.Select(a => ConfigurationService.Instance.GetDisplayName(a)).ToArray();
            var currentTarget = gazeState?.TargetEntity;
            int targetIndex = currentTarget != null ? actors.IndexOf(currentTarget) : -1;

            using (ImRaii.Disabled(targetDisabled && !controlsDisabled))
            {
                row.Label("Target:", ImGui.CalcTextSize("Target:").X / PoserUI.Scale + 8);

                row.Fill((w, h) =>
                {
                    if (Crystarium.Dropdown("##gaze_target", actorNames.Length > 0 ? actorNames : new[] { "No targets" }, ref targetIndex, new DropdownProps { Style = new DropdownStyle { Width = Sizing.Fixed(w / PoserUI.Scale) } }))
                    {
                        if (actor != null && gazeState?.Mode == GazeTargetMode.Entity && targetIndex >= 0 && targetIndex < actors.Count)
                        {
                            _gazeService.SetGazeTarget(actor, actors[targetIndex]);
                        }
                    }
                });
            }
        }

        // Track row with checkboxes and lock toggle buttons - evenly split into 3
        using (ImRaii.Disabled(controlsDisabled))
        {
            var targetType = gazeState?.TargetType ?? GazeTargetType.None;

            using var row = Flex.Row(gap: Flex.ItemGap);
            row.Label("Track:");
            row.Flex(1, (w, h) => DrawTrackGroup(w, h, "Eyes", actor, targetType, GazeTargetType.Eyes));
            row.Flex(1, (w, h) => DrawTrackGroup(w, h, "Head", actor, targetType, GazeTargetType.Head));
            row.Flex(1, (w, h) => DrawTrackGroup(w, h, "Body", actor, targetType, GazeTargetType.Body));
        }

        // Reset button row - bottom right
        using (ImRaii.Disabled(!enabled))
        {
            using var row = Flex.Row(gap: Flex.ItemGap);
            row.Spacer();
            row.Fixed(Flex.ButtonWidth, (w, h) =>
            {
                if (Crystarium.Button("Reset", new ButtonProps { Id = "reset_gaze", Style = new ButtonStyle { Width = Sizing.Fixed(w / PoserUI.Scale) } }))
                {
                    if (actor != null)
                        _gazeService.ResetGaze(actor);
                }
            });
        }
    }

    private void DrawTrackGroup(float width, float height, string label, IActor? actor, GazeTargetType currentType, GazeTargetType partType)
    {
        bool isTracked = currentType.HasFlag(partType);
        bool isLocked = actor != null && _gazeService.IsPartLocked(actor, partType);

        float checkboxSize = Crystarium.CheckboxSize / PoserUI.Scale;
        float iconToggleSize = Crystarium.IconToggleSize / PoserUI.Scale;
        string labelText = label + ":";

        // Layout: Label (centered in remaining space) | Checkbox | IconToggle (right-aligned)
        using var group = Flex.Row(Flex.RowHeight, gap: Flex.SmallGap, width: width);

        // Label centered in remaining space
        group.Fill((w, h) =>
        {
            float offsetY = (h - ImGui.GetTextLineHeight()) / 2f;
            if (offsetY > 0) ImGui.SetCursorPosY(ImGui.GetCursorPosY() + offsetY);

            // Center text within the fill area
            float textWidth = ImGui.CalcTextSize(labelText).X;
            float offsetX = (w - textWidth) / 2f;
            if (offsetX > 0) ImGui.SetCursorPosX(ImGui.GetCursorPosX() + offsetX);
            ImGui.Text(labelText);
        });

        // Checkbox - disabled when locked
        group.Fixed(checkboxSize, (w, h) =>
        {
            float offsetY = (h - Crystarium.CheckboxSize) / 2f;
            if (offsetY > 0) ImGui.SetCursorPosY(ImGui.GetCursorPosY() + offsetY);

            using (ImRaii.Disabled(isLocked))
            {
                if (Crystarium.Checkbox($"##track_{label}", ref isTracked) && actor != null)
                {
                    var newType = isTracked ? currentType | partType : currentType & ~partType;
                    _gazeService.SetGazeTargetType(actor, newType);
                }
            }
        });

        // Lock icon toggle
        group.Fixed(iconToggleSize, (w, h) =>
        {
            float offsetY = (h - Crystarium.IconToggleSize) / 2f;
            if (offsetY > 0) ImGui.SetCursorPosY(ImGui.GetCursorPosY() + offsetY);

            if (Crystarium.IconToggle($"##lock_{label}", ref isLocked, FontAwesomeIcon.Lock, isLocked ? $"Unlock {label}" : $"Lock {label}"))
            {
                if (actor != null)
                {
                    var cameraPos = _cameraService.GetCameraPosition();
                    _gazeService.SetTargetLock(actor, isLocked, partType, cameraPos);
                }
            }
        });
    }
}
