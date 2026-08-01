using System;
using System.Runtime.CompilerServices;
using Poser.UI.Reactive;

namespace Poser.UI;

/// <summary>
/// A range in the current frame arena's child buffer. Collection expressions
/// bind through <see cref="Create"/>, so <c>children: [a, b]</c> writes arena
/// storage instead of building an array.
/// </summary>
[CollectionBuilder(typeof(UiChildren), nameof(Create))]
public readonly struct UiChildren
{
    internal readonly int Start;
    internal readonly int Count;

    internal UiChildren(int start, int count)
    {
        Start = start;
        Count = count;
    }

    public static UiChildren Empty => default;

    /// <summary>None entries are dropped, so <c>cond ? Child() : UiNode.None</c> reads as a conditional child.</summary>
    public static UiChildren Create(ReadOnlySpan<UiNode> nodes)
    {
        FrameArena arena = FrameArena.Require();
        for (int i = 0; i < nodes.Length; i++)
            arena.ValidateNode(nodes[i]);
        int start = arena.AddChildren(nodes, out int count);
        return new UiChildren(start, count);
    }

    public static implicit operator UiChildren(UiNode single) => Create(new ReadOnlySpan<UiNode>(in single));

    public Enumerator GetEnumerator() => new(this);

    public struct Enumerator
    {
        private readonly int _end;
        private int _index;

        internal Enumerator(UiChildren children)
        {
            _end = children.Start + children.Count;
            _index = children.Start - 1;
        }

        public readonly UiNode Current => FrameArena.Require().ChildAt(_index);

        public bool MoveNext() => ++_index < _end;
    }
}
