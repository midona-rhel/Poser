using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using Poser.Entities;
using Poser.Game.Structs;

namespace Poser.UI.Controls;

/// <summary>
/// Tab pane for light property editing in the properties panel.
/// </summary>
public class LightTabPane : ITabPane
{
    private static readonly string[] FalloffTypeNames = { "Linear", "Quadratic", "Cubic" };

    // Current entity context (set before Draw)
    private IEntity? _entity;

    public string Name => "Light";
    public FontAwesomeIcon? Icon => FontAwesomeIcon.Lightbulb;

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
    public bool IsEnabled => _entity is LightEntity;

    public void Draw()
    {
        if (_entity is not LightEntity light)
        {
            ImGui.TextDisabled("Select a light to edit properties");
            return;
        }

        if (!light.IsValidLight)
        {
            ImGui.TextDisabled("Light is no longer valid");
            return;
        }

        DrawLightControls(light);
    }

    private void DrawLightControls(LightEntity light)
    {
        // Light On/Off toggle
        using (var row = PoserUI.Row(PoserUI.FrameHeight))
        {
            row.Label("Enabled", 80);
            var isOn = light.IsLightOn;
            if (row.Checkbox("##light_on", ref isOn))
            {
                light.IsLightOn = isOn;
            }
        }

        // Light type (read-only)
        using (var row = PoserUI.Row(PoserUI.FrameHeight))
        {
            row.Label("Type", 80);
            row.Text(GetLightTypeName(light.LightType));
        }

        PoserUI.Separator();

        // Color (convert Vector3 RGB to Vector4 for color picker)
        using (var row = PoserUI.Row(PoserUI.FrameHeight))
        {
            row.Label("Color", 80);
            var color = new Vector4(light.Color / 20f, 1f); // Normalize from HDR range
            if (row.ColorEdit("##light_color", ref color, ImGuiColorEditFlags.NoAlpha))
            {
                light.Color = new Vector3(color.X, color.Y, color.Z) * 20f;
            }
        }

        // Intensity
        using (var row = PoserUI.Row(PoserUI.ScrubberHeight))
        {
            row.Label("Intensity", 80);
            var intensity = light.Intensity;
            if (row.Scrubber("##light_intensity", ref intensity, 0f, 10f))
            {
                light.Intensity = intensity;
            }
        }

        // Range
        using (var row = PoserUI.Row(PoserUI.ScrubberHeight))
        {
            row.Label("Range", 80);
            var range = light.Range;
            if (row.Scrubber("##light_range", ref range, 1f, 200f))
            {
                light.Range = range;
            }
        }

        PoserUI.Separator();

        // Falloff Type
        using (var row = PoserUI.Row(PoserUI.DropdownHeight))
        {
            row.Label("Falloff Type", 80);
            var falloffIndex = (int)light.FalloffType;
            if (row.DropdownFill("##light_falloff_type", ref falloffIndex, FalloffTypeNames))
            {
                light.FalloffType = (FalloffType)falloffIndex;
            }
        }

        // Falloff
        using (var row = PoserUI.Row(PoserUI.ScrubberHeight))
        {
            row.Label("Falloff", 80);
            var falloff = light.Falloff;
            if (row.Scrubber("##light_falloff", ref falloff, 0f, 10f))
            {
                light.Falloff = falloff;
            }
        }

        // Spot light specific controls
        if (light.LightType == LightType.SpotLight)
        {
            PoserUI.Separator();

            // Spot Angle
            using (var row = PoserUI.Row(PoserUI.ScrubberHeight))
            {
                row.Label("Spot Angle", 80);
                var spotAngle = light.SpotAngle;
                if (row.Scrubber("##light_spot_angle", ref spotAngle, 1f, 180f))
                {
                    light.SpotAngle = spotAngle;
                }
            }

            // Falloff Angle
            using (var row = PoserUI.Row(PoserUI.ScrubberHeight))
            {
                row.Label("Edge Falloff", 80);
                var falloffAngle = light.FalloffAngle;
                if (row.Scrubber("##light_falloff_angle", ref falloffAngle, 0f, 1f))
                {
                    light.FalloffAngle = falloffAngle;
                }
            }
        }

        PoserUI.Separator();

        // Reflection
        using (var row = PoserUI.Row(PoserUI.FrameHeight))
        {
            row.Label("Reflection", 80);
            var hasReflection = light.HasReflection;
            if (row.Checkbox("##light_reflection", ref hasReflection))
            {
                light.HasReflection = hasReflection;
            }
        }

        // Character Shadow
        using (var row = PoserUI.Row(PoserUI.FrameHeight))
        {
            row.Label("Char Shadow", 80);
            var castsShadow = light.CastsCharacterShadow;
            if (row.Checkbox("##light_shadow", ref castsShadow))
            {
                light.CastsCharacterShadow = castsShadow;
            }
        }

        // Shadow Range (only if shadows enabled)
        if (light.CastsCharacterShadow)
        {
            using (var row = PoserUI.Row(PoserUI.ScrubberHeight))
            {
                row.Label("Shadow Range", 80);
                var shadowRange = light.CharacterShadowRange;
                if (row.Scrubber("##light_shadow_range", ref shadowRange, 1f, 200f))
                {
                    light.CharacterShadowRange = shadowRange;
                }
            }
        }
    }

    private static string GetLightTypeName(LightType type) => type switch
    {
        LightType.SpotLight => "Spot Light",
        LightType.AreaLight => "Point Light",
        LightType.FlatLight => "Flat Light",
        LightType.WorldLight => "World Light",
        _ => "Unknown"
    };
}
