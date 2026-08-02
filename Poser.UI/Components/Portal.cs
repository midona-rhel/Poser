using System.Numerics;
using Poser.UI.Reactive;

namespace Poser.UI;

public static partial class Crystarium
{
    /// <summary>
    /// A floating surface declared INSIDE the tree that opens it. The element
    /// is out of flow — it contributes nothing to its parent's box — and its
    /// children are laid out against the surface's own constraints, then
    /// walked on the popup rather than under the parent.
    ///
    /// <para>The portal is declared as a CHILD of its anchor, and that is not
    /// a convention: the popup handle and the anchor rect are both read off the
    /// parent's path, so the surface's identity follows the control it belongs
    /// to instead of wherever in the tree it was written.</para>
    ///
    /// <para>Internal because a portal is a control's own machinery: the
    /// anchor wiring is a pair of arena indices, and a caller that got one
    /// wrong would anchor a menu to a stranger.</para>
    /// </summary>
    /// <param name="contentSize">Logical surface span. A ZERO width means "as
    /// wide as the anchor", the only span a Fill-sized control knows before the
    /// solver has granted it one.</param>
    /// <param name="capChildHitWidth">Reserve the first interactive layer clear
    /// of the scrollbar gutter while their boxes keep the full width. Only
    /// meaningful with <paramref name="scrollRegionHeight"/>.</param>
    /// <param name="scrollFromChild">Index of the first child INSIDE the scroll
    /// viewport; the children before it are the surface's fixed head. 0 scrolls
    /// everything, which is what a menu wants and a picker does not.</param>
    /// <param name="treatment">Whether the host draws the shared glass shell
    /// around the surface, or the surface paints its own panel through
    /// <paramref name="surface"/>.</param>
    internal static UiNode Portal(
        UiChildren children,
        Vector2 contentSize,
        float padding,
        float anchorCompensation,
        float scrollRegionHeight,
        bool capChildHitWidth,
        IPortalSurfacePainter? surface,
        FloatingSurfaceTreatment treatment = FloatingSurfaceTreatment.Unframed,
        int scrollFromChild = 0,
        UiKey key = default)
    {
        FrameArena arena = FrameArena.Require();
        arena.ValidateChildren(children);
        ElementRecord record = default;
        record.Kind = ElementKind.Portal;
        record.Key = key;
        record.ChildStart = children.Start;
        record.ChildCount = children.Count;
        record.PortalContentSize = contentSize;
        record.PortalPadding = padding;
        record.PortalAnchorCompensation = anchorCompensation;
        record.ScrollRegionHeight = scrollRegionHeight;
        record.PortalScrollFromChild = scrollFromChild;
        record.PortalTreatment = (byte)treatment;
        record.Arg = capChildHitWidth ? 1 : 0;
        // A portal paints ONE box and never reserves, so it has no interactive
        // painter to compete for the slot.
        record.PainterSlot = surface is null ? 0 : arena.AddObject(surface);
        return arena.AddElement(record);
    }

    /// <summary>
    /// Wires an already-declared portal to the element it hangs under. The
    /// anchor cannot be named at declaration time — a parent is written into
    /// the arena after its children — so the one back-reference is patched in
    /// once the anchor exists.
    /// </summary>
    internal static void AnchorPortal(UiNode portal, UiNode anchor)
    {
        FrameArena arena = FrameArena.Require();
        arena.ValidateNode(portal);
        arena.ValidateNode(anchor);
        arena[portal.Index].AnchorNode = anchor.Index;
    }
}
