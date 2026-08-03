using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;

namespace Poser.UI.Reactive;

/// <summary>
/// The chassis panel's one seam: a fill rounded on the corners the element
/// named. Everything else about the box is the sheet's, so the hook is a
/// single <c>AddRectFilled</c> with corner flags — and there is one instance
/// per corner mask, because the mask is the hook's whole state.
/// </summary>
internal sealed class ChassisPainter : IPainter
{
    private static readonly ChassisPainter?[] Cache = new ChassisPainter?[16];

    private readonly ImDrawFlags _corners;

    private ChassisPainter(ImDrawFlags corners) => _corners = corners;

    internal static ChassisPainter For(Poser.UI.UiCorners corners)
    {
        int index = (int)corners & 0xF;
        return Cache[index] ??= new ChassisPainter(Flags((Poser.UI.UiCorners)index));
    }

    /// <summary>Chrome, not a control: a panel that reserved would take the
    /// hover of every control standing on it.</summary>
    public bool NeedsHit => false;

    public PaintResult Paint(in PaintContext context)
    {
        if (context.Style.Fill is not { } fill)
            return default;
        context.DrawList.AddRectFilled(
            context.Min,
            context.Max,
            ImGui.ColorConvertFloat4ToU32(Poser.UI.ColorEx.ApplyAlpha(fill)),
            context.Style.Radius * ImGuiHelpers.GlobalScale,
            _corners);
        return default;
    }

    /// <summary>ImGui reads an EMPTY corner set as "round them all", so the
    /// square box states its refusal outright rather than by omission.</summary>
    private static ImDrawFlags Flags(Poser.UI.UiCorners corners)
    {
        if (corners == Poser.UI.UiCorners.None)
            return ImDrawFlags.RoundCornersNone;
        ImDrawFlags flags = 0;
        if ((corners & Poser.UI.UiCorners.TopLeft) != 0)
            flags |= ImDrawFlags.RoundCornersTopLeft;
        if ((corners & Poser.UI.UiCorners.TopRight) != 0)
            flags |= ImDrawFlags.RoundCornersTopRight;
        if ((corners & Poser.UI.UiCorners.BottomLeft) != 0)
            flags |= ImDrawFlags.RoundCornersBottomLeft;
        if ((corners & Poser.UI.UiCorners.BottomRight) != 0)
            flags |= ImDrawFlags.RoundCornersBottomRight;
        return flags;
    }
}
