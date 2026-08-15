using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace Poser.UI;

/// <summary>
/// Applies a 2D transform (rotate + scale around a pivot) to every vertex in a
/// captured range of the current draw list. Per-glyph text transform comes free
/// because ImGui already emits one quad per glyph — every quad's vertices fall
/// inside the captured range and rotate / scale uniformly around the same pivot.
/// </summary>
public static class VertexTransform
{
    /// <summary>
    /// Uniform scale about a pivot, plus a translation, plus an alpha
    /// multiply, over a captured vertex range — the composited group
    /// animation a Mantine-style pop needs: chrome, shadow, text, and
    /// badges move and fade as ONE surface instead of a background rect
    /// shrinking under full-size glyphs.
    /// </summary>
    public static unsafe void ApplyPop(ImDrawListPtr drawList, int vtxStart, int vtxEnd,
        Vector2 pivot, float scale, Vector2 translate, float alphaMultiplier)
    {
        int count = drawList.VtxBuffer.Size;
        if (vtxEnd > count) vtxEnd = count;
        if (vtxEnd <= vtxStart) return;
        float alpha = Math.Clamp(alphaMultiplier, 0f, 1f);

        var vtxPtr = (ImDrawVert*)drawList.VtxBuffer.Data;
        for (int i = vtxStart; i < vtxEnd; i++)
        {
            vtxPtr[i].Pos = pivot + (vtxPtr[i].Pos - pivot) * scale + translate;
            uint col = vtxPtr[i].Col;
            uint a = (uint)(((col >> 24) & 0xFF) * alpha);
            vtxPtr[i].Col = (col & 0x00FFFFFF) | (a << 24);
        }
    }
}
