using System.Numerics;
using Poser.UI.Reactive;

namespace Poser.UI;

public static partial class Crystarium
{
    /// <summary>
    /// A floating surface declared INSIDE the tree that opens it. The element
    /// is out of flow — it contributes nothing to its parent's box — and its
    /// children are laid out against the surface's own constraints, then walked
    /// on the popup rather than under the parent.
    ///
    /// <para>The portal is declared as a CHILD of its anchor, and that is not a
    /// convention: the popup handle and the anchor rect are both read off the
    /// parent's path, so the surface's identity follows the control it belongs
    /// to instead of wherever in the tree it was written.</para>
    ///
    /// <para>Internal because a portal is a control's own machinery: the anchor
    /// wiring is a pair of arena indices, and a caller that got one wrong would
    /// anchor a menu to a stranger.</para>
    /// </summary>
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
        float scrollGutter = 0f,
        UiKey key = default)
    {
        FrameArena arena = FrameArena.Require();
        arena.ValidateChildren(children);
        ElementRecord record = default;
        record.Key = key;
        record.ChildStart = children.Start;
        record.ChildCount = children.Count;
        record.PortalSlot = arena.AddPortal(new PortalRecord
        {
            ContentSize = contentSize,
            Padding = padding,
            AnchorCompensation = anchorCompensation,
            ScrollRegionHeight = scrollRegionHeight,
            ScrollGutter = scrollGutter,
            ScrollFromChild = scrollFromChild,
            Treatment = (byte)treatment,
            CapChildHitWidth = capChildHitWidth,
            Surface = surface,
        });
        return arena.AddElement(in record);
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
        arena.Portal(arena[portal.Index].PortalSlot).AnchorNode = anchor.Index;
    }
}
