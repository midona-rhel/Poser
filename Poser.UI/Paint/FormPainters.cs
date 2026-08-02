using System.Numerics;
using Dalamud.Interface.Utility;

namespace Poser.UI.Reactive;

/// <summary>
/// Every form-control painter is a singleton over one wave-M paint seam: the
/// pixels are the imperative control's own, and the retained twin contributes
/// nothing but the box and the state. A painter that needs an authored value —
/// a range, a colour, a title — reads it off <see cref="PaintInput.Record"/>
/// rather than carrying per-element state, which is what keeps every one of
/// these a singleton.
/// </summary>
internal sealed class SliderPainter : IInteractivePainter
{
    internal static readonly SliderPainter Instance = new();

    private SliderPainter()
    {
    }

    public PaintOutput Paint(in PaintInput input)
    {
        // Marks are deliberately none in phase 3A: Appearance's sliders declare
        // no notches, and a mark list is a per-element allocation the record has
        // nowhere to keep.
        Poser.UI.LegacyCrystarium.PaintSlider(
            input.DrawList,
            input.Hit.ScreenMin,
            input.Hit.ScreenMax,
            input.Record.F2,
            null,
            input.Record.F0,
            input.Record.F1,
            input.Disabled);
        return default;
    }
}

internal sealed class SwitchPainter : IInteractivePainter
{
    internal static readonly SwitchPainter Instance = new();

    private SwitchPainter()
    {
    }

    public PaintOutput Paint(in PaintInput input)
    {
        Poser.UI.LegacyCrystarium.PaintSwitch(
            input.DrawList, input.Hit.ScreenMin, input.Hit.ScreenMax,
            input.Arg != 0, input.Disabled);
        return default;
    }
}

internal sealed class ColorWellBoxPainter : IInteractivePainter
{
    internal static readonly ColorWellBoxPainter Instance = new();

    private ColorWellBoxPainter()
    {
    }

    public PaintOutput Paint(in PaintInput input)
    {
        Poser.UI.LegacyCrystarium.PaintColorWellBox(
            input.Hit, input.Record.TextColor, input.Disabled);
        return default;
    }
}

/// <summary>Decorative: the bar reserves nothing and reports nothing, so it
/// rides a painted box rather than an interactive one.</summary>
internal sealed class ProgressPainter : IInteractivePainter
{
    internal static readonly ProgressPainter Instance = new();

    private ProgressPainter()
    {
    }

    public PaintOutput Paint(in PaintInput input)
    {
        Poser.UI.LegacyCrystarium.PaintProgress(
            input.DrawList, input.Hit.ScreenMin, input.BoxSize.X, input.Record.F2);
        return default;
    }
}

/// <summary>The 1px rule a section leads with — its ONLY separator.</summary>
internal sealed class SectionRulePainter : IInteractivePainter
{
    internal static readonly SectionRulePainter Instance = new();

    private SectionRulePainter()
    {
    }

    public PaintOutput Paint(in PaintInput input)
    {
        Poser.UI.LegacyCrystarium.PaintSectionRule(
            input.DrawList, input.Hit.ScreenMin, input.BoxSize.X,
            ImGuiHelpers.GlobalScale);
        return default;
    }
}

/// <summary>The 26px header row: title and chevron, with the disclosure's motion
/// keyed to the walk's own identity so a reordered section keeps its animation
/// state.</summary>
internal sealed class SectionHeaderPainter : IInteractivePainter
{
    internal static readonly SectionHeaderPainter Instance = new();

    private SectionHeaderPainter()
    {
    }

    public PaintOutput Paint(in PaintInput input)
    {
        Poser.UI.LegacyCrystarium.PaintSectionHeader(
            input.Hit,
            input.Identity,
            input.Record.Text ?? string.Empty,
            input.Arg != 0,
            input.Hit.ScreenMin,
            input.BoxSize.X);
        return default;
    }
}

/// <summary>
/// A form row's own help, registered GEOMETRICALLY over the whole row: the row
/// owns no hit box, so there is no live item to gate on. It is the LAST thing
/// the row paints, which is what makes it win over the help a control inside it
/// registered — the imperative inversion, preserved.
/// </summary>
internal sealed class RowHelpPainter : IInteractivePainter
{
    internal static readonly RowHelpPainter Instance = new();

    private RowHelpPainter()
    {
    }

    public PaintOutput Paint(in PaintInput input)
    {
        string? help = input.Record.Help;
        Vector2 min = input.Hit.ScreenMin;
        Vector2 max = input.Hit.ScreenMax;
        if (!string.IsNullOrEmpty(help)
            && Poser.UI.LegacyCrystarium.HoverHelp.HelpHovered(min, max))
            Poser.UI.LegacyCrystarium.HoverHelp.Explain(input.Id, min, max, help!);
        return default;
    }
}
