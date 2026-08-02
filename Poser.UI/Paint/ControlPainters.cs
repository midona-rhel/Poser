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
            context.Record.Selected, context.Record.Disabled,
            context.Identity);
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

/// <summary>
/// The section header with NOTHING to disclose: same seam, default hit, open
/// forever — which is exactly what the imperative non-collapsible section
/// hands it. No reservation, so a static title never hovers.
/// </summary>
internal sealed class StaticSectionHeaderPainter : IPainter
{
    internal static readonly StaticSectionHeaderPainter Instance = new();

    private StaticSectionHeaderPainter()
    {
    }

    public bool NeedsHit => false;

    public bool OwnsText => true;

    public PaintResult Paint(in PaintContext context)
    {
        Poser.UI.LegacyCrystarium.PaintSectionHeader(
            default,
            0,
            context.Record.Text ?? string.Empty,
            true,
            context.Min,
            context.Size.X);
        return default;
    }
}

/// <summary>The selected segment's fill pair; the tab's text and tones are
/// the base's, driven by the sheet's states.</summary>
internal sealed class SegmentTabPainter : IPainter
{
    internal static readonly SegmentTabPainter Instance = new();

    private SegmentTabPainter()
    {
    }

    public PaintResult Paint(in PaintContext context)
    {
        if (context.Record.Selected)
            Poser.UI.LegacyCrystarium.PaintSegmentActive(
                context.DrawList, context.Min, context.Max);
        return default;
    }
}

/// <summary>The accent swatch dot. The colour it shows is the sheet's
/// resolved fill; selection is the element's, hover is the hit's.</summary>
internal sealed class SwatchPainter : IPainter
{
    internal static readonly SwatchPainter Instance = new();

    private SwatchPainter()
    {
    }

    public PaintResult Paint(in PaintContext context)
    {
        Poser.UI.LegacyCrystarium.PaintSwatchDot(
            context.DrawList,
            context.Min,
            context.Size.Y / ImGuiHelpers.GlobalScale,
            context.Style.Fill ?? default,
            context.Record.Selected,
            context.Hit.Hovered);
        return default;
    }
}

/// <summary>
/// The bar's 1px edge rule, painted on the BORDER box so it reaches the
/// window edges the bar spans — and steals no height from the content row,
/// which centres on the full bar exactly as the imperative bar centred.
/// </summary>
internal sealed class BarSeparatorPainter : IPainter
{
    internal static readonly BarSeparatorPainter Top = new(top: true);
    internal static readonly BarSeparatorPainter Bottom = new(top: false);

    private readonly bool _top;

    private BarSeparatorPainter(bool top) => _top = top;

    public bool NeedsHit => false;

    public PaintResult Paint(in PaintContext context)
    {
        float thickness = System.MathF.Max(1f, ImGuiHelpers.GlobalScale);
        float y = _top ? context.Min.Y : context.Max.Y - thickness;
        context.DrawList.AddRectFilled(
            new Vector2(context.Min.X, y),
            new Vector2(context.Max.X, y + thickness),
            ImGui.ColorConvertFloat4ToU32(
                Poser.UI.LegacyCrystarium.ActiveTheme.FormSeparator));
        return default;
    }
}

/// <summary>The modal footer band: the ModalFooter fill rounded to the
/// window's bottom corners, exactly as the imperative chassis painted it.
/// </summary>
internal sealed class ModalFooterPainter : IPainter
{
    internal static readonly ModalFooterPainter Instance = new();

    private ModalFooterPainter()
    {
    }

    public bool NeedsHit => false;

    public PaintResult Paint(in PaintContext context)
    {
        var theme = Poser.UI.LegacyCrystarium.ActiveTheme;
        context.DrawList.AddRectFilled(
            context.Min,
            context.Max,
            ImGui.ColorConvertFloat4ToU32(theme.Chrome.ModalFooter),
            theme.Radii.Window * ImGuiHelpers.GlobalScale,
            ImDrawFlags.RoundCornersBottom);
        return default;
    }
}

// Dropdown painters live in DropdownPainters.cs and the picker's in
// PickerPainters.cs, so the two component rewrites own disjoint files.
