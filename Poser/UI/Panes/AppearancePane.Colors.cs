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
            bool pending = _colors.IsPending(actor);
            var reading = _colors.Read(actor);
            if (pending) form.Status("Resetting colour — waiting for the actor to redraw…");
            else if (!reading.Success) form.Status(reading.Detail ?? "Custom colours are unavailable.");
            foreach (var (channel, label) in CustomColourRows)
            {
                var owned = _colors.Override(actor, channel);
                Vector4 observed = reading.Value is { } values && values.TryGetValue(channel, out var value)
                    ? value : Vector4.One;
                bool disabled = pending || !reading.Success || !_appearanceAccess.CanEdit;
                form.Cells(cells =>
                {
                    cells.Cell(label, cell =>
                    {
                        ImGui.SetCursorScreenPos(cell.Center(Crystarium.ActiveTheme.Controls.ColorWellSize));
                        Crystarium.ColorWell($"custom-colour-{actor}-{channel}", owned ?? observed,
                            next => ReportColour(_colors.Set(actor, channel, next)),
                            rgbOnly: channel != AppearanceColorChannel.Mouth, disabled: disabled,
                            help: "Edit to use a custom colour. Reset returns to the current palette or design.",
                            onBegin: _colors.Seal, onCommit: _colors.Seal);
                    });
                    cells.Cell("Source", cell => cell.Text(owned.HasValue ? "Custom" : "Palette / design"));
                    cells.Cell("", cell => cell.Button($"reset-custom-{channel}", "Reset",
                        () => _colors.Clear(actor, channel, ReportColour),
                        disabled: disabled || !owned.HasValue,
                        help: "Reveal this channel's current palette or design colour"));
                });
            }
        });
    }

    private void ReportColour(ValueWriteResult result)
    {
        if (!result.Success) _notices.Failed(result.Detail ?? "The custom colour could not be changed.");
    }
}
