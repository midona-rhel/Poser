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
    /// Apply <paramref name="transform"/> to all vertices added to
    /// <paramref name="drawList"/> between <paramref name="vtxStart"/> (inclusive)
    /// and <paramref name="vtxEnd"/> (exclusive). Pivot is computed from the
    /// element's bounding box.
    /// </summary>
    public static unsafe void Apply(ImDrawListPtr drawList, int vtxStart, int vtxEnd,
        Vector2 boxMin, Vector2 boxMax, in Transform2D transform)
    {
        if (transform.IsIdentity || vtxEnd <= vtxStart) return;

        var size = boxMax - boxMin;
        var pivot = new Vector2(
            boxMin.X + size.X * transform.OriginX,
            boxMin.Y + size.Y * transform.OriginY);

        float cos = MathF.Cos(transform.Rotate);
        float sin = MathF.Sin(transform.Rotate);
        float sx = transform.ScaleX;
        float sy = transform.ScaleY;

        int count = drawList.VtxBuffer.Size;
        if (vtxEnd > count) vtxEnd = count;

        unsafe
        {
            var vtxPtr = (ImDrawVert*)drawList.VtxBuffer.Data;
            for (int i = vtxStart; i < vtxEnd; i++)
            {
                float dx = vtxPtr[i].Pos.X - pivot.X;
                float dy = vtxPtr[i].Pos.Y - pivot.Y;
                float scaledX = dx * sx;
                float scaledY = dy * sy;
                vtxPtr[i].Pos = new Vector2(
                    pivot.X + scaledX * cos - scaledY * sin,
                    pivot.Y + scaledX * sin + scaledY * cos);
            }
        }
    }
}
