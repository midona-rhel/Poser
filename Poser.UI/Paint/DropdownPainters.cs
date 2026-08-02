using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;

namespace Poser.UI.Reactive;

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
