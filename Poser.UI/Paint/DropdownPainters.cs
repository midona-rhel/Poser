using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;

namespace Poser.UI.Reactive;

/// <summary>
/// The closed <c>.btn</c> box. A HOOK for the same reason CheckBoxPainter is
/// one: the legacy box draws its border as four mitred per-side path strokes
/// (BoxRenderer), which is not the base's single centred stroke — the frozen
/// pixels demand the seam. Both pieces of content the trigger holds take
/// their treatment from the one return value: the label is the resolved
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

// The row's state fill is the DropdownRow sheet's (Hover/Active/Selected
// looks over the base box paint) — no painter.

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
