using System;

namespace Poser.UI;

public static partial class Crystarium
{
    /// <summary>
    /// Raised when a slider, well or field commits: the drag released, the
    /// typed value accepted on leaving the field. The value journal seals
    /// its open step here, so a drag is one undo step from press to
    /// release, a typed edit is one step from focus to unfocus, and the
    /// next touch of the same control opens a new step (ruled 2026-09-03).
    /// </summary>
    public static event Action? ValueCommitted;

    /// <summary>The control's own commit callback, then the shared seam.</summary>
    internal static void Commit(Action? onCommit)
    {
        onCommit?.Invoke();
        ValueCommitted?.Invoke();
    }
}
