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
        {
            var isOn = light.IsLightOn;
            using (var row = Flex.Row(Flex.RowHeight, Flex.SmallGap))
            {
                row.Label("Enabled", 80);
                row.Fill((w, h) =>
                {
                    if (PoserCheckbox.Draw("##light_on", ref isOn))
                    {
                        light.IsLightOn = isOn;
                    }
                });
            }
        }

        // Light type (read-only)
        using (var row = Flex.Row(Flex.RowHeight, Flex.SmallGap))
        {
            row.Label("Type", 80);
            row.Text(GetLightTypeName(light.LightType));
        }

        PoserUI.Separator();

        // Color (convert Vector3 RGB to Vector4 for color picker)
        {
            var color = new Vector4(light.Color / 20f, 1f);
            using (var row = Flex.Row(Flex.RowHeight, Flex.SmallGap))
            {
                row.Label("Color", 80);
                row.Fill((w, h) =>
                {
                    ImGui.SetNextItemWidth(w);
                    if (ImGui.ColorEdit3("##light_color", ref color, ImGuiColorEditFlags.NoInputs))
                    {
                        light.Color = new Vector3(color.X, color.Y, color.Z) * 20f;
                    }
                });
            }
        }

        // Intensity
        {
            var intensity = light.Intensity;
            using (var row = Flex.Row(Flex.RowHeight, Flex.SmallGap))
            {
                row.Label("Intensity", 80);
                row.Fill((w, h) =>
                {
                    if (Scrubber.Draw("##light_intensity", ref intensity, 0f, 10f, 0f, w))
                    {
                        light.Intensity = intensity;
                    }
                });
            }
        }

        // Range
        {
            var range = light.Range;
            using (var row = Flex.Row(Flex.RowHeight, Flex.SmallGap))
            {
                row.Label("Range", 80);
                row.Fill((w, h) =>
                {
                    if (Scrubber.Draw("##light_range", ref range, 1f, 200f, 0f, w))
                    {
                        light.Range = range;
                    }
                });
            }
        }

        PoserUI.Separator();

        // Falloff Type
        {
            var falloffIndex = (int)light.FalloffType;
            using (var row = Flex.Row(Flex.RowHeight, Flex.SmallGap))
            {
                row.Label("Falloff Type", 80);
                row.Fill((w, h) =>
                {
                    ImGui.SetNextItemWidth(w);
                    if (ImGui.Combo("##light_falloff_type", ref falloffIndex, FalloffTypeNames, FalloffTypeNames.Length))
                    {
                        light.FalloffType = (FalloffType)falloffIndex;
                    }
                });
            }
        }

        // Falloff
        {
            var falloff = light.Falloff;
            using (var row = Flex.Row(Flex.RowHeight, Flex.SmallGap))
            {
                row.Label("Falloff", 80);
                row.Fill((w, h) =>
                {
                    if (Scrubber.Draw("##light_falloff", ref falloff, 0f, 10f, 0f, w))
                    {
                        light.Falloff = falloff;
                    }
                });
            }
        }

        // Spot light specific controls
        if (light.LightType == LightType.SpotLight)
        {
            PoserUI.Separator();

            // Spot Angle
            {
                var spotAngle = light.SpotAngle;
                using (var row = Flex.Row(Flex.RowHeight, Flex.SmallGap))
                {
                    row.Label("Spot Angle", 80);
                    row.Fill((w, h) =>
                    {
                        if (Scrubber.Draw("##light_spot_angle", ref spotAngle, 1f, 180f, 0f, w))
                        {
                            light.SpotAngle = spotAngle;
                        }
                    });
                }
            }

            // Falloff Angle
            {
                var falloffAngle = light.FalloffAngle;
                using (var row = Flex.Row(Flex.RowHeight, Flex.SmallGap))
                {
                    row.Label("Edge Falloff", 80);
                    row.Fill((w, h) =>
                    {
                        if (Scrubber.Draw("##light_falloff_angle", ref falloffAngle, 0f, 1f, 0f, w))
                        {
                            light.FalloffAngle = falloffAngle;
                        }
                    });
                }
            }
        }

        PoserUI.Separator();

        // Reflection
        {
            var hasReflection = light.HasReflection;
            using (var row = Flex.Row(Flex.RowHeight, Flex.SmallGap))
            {
                row.Label("Reflection", 80);
                row.Fill((w, h) =>
                {
                    if (PoserCheckbox.Draw("##light_reflection", ref hasReflection))
                    {
                        light.HasReflection = hasReflection;
                    }
                });
            }
        }

        // Character Shadow
        {
            var castsShadow = light.CastsCharacterShadow;
            using (var row = Flex.Row(Flex.RowHeight, Flex.SmallGap))
            {
                row.Label("Char Shadow", 80);
                row.Fill((w, h) =>
                {
                    if (PoserCheckbox.Draw("##light_shadow", ref castsShadow))
                    {
                        light.CastsCharacterShadow = castsShadow;
                    }
                });
            }
        }

        // Shadow Range (only if shadows enabled)
        if (light.CastsCharacterShadow)
        {
            var shadowRange = light.CharacterShadowRange;
            using (var row = Flex.Row(Flex.RowHeight, Flex.SmallGap))
            {
                row.Label("Shadow Range", 80);
                row.Fill((w, h) =>
                {
                    if (Scrubber.Draw("##light_shadow_range", ref shadowRange, 1f, 200f, 0f, w))
                    {
                        light.CharacterShadowRange = shadowRange;
                    }
                });
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
