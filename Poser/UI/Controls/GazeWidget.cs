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
/// Uses 40% opacity for disabled state instead of ImGui disabled.
/// </summary>
public class GazeWidget
{
    private const float LabelWidth = 50f;
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
        float opacity = enabled ? 1f : UIColors.DisabledOpacity;

        // Enable row with checkbox and reset button
        using (PushOpacity(opacity))
        {
            using var row = PoserUI.Row(PoserUI.FrameHeight);
            row.Label("Enable:", LabelWidth);
            if (row.Checkbox("##enable_gaze", ref gazeEnabled))
            {
                if (enabled && actor != null)
                {
                    if (gazeEnabled)
                        _gazeService.EnableGaze(actor);
                    else
                        _gazeService.DisableGaze(actor);
                }
            }
            row.Spacer(8);
            if (row.Button("reset_gaze", "Reset"))
            {
                if (enabled && actor != null)
                    _gazeService.ResetGaze(actor);
            }
        }

        // Calculate opacities (don't nest - use absolute values)
        float controlsOpacity = (enabled && gazeEnabled) ? 1f : UIColors.DisabledOpacity;
        float targetOpacity = (enabled && gazeEnabled && gazeState?.Mode == GazeTargetMode.Entity) ? 1f : UIColors.DisabledOpacity;

        // Mode and Target on same row
        using (PushOpacity(controlsOpacity))
        {
            using var row = PoserUI.Row(PoserUI.DropdownHeight);
            row.Label("Mode:", LabelWidth);

            int modeIndex = (int)(gazeState?.Mode ?? GazeTargetMode.None);
            if (row.Dropdown("##gaze_mode", ref modeIndex, GazeModeNames, 100))
            {
                if (enabled && gazeEnabled && actor != null)
                    _gazeService.SetGazeMode(actor, (GazeTargetMode)modeIndex);
            }

            row.Spacer(8);

            // Target with separate opacity
            var actors = _actorManager.Actors.Where(a => a != actor).ToList();
            var actorNames = actors.Select(a => ConfigurationService.Instance.GetDisplayName(a)).ToArray();
            var currentTarget = gazeState?.TargetEntity;
            int targetIndex = currentTarget != null ? actors.IndexOf(currentTarget) : -1;

            row.Custom(ImGui.CalcTextSize("Target:").X / PoserUI.Scale + 8, () =>
            {
                // Safe relative opacity (avoid division by very small numbers)
                float relativeOpacity = controlsOpacity > 0.01f ? targetOpacity / controlsOpacity : targetOpacity;
                using (PushOpacity(relativeOpacity))
                    ImGui.Text("Target:");
            });

            // Safe relative opacity (avoid division by very small numbers)
            float targetRelativeOpacity = controlsOpacity > 0.01f ? targetOpacity / controlsOpacity : targetOpacity;
            using (PushOpacity(targetRelativeOpacity))
            {
                if (row.DropdownFill("##gaze_target", ref targetIndex, actorNames.Length > 0 ? actorNames : new[] { "No targets" }))
                {
                    if (enabled && gazeEnabled && actor != null && gazeState?.Mode == GazeTargetMode.Entity && targetIndex >= 0 && targetIndex < actors.Count)
                    {
                        _gazeService.SetGazeTarget(actor, actors[targetIndex]);
                    }
                }
            }
        }

        // Track row with checkboxes and lock toggle buttons - evenly split
        float rowHeight = PoserToggleButton.Size;
        using (PushOpacity(controlsOpacity))
        {
            var targetType = gazeState?.TargetType ?? GazeTargetType.None;
            bool trackEyes = targetType.HasFlag(GazeTargetType.Eyes);
            bool trackHead = targetType.HasFlag(GazeTargetType.Head);
            bool trackBody = targetType.HasFlag(GazeTargetType.Body);

            bool lockEyes = actor != null && _gazeService.IsPartLocked(actor, GazeTargetType.Eyes);
            bool lockHead = actor != null && _gazeService.IsPartLocked(actor, GazeTargetType.Head);
            bool lockBody = actor != null && _gazeService.IsPartLocked(actor, GazeTargetType.Body);

            using var row = PoserUI.Row(rowHeight);
            row.Label("Track:", LabelWidth);

            row.CustomFill(availWidth =>
            {
                float itemWidth = availWidth / 3f;
                float checkboxSize = PoserCheckbox.Size;
                float toggleSize = PoserToggleButton.Size;
                float textHeight = ImGui.GetTextLineHeight();
                float spacing = 4f * PoserUI.Scale;

                var startPos = ImGui.GetCursorPos();
                float startX = startPos.X;
                float startY = startPos.Y;

                // Vertical centering offsets
                float textY = startY + (rowHeight - textHeight) / 2f;
                float checkboxY = startY + (rowHeight - checkboxSize) / 2f;
                float toggleY = startY + (rowHeight - toggleSize) / 2f;

                // Eyes group
                float curX = startX;
                ImGui.SetCursorPos(new Vector2(curX, textY));
                ImGui.Text("Eyes");
                curX += ImGui.CalcTextSize("Eyes").X + spacing;

                ImGui.SetCursorPos(new Vector2(curX, checkboxY));
                if (PoserCheckbox.Draw("##track_eyes", ref trackEyes) && enabled && gazeEnabled && actor != null)
                {
                    var newType = trackEyes ? targetType | GazeTargetType.Eyes : targetType & ~GazeTargetType.Eyes;
                    _gazeService.SetGazeTargetType(actor, newType);
                }
                curX += checkboxSize + spacing;

                ImGui.SetCursorPos(new Vector2(curX, toggleY));
                if (PoserToggleButton.Draw("##lock_eyes", ref lockEyes, FontAwesomeIcon.LockOpen, FontAwesomeIcon.Lock, lockEyes ? "Unlock Eyes" : "Lock Eyes"))
                {
                    if (enabled && gazeEnabled && actor != null)
                    {
                        var cameraPos = _cameraService.GetCameraPosition();
                        _gazeService.SetTargetLock(actor, lockEyes, GazeTargetType.Eyes, cameraPos);
                    }
                }

                // Head group
                curX = startX + itemWidth;
                ImGui.SetCursorPos(new Vector2(curX, textY));
                ImGui.Text("Head");
                curX += ImGui.CalcTextSize("Head").X + spacing;

                ImGui.SetCursorPos(new Vector2(curX, checkboxY));
                if (PoserCheckbox.Draw("##track_head", ref trackHead) && enabled && gazeEnabled && actor != null)
                {
                    var newType = trackHead ? targetType | GazeTargetType.Head : targetType & ~GazeTargetType.Head;
                    _gazeService.SetGazeTargetType(actor, newType);
                }
                curX += checkboxSize + spacing;

                ImGui.SetCursorPos(new Vector2(curX, toggleY));
                if (PoserToggleButton.Draw("##lock_head", ref lockHead, FontAwesomeIcon.LockOpen, FontAwesomeIcon.Lock, lockHead ? "Unlock Head" : "Lock Head"))
                {
                    if (enabled && gazeEnabled && actor != null)
                    {
                        var cameraPos = _cameraService.GetCameraPosition();
                        _gazeService.SetTargetLock(actor, lockHead, GazeTargetType.Head, cameraPos);
                    }
                }

                // Body group
                curX = startX + itemWidth * 2;
                ImGui.SetCursorPos(new Vector2(curX, textY));
                ImGui.Text("Body");
                curX += ImGui.CalcTextSize("Body").X + spacing;

                ImGui.SetCursorPos(new Vector2(curX, checkboxY));
                if (PoserCheckbox.Draw("##track_body", ref trackBody) && enabled && gazeEnabled && actor != null)
                {
                    var newType = trackBody ? targetType | GazeTargetType.Body : targetType & ~GazeTargetType.Body;
                    _gazeService.SetGazeTargetType(actor, newType);
                }
                curX += checkboxSize + spacing;

                ImGui.SetCursorPos(new Vector2(curX, toggleY));
                if (PoserToggleButton.Draw("##lock_body", ref lockBody, FontAwesomeIcon.LockOpen, FontAwesomeIcon.Lock, lockBody ? "Unlock Body" : "Lock Body"))
                {
                    if (enabled && gazeEnabled && actor != null)
                    {
                        var cameraPos = _cameraService.GetCameraPosition();
                        _gazeService.SetTargetLock(actor, lockBody, GazeTargetType.Body, cameraPos);
                    }
                }
            });
        }
    }

    /// <summary>
    /// Pushes ImGui style alpha for opacity-based disabled state.
    /// </summary>
    private static ImRaii.Style PushOpacity(float opacity)
    {
        return ImRaii.PushStyle(ImGuiStyleVar.Alpha, ImGui.GetStyle().Alpha * opacity);
    }
}
