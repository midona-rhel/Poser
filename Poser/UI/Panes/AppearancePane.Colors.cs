using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
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
            foreach (var (channel, label) in CustomColourRows)
            {
                var owned = _colors.Override(actor, channel);
                Vector4 observed = reading.Value is { } values && values.TryGetValue(channel, out var value)
                    ? value : Vector4.One;
                bool disabled = !reading.Success || !_appearanceAccess.CanEdit;
                var theme = Crystarium.ActiveTheme;
                form.Custom(label, theme.Controls.FormRowHeight, row =>
                {
                    var origin = row.CenterControl(theme.Controls.ColorWellSize);
                    float side = theme.Controls.ColorWellSize * row.Scale;
                    ImGui.SetCursorScreenPos(origin);
                    ImGui.BeginGroup();
                    Crystarium.ColorWell($"custom-colour-{actor}-{channel}", owned ?? observed,
                        next => ReportColour(_colors.Set(actor, channel, next)),
                        rgbOnly: channel != AppearanceColorChannel.Mouth, disabled: disabled,
                        help: owned.HasValue
                            ? "Custom colour active. Reset restores the captured incoming colour."
                            : "No custom colour. Edit to enable an override.",
                        onBegin: _colors.Seal, onCommit: _colors.Seal);
                    if (owned.HasValue)
                        ImGui.GetWindowDrawList().AddRect(origin - new Vector2(row.Scale),
                            origin + new Vector2(side + row.Scale),
                            ImGui.ColorConvertFloat4ToU32(theme.Accent),
                            theme.Radii.Control * row.Scale, ImDrawFlags.None, row.Scale);
                    // One fixed group in one form row: reset stays beside its well
                    // even when the surrounding form uses a narrow layout.
                    var resetAt = row.CenterControl(theme.Controls.WorkspaceHeight);
                    resetAt.X = origin.X + side + theme.Page.ActionGap * row.Scale;
                    ImGui.SetCursorScreenPos(resetAt);
                    Crystarium.IconButton(TablerIcon.ArrowBackUp,
                        () => ReportColour(_colors.Clear(actor, channel)),
                        ControlStyle.Square(theme.Controls.WorkspaceHeight),
                        disabled: disabled || !owned.HasValue,
                        help: "Reset to the captured incoming colour",
                        id: $"reset-custom-{actor}-{channel}");
                    ImGui.EndGroup();
                });
            }
        });
    }

    private void ReportColour(ValueWriteResult result)
    {
        if (!result.Success) _notices.Failed(result.Detail ?? "The custom colour could not be changed.");
    }
}
