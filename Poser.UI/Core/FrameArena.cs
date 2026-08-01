using System;
using System.Numerics;

namespace Poser.UI.Reactive;

internal enum ElementKind : byte
{
    Box,
    Text,
    Svg,
    Interactive,
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
    // Paint parameters an Interactive leaf hands to its legacy painter.
    internal byte Variant;
    internal string? Help;
    // Filled by the layout pass in wave B.
    internal Vector2 LogicalSize;
    internal Vector2 LogicalPos;
    internal bool Disabled;
}

/// <summary>
/// Per-frame storage for element declarations, child ranges, retained event
/// references and small unmanaged payloads. Every buffer is grow-only and
/// reset by index, so a warm frame allocates nothing.
/// </summary>
internal sealed class FrameArena
{
    // Slot 0 of the element and object buffers is reserved so that a zeroed
    // handle (UiNode.None, UiEvent with slot 0) reads as "none".
    private ElementRecord[] _elements = new ElementRecord[256];
    private int _elementCount = 1;
    private int[] _childIndices = new int[512];
    private int _childCount;
    private object?[] _objects = new object?[64];
    private int _objectCount = 1;
    private byte[] _values = new byte[512];
    private int _valueCount;

    internal static FrameArena? Current { get; set; }

    internal static FrameArena Require() =>
        Current ?? throw new InvalidOperationException(
            "No frame arena is active. UI declarations may only be built inside a UiRoot build callback.");

    /// <summary>Incremented by <see cref="Reset"/>; identifies the frame whose declarations are live.</summary>
    internal int FrameId { get; private set; }

    internal int Count => _elementCount;

    internal ref ElementRecord this[int index] => ref _elements[index];

    internal int AddElement(in ElementRecord record)
    {
        Ensure(ref _elements, _elementCount + 1);
        _elements[_elementCount] = record;
        return _elementCount++;
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

    internal UiNode ChildAt(int index) => new(_childIndices[index]);

    internal int AddObject(object o)
    {
        Ensure(ref _objects, _objectCount + 1);
        _objects[_objectCount] = o;
        return _objectCount++;
    }

    internal object? GetObject(int slot) => slot <= 0 ? null : _objects[slot];

    internal Span<byte> AddValue(int bytes, out int offset)
    {
        Ensure(ref _values, _valueCount + bytes);
        offset = _valueCount;
        _valueCount += bytes;
        return _values.AsSpan(offset, bytes);
    }

    internal ReadOnlySpan<byte> GetValue(int offset, int bytes) => _values.AsSpan(offset, bytes);

    internal void Reset()
    {
        // Object slots hold delegates and strings: null them so a frame's
        // references die with it, but keep every buffer at its high-water mark.
        Array.Clear(_objects, 0, _objectCount);
        _elementCount = 1;
        _childCount = 0;
        _objectCount = 1;
        _valueCount = 0;
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
