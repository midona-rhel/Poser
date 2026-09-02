using System.Numerics;
using Dalamud.Bindings.ImGui;
using Poser.UI;

namespace Poser.UI;

public static partial class Crystarium
{
    /// <summary>Where the current draw list stands: pass it to
    /// <see cref="FadeSince"/> after drawing to fade what was drawn.</summary>
    public static int VertexMark() => ImGui.GetWindowDrawList().VtxBuffer.Size;

    /// <summary>Multiplies the alpha of everything drawn since the mark —
    /// the pop-in of search results, a page that has just changed.</summary>
    public static void FadeSince(int mark, float alpha)
    {
        var drawList = ImGui.GetWindowDrawList();
        VertexTransform.ApplyPop(
            drawList, mark, drawList.VtxBuffer.Size, Vector2.Zero, 1f, Vector2.Zero, alpha);
    }
}
