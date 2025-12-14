using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using Poser.Entities;
using Poser.Services;

namespace Poser.UI.Controls;

/// <summary>
/// Tab pane for camera property editing in the properties panel.
/// </summary>
public class CameraTabPane : ITabPane
{
    private readonly IVirtualCameraService? _cameraService;

    // Current entity context (set before Draw)
    private IEntity? _entity;

    // Default values for reset
    private const float DefaultDistance = 6f;
    private const float DefaultFoV = 0.78f;
    private const float DefaultRoll = 0f;
    private static readonly Vector2 DefaultPan = Vector2.Zero;

    public string Name => "Camera";
    public FontAwesomeIcon? Icon => FontAwesomeIcon.Camera;

    public CameraTabPane(IVirtualCameraService? cameraService = null)
    {
        _cameraService = cameraService;
    }

    /// <summary>
    /// Sets the entity to display/edit. Call before Draw().
    /// </summary>
    public void SetEntity(IEntity? entity)
    {
        _entity = entity;
    }

    /// <summary>
    /// Whether this tab is enabled for the current entity.
    /// </summary>
    public bool IsEnabled => _entity is VirtualCameraEntity;

    public void Draw()
    {
        if (_entity is not VirtualCameraEntity camera)
        {
            ImGui.TextDisabled("Select a camera to edit properties");
            return;
        }

        DrawCameraControls(camera);
    }

    private void DrawCameraControls(VirtualCameraEntity camera)
    {
        // Active indicator
        using (var row = PoserUI.Row(PoserUI.FrameHeight))
        {
            row.Label("Active", 80);
            if (camera.IsActive)
            {
                row.Text("Yes");
            }
            else
            {
                if (row.Button("##camera_select", "Select"))
                {
                    _cameraService?.SelectCamera(camera);
                }
            }
        }

        DrawSectionHeader("View", isFirst: true);

        // Distance (zoom) with input and reset
        {
            float maxDist = camera.DelimitCamera ? 500f : 20f;
            var distance = camera.Distance;
            bool changed = DrawValueRow("Distance", "##camera_distance", ref distance, 0f, maxDist, DefaultDistance);
            if (changed)
            {
                camera.Distance = distance;
                if (camera.IsActive) _cameraService?.ApplyToGame(camera);
            }
        }

        // Field of View with input and reset
        {
            var fov = camera.FoV * (180f / MathF.PI);
            float defaultFovDeg = DefaultFoV * (180f / MathF.PI);
            bool changed = DrawValueRow("FoV", "##camera_fov", ref fov, 20f, 120f, defaultFovDeg, "°");
            if (changed)
            {
                camera.FoV = fov * (MathF.PI / 180f);
                if (camera.IsActive) _cameraService?.ApplyToGame(camera);
            }
        }

        // Roll with input and reset
        {
            var roll = camera.Roll * (180f / MathF.PI);
            float defaultRollDeg = DefaultRoll * (180f / MathF.PI);
            bool changed = DrawValueRow("Roll", "##camera_roll", ref roll, -180f, 180f, defaultRollDeg, "°");
            if (changed)
            {
                camera.Roll = roll * (MathF.PI / 180f);
                if (camera.IsActive) _cameraService?.ApplyToGame(camera);
            }
        }

        DrawSectionHeader("Pan");

        // Pan X with input and reset
        {
            var panX = camera.Pan.X;
            bool changed = DrawValueRow("Horizontal", "##camera_pan_x", ref panX, -1f, 1f, DefaultPan.X);
            if (changed)
            {
                camera.Pan = new Vector2(panX, camera.Pan.Y);
                if (camera.IsActive) _cameraService?.ApplyToGame(camera);
            }
        }

        // Pan Y with input and reset
        {
            var panY = camera.Pan.Y;
            bool changed = DrawValueRow("Vertical", "##camera_pan_y", ref panY, -1f, 1f, DefaultPan.Y);
            if (changed)
            {
                camera.Pan = new Vector2(camera.Pan.X, panY);
                if (camera.IsActive) _cameraService?.ApplyToGame(camera);
            }
        }

        DrawSectionHeader("Options");

        // Delimit Camera (extended zoom)
        using (var row = PoserUI.Row(PoserUI.FrameHeight))
        {
            row.Label("Extended Zoom", 80);
            var delimit = camera.DelimitCamera;
            if (row.Checkbox("##camera_delimit", ref delimit))
            {
                camera.DelimitCamera = delimit;
                if (camera.IsActive) _cameraService?.ApplyToGame(camera);
            }
        }

        // Disable Collision
        using (var row = PoserUI.Row(PoserUI.FrameHeight))
        {
            row.Label("No Collision", 80);
            var noCollision = camera.DisableCollision;
            if (row.Checkbox("##camera_collision", ref noCollision))
            {
                camera.DisableCollision = noCollision;
            }
        }

        DrawSectionHeader("Actions");

        // Actions on same row: Sync from Game | Reset All
        using (var row = PoserUI.Row(PoserUI.FrameHeight))
        {
            if (row.Button("##camera_capture", "Sync View"))
            {
                _cameraService?.CaptureFromGame(camera);
            }
            row.Spacer(8);
            if (row.Button("##camera_reset", "Reset All"))
            {
                camera.Distance = DefaultDistance;
                camera.FoV = DefaultFoV;
                camera.Roll = DefaultRoll;
                camera.Pan = DefaultPan;
                camera.DelimitCamera = false;
                camera.DisableCollision = false;
                if (camera.IsActive) _cameraService?.ApplyToGame(camera);
            }
        }
    }

    /// <summary>
    /// Draws a value row with label, scrubber, small input, and reset button.
    /// Layout: Label | Scrubber (fill) | Input (fixed) | Reset (fixed)
    /// </summary>
    private static bool DrawValueRow(string label, string id, ref float value, float min, float max, float defaultValue, string suffix = "")
    {
        bool changed = false;
        float scale = ImGuiHelpers.GlobalScale;

        // Local copies for lambdas (can't capture ref)
        float localValue = value;
        bool scrubberChanged = false;
        bool inputChanged = false;
        bool resetClicked = false;

        using (var row = Flex.Row(Flex.RowHeight, Flex.SmallGap))
        {
            // Label (fixed width)
            row.Label(label, 80);

            // Scrubber (fills remaining space, value hidden since we have separate input)
            row.Fill((w, h) =>
            {
                if (Scrubber.Draw(id, ref localValue, min, max, 0f, w, hideValue: true))
                {
                    scrubberChanged = true;
                }
            });

            // Small input field (fixed width)
            row.Fixed(50, (w, h) =>
            {
                float offsetY = (h - ImGui.GetFrameHeight()) / 2f;
                if (offsetY > 0) ImGui.SetCursorPosY(ImGui.GetCursorPosY() + offsetY);

                ImGui.PushStyleColor(ImGuiCol.FrameBg, UIColors.ControlBackground);
                ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(4f * scale, 2f * scale));
                ImGui.SetNextItemWidth(w);

                string format = suffix.Length > 0 ? $"%.1f{suffix}" : "%.2f";
                if (ImGui.InputFloat($"{id}_input", ref localValue, 0f, 0f, format, ImGuiInputTextFlags.EnterReturnsTrue))
                {
                    inputChanged = true;
                }
                if (ImGui.IsItemDeactivatedAfterEdit())
                {
                    inputChanged = true;
                }

                ImGui.PopStyleVar();
                ImGui.PopStyleColor();
            });

            // Reset button (fixed width)
            row.Fixed(Flex.RowHeight, (w, h) =>
            {
                if (PoserButton.DrawIcon($"{id}_reset", FontAwesomeIcon.Undo, "Reset to default"))
                {
                    resetClicked = true;
                }
            });
        }

        // Apply changes from lambdas
        if (scrubberChanged || inputChanged)
        {
            value = Math.Clamp(localValue, min, max);
            changed = true;
        }
        if (resetClicked)
        {
            value = defaultValue;
            changed = true;
        }

        return changed;
    }

    private static void DrawSectionHeader(string text, bool isFirst = false)
    {
        if (!isFirst)
            PoserUI.Separator();

        using (var row = Flex.Row())
        {
            row.Fill((w, h) =>
            {
                float offsetY = (h - ImGui.GetTextLineHeight()) / 2f;
                if (offsetY > 0) ImGui.SetCursorPosY(ImGui.GetCursorPosY() + offsetY);
                ImGui.TextColored(UIColors.TextDisabled, text);
            });
        }
    }
}
