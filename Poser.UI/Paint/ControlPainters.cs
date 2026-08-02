using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;

namespace Poser.UI.Reactive;

/// <summary>
/// The surviving escape hatches, and only those: geometry no stylesheet can
/// express. Each is a singleton — a hook carries no per-element state, because
/// everything it needs is a TYPED field of the element or a value the base
/// already resolved.
/// </summary>
internal sealed class SliderPainter : IPainter
{
    internal static readonly SliderPainter Instance = new();

    private SliderPainter()
    {
    }

    public PaintResult Paint(in PaintContext context)
    {
        // Marks are deliberately none: Appearance's sliders declare no
        // notches, and a mark list is a per-element allocation the record has
        // nowhere to keep.
        Poser.UI.LegacyCrystarium.PaintSlider(
            context.DrawList,
            context.Hit.ScreenMin,
            context.Hit.ScreenMax,
            context.Record.Value,
            null,
            context.Record.On.Min,
            context.Record.On.Max,
            context.Record.Disabled);
        return default;
    }
}

internal sealed class SwitchPainter : IPainter
{
    internal static readonly SwitchPainter Instance = new();

    private SwitchPainter()
    {
    }

    public PaintResult Paint(in PaintContext context)
    {
        Poser.UI.LegacyCrystarium.PaintSwitch(
            context.DrawList, context.Hit.ScreenMin, context.Hit.ScreenMax,
            context.Record.Selected, context.Record.Disabled);
        return default;
    }
}

/// <summary>The colour well's BOX. The value it shows is the sheet's resolved
/// fill, so the well states a colour the same way anything else does.</summary>
internal sealed class ColorWellPainter : IPainter
{
    internal static readonly ColorWellPainter Instance = new();

    private ColorWellPainter()
    {
    }

    public PaintResult Paint(in PaintContext context)
    {
        Poser.UI.LegacyCrystarium.PaintColorWellBox(
            context.Hit, context.Style.Fill ?? default, context.Record.Disabled);
        return default;
    }
}

/// <summary>The determinate bar. Reserves nothing and reports nothing.</summary>
internal sealed class ProgressPainter : IPainter
{
    internal static readonly ProgressPainter Instance = new();

    private ProgressPainter()
    {
    }

    public bool NeedsHit => false;

    public PaintResult Paint(in PaintContext context)
    {
        Poser.UI.LegacyCrystarium.PaintProgress(
            context.DrawList, context.Min, context.Size.X, context.Record.Value);
        return default;
    }
}

/// <summary>The 1px rule a section leads with — its ONLY separator.</summary>
internal sealed class SectionRulePainter : IPainter
{
    internal static readonly SectionRulePainter Instance = new();

    private SectionRulePainter()
    {
    }

    public bool NeedsHit => false;

    public PaintResult Paint(in PaintContext context)
    {
        Poser.UI.LegacyCrystarium.PaintSectionRule(
            context.DrawList, context.Min, context.Size.X,
            ImGuiHelpers.GlobalScale);
        return default;
    }
}

/// <summary>
/// The 26px header row: the title, the chevron and the header's own hover
/// swap are ONE legacy seam shared with the imperative page, and the
/// disclosure's motion is keyed to the walk's identity so a reordered section
/// keeps its animation state. <c>Selected</c> IS the expanded flag.
/// </summary>
internal sealed class SectionHeaderPainter : IPainter
{
    internal static readonly SectionHeaderPainter Instance = new();

    private SectionHeaderPainter()
    {
    }

    public bool OwnsText => true;

    public PaintResult Paint(in PaintContext context)
    {
        Poser.UI.LegacyCrystarium.PaintSectionHeader(
            context.Hit,
            context.Identity,
            context.Record.Text ?? string.Empty,
            context.Record.Selected,
            context.Min,
            context.Size.X);
        return default;
    }
}

// Dropdown painters live in DropdownPainters.cs and the picker's in
// PickerPainters.cs, so the two component rewrites own disjoint files.
