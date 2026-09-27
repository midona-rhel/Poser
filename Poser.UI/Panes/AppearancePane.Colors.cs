using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Poser.Application.Transforms;
using Poser.Domain.Identity;
using Poser.Domain.Presentation;

namespace Poser.UI;

public sealed partial class AppearancePane
{
    private bool _openCustomColours = true;
    private static readonly (AppearanceColorChannel Channel, string Label)[] CustomColourRows =
    [
        (AppearanceColorChannel.Skin, "Skin"), (AppearanceColorChannel.Hair, "Hair"),
        (AppearanceColorChannel.Highlights, "Highlights"), (AppearanceColorChannel.LeftEye, "Left eye"),
        (AppearanceColorChannel.RightEye, "Right eye"), (AppearanceColorChannel.Mouth, "Mouth"),
        (AppearanceColorChannel.Feature, "Feature"),
    ];

    private void DrawCustomColours(Crystarium.PageScope page, ActorId actor)
    {
        page.Section("Custom colours", _openCustomColours, next => _openCustomColours = next, form =>
        {
            var reading = _colors.Read(actor);
            if (!reading.Success) form.Status(reading.Detail ?? "Custom colours are unavailable.");
            var theme = Crystarium.ActiveTheme;
            bool disabled = !reading.Success || !_appearanceAccess.CanEdit;
            float controlsWidth = theme.Controls.ColorWellSize;
            FixedColourGroups(form, "custom-colour", CustomColourRows.Length,
                index => CustomColourRows[index].Label, controlsWidth, (index, origin, scale) =>
                {
                    var (channel, _) = CustomColourRows[index];
                    var owned = _colors.Override(actor, channel);
                    Vector4 observed = reading.Value is { } values && values.TryGetValue(channel, out var value)
                        ? value : Vector4.One;
                    float side = theme.Controls.ColorWellSize * scale;
                    ImGui.SetCursorScreenPos(origin);
                    ImGui.BeginGroup();
                    Crystarium.ColorWell($"custom-colour-{actor}-{channel}", owned ?? observed,
                        next => ReportColour(_colors.Set(actor, channel, next)),
                        rgbOnly: channel != AppearanceColorChannel.Mouth, disabled: disabled, hdr: true,
                        help: owned.HasValue
                            ? "Custom colour active. Right-click to restore the incoming colour."
                            : "No custom colour. Edit to enable an override.",
                        onBegin: _colors.Seal,
                        onRightClick: () =>
                        {
                            if (owned.HasValue) ReportColour(_colors.Clear(actor, channel));
                        });
                    if (owned.HasValue)
                        ImGui.GetWindowDrawList().AddRect(origin - new Vector2(scale),
                            origin + new Vector2(side + scale),
                            ImGui.ColorConvertFloat4ToU32(theme.Accent),
                            theme.Radii.Control * scale, ImDrawFlags.None, scale);
                    ImGui.EndGroup();
                });
        });
    }

    private static void FixedColourGroups(
        Crystarium.FormScope form, string id, int count, Func<int, string> labelAt,
        float controlsWidth, Action<int, Vector2, float> draw)
    {
        var theme = Crystarium.ActiveTheme;
        float scale = ImGuiHelpers.GlobalScale;
        var labelStyle = new TextStyle { Size = theme.Typography.LabelSize, Color = theme.FormLabel };
        float labelWidth = 0f;
        for (int i = 0; i < count; i++)
            labelWidth = MathF.Max(labelWidth, Crystarium.MeasureText(labelAt(i), labelStyle).X);
        float labelGap = theme.Spacing.Three * scale;
        float columnGap = theme.Spacing.Six * scale;
        form.EndPair();
        // Keep row membership stable while editing or resizing; extra width
        // spreads the same two tracks instead of moving swatches between rows.
        const int columns = 2;
        for (int start = 0; start < count; start += columns)
        {
            int first = start;
            form.Canvas($"{id}-row-{first}", theme.Controls.FormRowHeight, (rowOrigin, size) =>
            {
                float track = (size.X - columnGap * (columns - 1)) / columns;
                float labelSpace = MathF.Min(labelWidth,
                    MathF.Max(0f, track - labelGap - controlsWidth * scale));
                for (int column = 0; column < columns && first + column < count; column++)
                {
                    int index = first + column;
                    var group = rowOrigin + new Vector2(column * (track + columnGap), 0f);
                    if (labelSpace > 0f)
                        Crystarium.TextInBand(group, new Vector2(labelSpace, size.Y), labelAt(index),
                            labelStyle, TextConstraint.Truncate(labelSpace));
                    var control = group + new Vector2(MathF.Max(0f, track - controlsWidth * scale),
                        (size.Y - theme.Controls.WorkspaceHeight * scale) * 0.5f);
                    draw(index, control, scale);
                }
            });
        }
    }

    private void ReportColour(ValueWriteResult result)
    {
        if (!result.Success) _notices.Failed(result.Detail ?? "The custom colour could not be changed.");
    }
}
