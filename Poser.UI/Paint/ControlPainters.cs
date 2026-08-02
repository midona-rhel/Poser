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

/// <summary>
/// OverlayShell's <c>box-shadow: 0 -1px 0 var(--color-border-secondary) inset</c>
/// — the hairline the header and the search area each carry along their BOTTOM
/// edge. An inset shadow is painted inside the box, so it costs the band no
/// height and the content above it never shifts.
/// </summary>
internal sealed class PickerRulePainter : IPainter
{
    internal static readonly PickerRulePainter Instance = new();

    private PickerRulePainter()
    {
    }

    public bool NeedsHit => false;

    public PaintResult Paint(in PaintContext context)
    {
        float scale = ImGuiHelpers.GlobalScale;
        Vector2 max = context.Max;
        context.DrawList.AddRectFilled(
            new Vector2(context.Min.X, max.Y - scale),
            max,
            ImGui.ColorConvertFloat4ToU32(
                Poser.UI.ColorEx.ApplyAlpha(Poser.UI.Crystarium.ActiveTheme.Border)));
        return default;
    }
}

/// <summary>
/// OverlayShell's <c>.checkBox</c>: a 14px square filled <c>--color-black-20</c>
/// under a 1px INSET outline at <c>--color-pressed-overlay</c>. Checked it
/// becomes solid <c>--color-primary</c> with the outline dropped, which is why
/// the two states are one hook and not two boxes — and why it is a hook at
/// all: the per-side mitred outline is not the base's single-stroke border.
/// </summary>
internal sealed class CheckBoxPainter : IPainter
{
    internal static readonly CheckBoxPainter Instance = new();

    private CheckBoxPainter()
    {
    }

    public bool NeedsHit => false;

    public PaintResult Paint(in PaintContext context)
    {
        Theme theme = Poser.UI.Crystarium.ActiveTheme;
        bool @checked = context.Record.Selected;
        // --color-pressed-overlay is declared by tokens.css but is not carried
        // by the generated projection, so it is derived on the same terms as
        // Chrome.DangerHover: the theme's own overlay hue at .20.
        Vector4? outline = @checked
            ? null
            : theme.Chrome.ActiveOverlay with { W = 0.20f };
        Poser.UI.BoxRenderer.Draw(
            context.DrawList,
            context.Min,
            context.Max,
            new Poser.UI.BoxStyle
            {
                BackgroundColor = @checked
                    ? theme.Chrome.Primary
                    : theme.Chrome.InputWell,
                BorderRadius = theme.Radii.Medium,
                BorderWidth = outline is null ? 0f : 1f,
                BorderTopColor = outline,
                BorderRightColor = outline,
                BorderBottomColor = outline,
                BorderLeftColor = outline,
            });
        // .checkBoxChecked { color: rgba(255,255,255,.99) } — the glyph inside
        // takes it as currentColor rather than restating it.
        return new PaintResult(@checked ? theme.Chrome.Checkmark : null, null);
    }
}

/// <summary>
/// The closed <c>.btn</c> box. Both pieces of content the trigger holds take
/// their treatment from this one return value: the label is the resolved
/// foreground, the chevron is the subtree's glyph opacity.
/// </summary>
internal sealed class DropdownTriggerPainter : IPainter
{
    internal static readonly DropdownTriggerPainter Instance = new();

    private DropdownTriggerPainter()
    {
    }

    public PaintResult Paint(in PaintContext context)
    {
        Poser.UI.LegacyCrystarium.DropdownTriggerPaint paint =
            Poser.UI.LegacyCrystarium.PaintDropdownBox(
                context.Hit, context.Record.Disabled);
        return new PaintResult(paint.LabelColor, paint.ChevronOpacity);
    }
}

/// <summary>
/// One <c>.opt</c> row's state fill. <c>:hover</c> and <c>.optActive</c> are
/// the same token, so one test covers both. The fill spans the ARRANGED box
/// rather than the reservation: a scrolling menu reserves rows clear of the
/// scrollbar gutter but still paints them across the whole surface.
/// </summary>
internal sealed class DropdownRowPainter : IPainter
{
    internal static readonly DropdownRowPainter Instance = new();

    private DropdownRowPainter()
    {
    }

    public PaintResult Paint(in PaintContext context)
    {
        if (context.Record.Selected || context.Hit.Hovered)
            Poser.UI.LegacyCrystarium.PaintDropdownRowFill(
                context.DrawList,
                context.Min,
                context.Size,
                Poser.UI.Crystarium.ActiveTheme.Radii.Medium * ImGuiHelpers.GlobalScale);
        return default;
    }
}

/// <summary>The open <c>.drop</c> panel behind the rows.</summary>
internal sealed class DropdownSurfacePainter : IPortalSurfacePainter
{
    internal static readonly DropdownSurfacePainter Instance = new();

    private DropdownSurfacePainter()
    {
    }

    public void Paint(ImDrawListPtr drawList, Vector2 min, Vector2 max) =>
        Poser.UI.LegacyCrystarium.PaintDropdownSurface(drawList, min, max);
}
