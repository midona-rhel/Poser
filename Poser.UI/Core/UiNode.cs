namespace Poser.UI.Reactive;

/// <summary>
/// Opaque handle to one element in the current frame arena. Index 0 is the
/// reserved arena slot and therefore means "no node"; a default
/// <see cref="UiNode"/> is skipped wherever children are collected.
/// </summary>
public readonly struct UiNode
{
    internal readonly int Index;

    internal UiNode(int index) => Index = index;

    public static UiNode None => default;

    internal bool IsNone => Index == 0;
}
