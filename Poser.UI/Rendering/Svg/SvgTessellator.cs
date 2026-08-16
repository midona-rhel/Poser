using System.Collections.Generic;
using System.Numerics;

namespace Poser.UI;

/// <summary>
/// Polygon tessellation for SVG fills. Convex polygons get the fast path;
/// non-convex go through ear-clipping. Used by <see cref="SvgRenderer"/>.
/// </summary>
internal static class SvgTessellator
{
    public static bool IsConvex(IReadOnlyList<Vector2> p)
    {
        if (p.Count < 3) return true;
        int sign = 0;
        for (int i = 0; i < p.Count; i++)
        {
            var a = p[i];
            var b = p[(i + 1) % p.Count];
            var c = p[(i + 2) % p.Count];
            float cross = (b.X - a.X) * (c.Y - b.Y) - (b.Y - a.Y) * (c.X - b.X);
            int s = cross > 0f ? 1 : (cross < 0f ? -1 : 0);
            if (s != 0)
            {
                if (sign == 0) sign = s;
                else if (sign != s) return false;
            }
        }
        return true;
    }

    /// <summary>
    /// Ear-clipping triangulation. Returns triangle indices into the input
    /// polygon. Caller draws each triangle.
    /// </summary>
    public static List<int> Triangulate(IReadOnlyList<Vector2> poly)
    {
        var tris = new List<int>();
        int n = poly.Count;
        if (n < 3) return tris;

        var indices = new List<int>(n);
        for (int i = 0; i < n; i++) indices.Add(i);

        bool ccw = SignedArea(poly) > 0f;

        int guard = 0;
        while (indices.Count > 3 && guard < n * n)
        {
            guard++;
            bool earFound = false;
            for (int k = 0; k < indices.Count; k++)
            {
                int prev = indices[(k - 1 + indices.Count) % indices.Count];
                int curr = indices[k];
                int next = indices[(k + 1) % indices.Count];

                var a = poly[prev];
                var b = poly[curr];
                var c = poly[next];

                float cross = (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);
                bool isReflex = ccw ? cross < 0f : cross > 0f;
                if (isReflex) continue;

                bool containsAnother = false;
                for (int j = 0; j < indices.Count; j++)
                {
                    if (j == k || j == (k - 1 + indices.Count) % indices.Count || j == (k + 1) % indices.Count) continue;
                    if (PointInTriangle(poly[indices[j]], a, b, c)) { containsAnother = true; break; }
                }
                if (containsAnother) continue;

                tris.Add(prev);
                tris.Add(curr);
                tris.Add(next);
                indices.RemoveAt(k);
                earFound = true;
                break;
            }
            if (!earFound) break;
        }
        if (indices.Count == 3)
        {
            tris.Add(indices[0]);
            tris.Add(indices[1]);
            tris.Add(indices[2]);
        }
        return tris;
    }

    private static float SignedArea(IReadOnlyList<Vector2> p)
    {
        float a = 0f;
        for (int i = 0; i < p.Count; i++)
        {
            var x0 = p[i];
            var x1 = p[(i + 1) % p.Count];
            a += x0.X * x1.Y - x1.X * x0.Y;
        }
        return a * 0.5f;
    }

    private static bool PointInTriangle(Vector2 p, Vector2 a, Vector2 b, Vector2 c)
    {
        float d1 = Sign(p, a, b);
        float d2 = Sign(p, b, c);
        float d3 = Sign(p, c, a);
        bool hasNeg = (d1 < 0f) || (d2 < 0f) || (d3 < 0f);
        bool hasPos = (d1 > 0f) || (d2 > 0f) || (d3 > 0f);
        return !(hasNeg && hasPos);
    }

    private static float Sign(Vector2 p1, Vector2 p2, Vector2 p3)
        => (p1.X - p3.X) * (p2.Y - p3.Y) - (p2.X - p3.X) * (p1.Y - p3.Y);
}
