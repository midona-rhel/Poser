using System.Collections.Generic;
using Dalamud.Bindings.ImGui;

namespace Poser.UI;

/// <summary>
/// Per-nesting-depth pool of native ImDrawListSplitters for chrome containers.
///
/// <para>ImGui's built-in <c>drawList.ChannelsSplit</c> uses the draw list's single
/// internal splitter and CANNOT nest: a chrome container inside another chrome
/// container (Card → Badge) silently corrupts the command buffer in release builds
/// (asserts are compiled out).
/// Private splitter instances nest fine — the same pattern ImGui tables use.</para>
///
/// <para>Rent/Return must bracket like a stack. Native splitters are small, pooled
/// per nesting depth, and live for the process lifetime.</para>
/// </summary>
internal static class SplitterPool
{
    [System.ThreadStatic] private static List<ImDrawListSplitterPtr>? _pool;
    [System.ThreadStatic] private static int _depth;

    public static ImDrawListSplitterPtr Rent()
    {
        _pool ??= new List<ImDrawListSplitterPtr>();
        if (_depth == _pool.Count)
            _pool.Add(ImGui.ImDrawListSplitter());
        return _pool[_depth++];
    }

    public static void Return()
    {
        if (_depth > 0) _depth--;
    }
}
