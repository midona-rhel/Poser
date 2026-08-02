using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;

namespace Poser.UI.Reactive;

/// <summary>
/// The closed <c>.btn</c> box. Both pieces of content the trigger holds take
/// their treatment from this one return value: the label is the resolved
/// foreground, the chevron is the subtree's glyph opacity.
/// </summary>
internal sealed class DropdownTriggerPainter : IInteractivePainter
{
    internal static readonly DropdownTriggerPainter Instance = new();

    private DropdownTriggerPainter()
    {
    }

    public PaintOutput Paint(in PaintInput input)
    {
        Poser.UI.LegacyCrystarium.DropdownTriggerPaint paint =
            Poser.UI.LegacyCrystarium.PaintDropdownBox(input.Hit, input.Disabled);
        return new PaintOutput(paint.LabelColor, paint.ChevronOpacity);
    }
}

/// <summary>
/// One <c>.opt</c> row's state fill. <c>Arg</c> is the selected flag, and
/// <c>:hover</c> and <c>.optActive</c> are the same token, so one test covers
/// both. The fill spans the ARRANGED box rather than the reservation: a
/// scrolling menu reserves rows clear of the scrollbar gutter but still paints
/// them across the whole surface.
/// </summary>
internal sealed class DropdownRowPainter : IInteractivePainter
{
    internal static readonly DropdownRowPainter Instance = new();

    private DropdownRowPainter()
    {
    }

    public PaintOutput Paint(in PaintInput input)
    {
        if (input.Arg != 0 || input.Hit.Hovered)
            Poser.UI.LegacyCrystarium.PaintDropdownRowFill(
                input.DrawList,
                input.Hit.ScreenMin,
                input.BoxSize,
                Poser.UI.Crystarium.ActiveTheme.Radii.Medium * ImGuiHelpers.GlobalScale);
        // `.opt` declares neither a color nor a glyph opacity, so its content
        // keeps the theme default and whatever the surface already resolved.
        return new PaintOutput(null, null);
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
