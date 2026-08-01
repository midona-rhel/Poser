namespace Poser.UI;

/// <summary>
/// Opaque handle to one element in the current frame arena. Index 0 is the
/// reserved arena slot and therefore means "no node"; a default
/// <see cref="UiNode"/> is skipped wherever children are collected. Under
/// DEBUG a handle also carries the arena and frame that minted it, so a
/// stale or foreign index is caught instead of addressing a stranger;
/// release builds carry the index alone.
/// </summary>
public readonly struct UiNode
{
    internal readonly int Index;
#if DEBUG
    internal readonly int Frame;
    internal readonly int Arena;
#endif

    internal UiNode(int index, int frame, int arena)
    {
        Index = index;
#if DEBUG
        Frame = frame;
        Arena = arena;
#else
        _ = frame;
        _ = arena;
#endif
    }

    public static UiNode None => default;

    internal bool IsNone => Index == 0;
}
