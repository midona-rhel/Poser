using System;
using System.Diagnostics;
using System.Numerics;
using System.Threading;

namespace Poser.UI.Reactive;

/// <summary>
/// An island of imperative ImGui inside the retained tree — the named native
/// escape hatch. The runtime resolves the identity, places the cursor at
/// <paramref name="min"/> and hands over the arranged rect; the island owns
/// every widget it draws inside it and MUST NOT paint outside it.
/// </summary>
internal interface INativeElement
{
    void Draw(string id, Vector2 min, Vector2 max);
}

/// <summary>
/// One frame declaration. There is ONE record type because there is one
/// element: every facet below is optional, and a control is a projection onto
/// this shape rather than a species of it. Plain mutable fields on purpose —
/// records live in a pooled array and are written by index, never copied per
/// frame.
/// </summary>
internal struct ElementRecord
{
    /// <summary>The family sheet, and the frame-scoped inline patch (0 = the
    /// element states nothing of its own). The patch lives in a bump arena so
    /// a record never carries a fat sheet copy.</summary>
    internal Poser.UI.SheetRef Sheet;
    internal int PatchSlot;

    internal Poser.UI.Listeners On;

    // Leaf content. Two nullable facets, NOT two species: an element with
    // neither is a box, one with both is a box that draws a run and a glyph.
    internal string? Text;
    internal string? Glyph;
    internal float GlyphSize;
    internal float GlyphStroke;
    internal bool GlyphNoInherit;

    /// <summary>A host-owned image handle, 0 for none. It WINS over the glyph:
    /// the fallback is stated beside it so a row that never resolves its icon
    /// still draws one.</summary>
    internal nint Texture;
    internal float TextureSize;
    internal bool Preview;

    internal bool Disabled;
    internal bool Selected;
    internal string? Help;
    internal Poser.UI.UiKey Key;

    internal int ChildStart;
    internal int ChildCount;
    internal int ScopeId;

    /// <summary>The normalized 0..1 position a ranged control shows. Written
    /// by the drag path BEFORE paint, so the thumb lands under the pointer on
    /// the frame the pointer moved.</summary>
    internal float Value;

    /// <summary>Notch positions in the control's value space; a caller-retained
    /// reference, never synthesized per frame.</summary>
    internal float[]? Marks;

    /// <summary>What <see cref="Poser.UI.Listeners.OnPick"/> reports.</summary>
    internal int Index;

    /// <summary>The escape hatch for geometry a sheet cannot express.</summary>
    internal IPainter? Painter;

    /// <summary>The painter owns the box, so its subtree is clipped to it;
    /// the walk pushes the clip once around the whole child traversal.</summary>
    internal bool ClipChildren;

    // Floating-surface wiring. A trigger names the portal it opens; the
    // portal's own geometry lives in the side arena, keyed by this slot.
    internal int OpensPortalNode;
    internal bool ClosesPortal;
    internal Poser.UI.Activation ActivateOn;
    internal int PortalSlot;

    /// <summary>The arena object slot holding an <see cref="INativeElement"/>.</summary>
    internal int NativeSlot;

    // Filled by the layout pass. Typography is resolved there because a run's
    // intrinsic box is made of it, and no pseudo state can reach it.
    internal Poser.UI.ResolvedLayout Layout;
    internal Poser.UI.ResolvedType Type;

    /// <summary>The run's PHYSICAL measure, taken once by the measure pass and
    /// reused by the paint.</summary>
    internal Vector2 TextSize;
    internal Vector2 LogicalSize;
    internal Vector2 LogicalPos;
}

/// <summary>
/// A floating surface's own geometry, kept OFF the element record: a portal is
/// rare and its box is fat, so the one element pays nothing for the facet it
/// does not use.
/// </summary>
internal struct PortalRecord
{
    /// <summary>Logical surface span. A ZERO width means "as wide as the
    /// anchor" — a Fill-sized trigger has no span until the solver grants it.
    /// </summary>
    internal Vector2 ContentSize;
    internal float Padding;
    internal float AnchorCompensation;

    /// <summary>The scroll viewport's logical height; 0 for a surface whose
    /// children do not scroll.</summary>
    internal float ScrollRegionHeight;

    /// <summary>The reserved bar width for the portal's scroll wrap, logical;
    /// zero takes the theme's shell gutter.</summary>
    internal float ScrollGutter;

    /// <summary>Index of the first child INSIDE the viewport. The children
    /// before it are the surface's fixed head.</summary>
    internal int ScrollFromChild;

    /// <summary>Mirrors <see cref="Poser.UI.FloatingSurfaceTreatment"/>.</summary>
    internal byte Treatment;

    /// <summary>Reserve the first interactive layer clear of the scrollbar
    /// gutter while their boxes keep the full width.</summary>
    internal bool CapChildHitWidth;

    /// <summary>The element the surface hangs under, which is also its
    /// parent: the popup handle and the anchor rect are both read off that
    /// one path.</summary>
    internal int AnchorNode;

    internal IPortalSurfacePainter? Surface;
}

/// <summary>
/// Per-frame storage for element declarations, child ranges, inline sheet
/// patches, portal geometry and retained references. Every buffer is grow-only
/// and reset by index, so a warm frame allocates nothing.
/// </summary>
internal sealed class FrameArena
{
    private static int _nextArenaId;

    // Slot 0 of every buffer is reserved so that a zeroed handle reads as
    // "none".
    private ElementRecord[] _elements = new ElementRecord[256];
    private int _elementCount = 1;
    private int[] _childIndices = new int[512];
    private int _childCount;
    private Poser.UI.ElementSheet[] _patches = new Poser.UI.ElementSheet[64];
    private int _patchCount = 1;
    private PortalRecord[] _portals = new PortalRecord[8];
    private int _portalCount = 1;
    private object?[] _objects = new object?[64];
    private int _objectCount = 1;
    private UiNode[] _scratchNodes = new UiNode[64];
    private int _scratchCount;

    internal static FrameArena? Current { get; set; }

    internal static FrameArena Require() =>
        Current ?? throw new InvalidOperationException(
            "No frame arena is active. UI declarations may only be built inside a UiRoot build callback.");

    /// <summary>Incremented by <see cref="Reset"/>; identifies the frame whose declarations are live.</summary>
    internal int FrameId { get; private set; }

    /// <summary>Distinguishes one root's arena from another's, so a handle
    /// carried across roots is caught instead of indexing a stranger.</summary>
    internal int Id { get; } = Interlocked.Increment(ref _nextArenaId);

    internal int Count => _elementCount;

    internal ref ElementRecord this[int index] => ref _elements[index];

    internal ref PortalRecord Portal(int slot) => ref _portals[slot];

    /// <summary>The inline patch a record named, or null when it named none.
    /// Returned by reference: a patch is a fat struct and the resolver only
    /// reads it.</summary>
    internal ref readonly Poser.UI.ElementSheet Patch(int slot) => ref _patches[slot];

    internal bool HasPatch(int slot) => slot > 0;

    internal UiNode AddElement(in ElementRecord record)
    {
        Ensure(ref _elements, _elementCount + 1);
        _elements[_elementCount] = record;
        return new UiNode(_elementCount++, FrameId, Id);
    }

    internal int AddPatch(in Poser.UI.ElementSheet patch)
    {
        Ensure(ref _patches, _patchCount + 1);
        _patches[_patchCount] = patch;
        return _patchCount++;
    }

    internal int AddPortal(in PortalRecord portal)
    {
        Ensure(ref _portals, _portalCount + 1);
        _portals[_portalCount] = portal;
        return _portalCount++;
    }

    /// <summary>
    /// DEBUG provenance: a handle is an index, so one from another root or
    /// from a previous frame would silently address whatever now lives at
    /// that index. Release builds carry neither the fields nor the check.
    /// </summary>
    [Conditional("DEBUG")]
    internal void ValidateNode(in UiNode node)
    {
#if DEBUG
        if (node.IsNone)
            return;
        if (node.Arena != Id)
            throw new InvalidOperationException("node from another root");
        if (node.Frame != FrameId)
            throw new InvalidOperationException("stale node from a previous frame");
#endif
    }

    /// <summary>
    /// The same provenance rule for a child RANGE, checked where a range is
    /// written into a record. An empty range names no storage, so it is
    /// always valid — that is what keeps <c>default</c> and
    /// <see cref="Poser.UI.UiChildren.Empty"/> usable anywhere.
    /// </summary>
    [Conditional("DEBUG")]
    internal void ValidateChildren(in Poser.UI.UiChildren children)
    {
#if DEBUG
        if (children.Count == 0)
            return;
        if (children.Arena != Id)
            throw new InvalidOperationException("children from another root");
        if (children.Frame != FrameId)
            throw new InvalidOperationException("stale children from a previous frame");
#endif
    }

    /// <summary>The same rule for a reducer token, checked where a control
    /// writes one into its record. A none token names no slot.</summary>
    [Conditional("DEBUG")]
    internal void ValidateEvent(in Poser.UI.UiEvent token)
    {
#if DEBUG
        if (token.IsNone)
            return;
        if (token.Arena != Id)
            throw new InvalidOperationException("event from another root");
        if (token.Frame != FrameId)
            throw new InvalidOperationException("stale event from a previous frame");
#endif
    }

    /// <inheritdoc cref="ValidateEvent(in Poser.UI.UiEvent)"/>
    [Conditional("DEBUG")]
    internal void ValidateEvent<TValue>(in Poser.UI.UiEvent<TValue> token)
    {
#if DEBUG
        if (token.IsNone)
            return;
        if (token.Arena != Id)
            throw new InvalidOperationException("event from another root");
        if (token.Frame != FrameId)
            throw new InvalidOperationException("stale event from a previous frame");
#endif
    }

    /// <summary>
    /// Copies the non-None node indices into the child buffer and returns the
    /// range start; <paramref name="count"/> receives the copied length.
    /// </summary>
    internal int AddChildren(ReadOnlySpan<UiNode> nodes, out int count)
    {
        Ensure(ref _childIndices, _childCount + nodes.Length);
        int start = _childCount;
        for (int i = 0; i < nodes.Length; i++)
        {
            if (nodes[i].IsNone)
                continue;
            _childIndices[_childCount++] = nodes[i].Index;
        }

        count = _childCount - start;
        return start;
    }

    internal UiNode ChildAt(int index) => new(_childIndices[index], FrameId, Id);

    internal int AddObject(object o)
    {
        Ensure(ref _objects, _objectCount + 1);
        _objects[_objectCount] = o;
        return _objectCount++;
    }

    internal object? GetObject(int slot) => slot <= 0 ? null : _objects[slot];

    /// <summary>
    /// Working room for a declaration that must assemble a VARIABLE number of
    /// handles before handing them to <see cref="Poser.UI.UiChildren"/>. The
    /// buffer is grow-only and reset — never freed — by <see cref="Reset"/>, so
    /// a menu of any length costs a warm frame nothing.
    ///
    /// <para>Spans are BUMP-allocated, so two live at once never overlap. A
    /// request that GROWS the buffer reallocates it and invalidates every span
    /// handed out earlier in the frame: consume one before asking for the next.
    /// </para>
    /// </summary>
    internal Span<UiNode> ScratchNodes(int count)
    {
        if (count <= 0)
            return default;
        Ensure(ref _scratchNodes, _scratchCount + count);
        int start = _scratchCount;
        _scratchCount += count;
        // Cleared so an unwritten slot reads as UiNode.None rather than as
        // last frame's element at that index.
        Span<UiNode> span = _scratchNodes.AsSpan(start, count);
        span.Clear();
        return span;
    }

    internal void Reset()
    {
        // Records now hold delegates directly (the listener set) and painters
        // and strings besides, so the USED RANGE is cleared rather than merely
        // rewound: a frame's references must die with it. Every buffer keeps
        // its high-water mark, so the clear is the whole cost.
        Array.Clear(_elements, 0, _elementCount);
        Array.Clear(_patches, 0, _patchCount);
        Array.Clear(_portals, 0, _portalCount);
        Array.Clear(_objects, 0, _objectCount);
        _elementCount = 1;
        _childCount = 0;
        _patchCount = 1;
        _portalCount = 1;
        _objectCount = 1;
        _scratchCount = 0;
        FrameId++;
    }

    private static void Ensure<T>(ref T[] array, int required)
    {
        if (required <= array.Length)
            return;

        int size = array.Length;
        while (size < required)
            size <<= 1;
        Array.Resize(ref array, size);
    }
}
