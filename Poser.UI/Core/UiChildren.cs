using System;
using System.Runtime.CompilerServices;
using Poser.UI.Reactive;

namespace Poser.UI;

/// <summary>
/// A range in the current frame arena's child buffer. Collection expressions
/// bind through <see cref="Create"/>, so <c>children: [a, b]</c> writes arena
/// storage instead of building an array. Under DEBUG a range carries the
/// arena and frame that minted it, exactly as <see cref="UiNode"/> does: an
/// empty range names nothing and is therefore always valid.
/// </summary>
[CollectionBuilder(typeof(UiChildren), nameof(Create))]
public readonly struct UiChildren
{
    internal readonly int Start;
    internal readonly int Count;
#if DEBUG
    internal readonly int Frame;
    internal readonly int Arena;
#endif

    internal UiChildren(int start, int count, int frame, int arena)
    {
        Start = start;
        Count = count;
#if DEBUG
        Frame = frame;
        Arena = arena;
#else
        _ = frame;
        _ = arena;
#endif
    }

    public static UiChildren Empty => default;

    /// <summary>None entries are dropped, so <c>cond ? Child() : UiNode.None</c> reads as a conditional child.</summary>
    public static UiChildren Create(ReadOnlySpan<UiNode> nodes)
    {
        FrameArena arena = FrameArena.Require();
        for (int i = 0; i < nodes.Length; i++)
            arena.ValidateNode(nodes[i]);
        int start = arena.AddChildren(nodes, out int count);
        return new UiChildren(start, count, arena.FrameId, arena.Id);
    }

    public static implicit operator UiChildren(UiNode single) => Create(new ReadOnlySpan<UiNode>(in single));

    // Deleting the enumerator is not an option: [CollectionBuilder] binds
    // collection expressions only on an enumerable type. Validating here
    // closes the one consumption path that skipped provenance.
    public Enumerator GetEnumerator()
    {
        FrameArena arena = FrameArena.Require();
        arena.ValidateChildren(this);
        return new(this);
    }

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
