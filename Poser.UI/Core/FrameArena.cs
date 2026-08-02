using System;
using System.Diagnostics;
using System.Numerics;
using System.Threading;

namespace Poser.UI.Reactive;

internal enum ElementKind : byte
{
    Box,
    Text,
    Svg,
    Interactive,
    // APPEND ONLY: the interaction path hash mixes this byte, so moving an
    // existing kind would re-identify every element already declared under it.
    Portal,
}

/// <summary>
/// Which input edge dispatches an interactive element, and whether
/// <see cref="ElementRecord.Arg"/> rides along with it. One byte rather than
/// two fields because the edge and the payload are one authored decision: a
/// control that fires on the raw press is a menu, and a menu says WHICH row.
/// </summary>
internal static class DispatchMode
{
    /// <summary>Click-release or Enter, no payload — the button edge.</summary>
    internal const byte Activated = 0;

    /// <summary>The raw press edge, no payload — what a menu trigger opens on.</summary>
    internal const byte Clicked = 1;

    /// <summary>The raw press edge carrying <see cref="ElementRecord.Arg"/> to
    /// an <see cref="Action{T}"/> handler or a valued reducer.</summary>
    internal const byte ClickedWithArg = 2;
}

/// <summary>
/// One tagged frame declaration. Plain mutable fields on purpose: records live
/// in a pooled array and are written by index, never copied per frame.
/// </summary>
internal struct ElementRecord
{
    internal ElementKind Kind;
    internal UiStyle Style;
    // Text label for Text, source name for Svg.
    internal string? Text;
    internal float TextSize;
    internal byte TextWeight;
    internal Vector4 TextColor;
    internal bool HasTextColor;
    internal UiKey Key;
    internal int ChildStart;
    internal int ChildCount;
    // Index into the arena object slots, 0 when the element has no behavior.
    // Reserved for plain Action handlers; component events ride the two int
    // fields below so a UiEvent token never has to be boxed into a slot.
    internal int BehaviorSlot;
    internal int EventScope;
    internal int EventReducer;
    internal int ScopeId;
    // Interactive paint: the arena object slot holding the retained
    // IInteractivePainter (0 = none) plus the single byte of parameter the
    // painter interprets — a variant, a tone, a level. The runtime never
    // reads it, so no element kind is special-cased in the walk.
    internal int PainterSlot;
    internal byte PaintArg;
    // The painter owns the box, so its subtree is clipped to it; the walk
    // pushes the clip once around the whole child traversal.
    internal bool ClipChildren;
    internal string? Help;
    // The dispatch payload: whatever small int the element stands for — a row
    // index on a menu row, a flag on a portal. See DispatchMode.
    internal int Arg;
    internal byte DispatchMode;
    // Menu wiring. A trigger names the portal it opens (0 = none); a portal
    // names the element it hangs under, which is also its PARENT, because the
    // popup handle and the anchor rect are both read off that one path.
    internal int OpensPortalNode;
    internal int AnchorNode;
    // Closing is the ELEMENT's business, not the handler's: the legacy menu
    // closes on every click, including the one that changes nothing.
    internal bool ClosesPortal;
    // Portal box, all logical. A zero width means "as wide as the anchor" —
    // a Fill-sized trigger has no span until the solver grants it.
    internal Vector2 PortalContentSize;
    internal float PortalPadding;
    internal float PortalAnchorCompensation;
    // The scroll viewport's logical height, 0 for a surface whose children do
    // not scroll. Only the height is authored: the viewport's width is the
    // surface's content region, which the popup window itself reports.
    internal float ScrollRegionHeight;
    // Svg: 0 reads as 1. A control-owned glyph opts OUT of currentColor when
    // its tint must come from the icon renderer's own default instead.
    internal float SvgOpacity;
    internal bool SvgInheritsColor;
    // Text: overflow is the run's OWN declaration, never inferred from its
    // width — see TextOverflow. The preview flag is separate and only means
    // anything under Truncate: a cut run offers the full text as a readout.
    internal Poser.UI.TextOverflow TextOverflow;
    internal bool TextPreviewOnClip;
    // Filled by the layout pass in wave B.
    internal Vector2 LogicalSize;
    internal Vector2 LogicalPos;
    internal bool Disabled;
}

/// <summary>
/// Per-frame storage for element declarations, child ranges and retained
/// event references. Every buffer is grow-only and reset by index, so a warm
/// frame allocates nothing.
/// </summary>
internal sealed class FrameArena
{
    private static int _nextArenaId;

    // Slot 0 of the element and object buffers is reserved so that a zeroed
    // handle (UiNode.None, UiEvent with slot 0) reads as "none".
    private ElementRecord[] _elements = new ElementRecord[256];
    private int _elementCount = 1;
    private int[] _childIndices = new int[512];
    private int _childCount;
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

    internal UiNode AddElement(in ElementRecord record)
    {
        Ensure(ref _elements, _elementCount + 1);
        _elements[_elementCount] = record;
        return new UiNode(_elementCount++, FrameId, Id);
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
    /// a menu of any length costs a warm frame nothing; a
    /// <c>stackalloc</c>-with-heap-fallback would allocate the moment the list
    /// got long, which is exactly when it matters.
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
        // Object slots hold delegates and strings: null them so a frame's
        // references die with it, but keep every buffer at its high-water mark.
        // Scratch handles are plain ints, so rewinding the bump cursor is the
        // whole reset.
        Array.Clear(_objects, 0, _objectCount);
        _elementCount = 1;
        _childCount = 0;
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
