using System;
using System.Collections.Generic;
using System.Numerics;

namespace Poser.UI;

/// <summary>
/// Derives the FROM-FILE badge from any glyph: every stroke is clipped out
/// of the bottom-right badge box — the plus's extent grown by one stroke
/// width of margin — and the plus itself is appended. Tabler's own "-plus"
/// convention (user-plus, message-plus), computed for glyphs Tabler never
/// shipped a plus twin for, so all from-file rows derive instead of seven
/// hand-forged icons drifting apart (ruled 2026-08-31).
/// </summary>
internal static class SvgCornerPlus
{
    /// <summary>The plus's stroke run in the 24-grid: Tabler's own badge
    /// seat (their user-plus draws exactly M16 19h6 / M19 16v6).</summary>
    private const float PlusMin = 16f;
    private const float PlusMax = 22f;
    private const float PlusMid = 19f;

    /// <summary>One stroke width of margin around the plus.</summary>
    private const float Margin = 2f;

    private static readonly Vector2 BoxMin = new(PlusMin - Margin, PlusMin - Margin);
    private static readonly Vector2 BoxMax = new(25f, 25f);

    internal static void Apply(
        List<SvgSubPath> source,
        List<SvgSubPath> clipped,
        out bool touched)
    {
        touched = false;
        foreach (var sub in source)
        {
            var points = sub.Points;
            if (points.Count < 2)
                continue;

            // A closed ring unrolls to an open run with the wrap segment
            // appended; if nothing gets cut it closes again below.
            int segments = sub.Closed ? points.Count : points.Count - 1;
            var run = new List<Vector2>();
            bool cut = false;

            void Flush()
            {
                if (run.Count > 1)
                {
                    var kept = new SvgSubPath();
                    kept.Points.AddRange(run);
                    clipped.Add(kept);
                }
                run = new List<Vector2>();
            }

            for (int i = 0; i < segments; i++)
            {
                var a = points[i];
                var b = points[(i + 1) % points.Count];
                if (!SegmentSpan(a, b, out float tIn, out float tOut))
                {
                    if (run.Count == 0)
                        run.Add(a);
                    run.Add(b);
                    continue;
                }
                cut = true;
                if (tIn > 0f)
                {
                    if (run.Count == 0)
                        run.Add(a);
                    run.Add(Vector2.Lerp(a, b, tIn));
                }
                Flush();
                if (tOut < 1f)
                {
                    run.Add(Vector2.Lerp(a, b, tOut));
                    run.Add(b);
                }
            }
            Flush();

            if (!cut && sub.Closed && clipped.Count > 0)
                // The whole ring survived: restore its closure so joins
                // render as the original drew them. The unroll above added
                // it as ONE open run ending where it began.
                clipped[^1].Closed = true;
            touched |= cut;
        }
    }

    /// <summary>The plus's two strokes.</summary>
    internal static IEnumerable<SvgSubPath> PlusStrokes()
    {
        var horizontal = new SvgSubPath();
        horizontal.Points.Add(new Vector2(PlusMin, PlusMid));
        horizontal.Points.Add(new Vector2(PlusMax, PlusMid));
        yield return horizontal;
        var vertical = new SvgSubPath();
        vertical.Points.Add(new Vector2(PlusMid, PlusMin));
        vertical.Points.Add(new Vector2(PlusMid, PlusMax));
        yield return vertical;
    }

    /// <summary>Liang-Barsky: the [tIn, tOut] span of the segment inside
    /// the badge box; false when it never enters.</summary>
    private static bool SegmentSpan(
        Vector2 a, Vector2 b, out float tIn, out float tOut)
    {
        tIn = 0f;
        tOut = 1f;
        var d = b - a;
        Span<float> p = [-d.X, d.X, -d.Y, d.Y];
        Span<float> q =
        [
            a.X - BoxMin.X, BoxMax.X - a.X,
            a.Y - BoxMin.Y, BoxMax.Y - a.Y,
        ];
        for (int i = 0; i < 4; i++)
        {
            if (p[i] == 0f)
            {
                if (q[i] < 0f)
                    return false;
                continue;
            }
            float t = q[i] / p[i];
            if (p[i] < 0f)
            {
                if (t > tOut)
                    return false;
                if (t > tIn)
                    tIn = t;
            }
            else
            {
                if (t < tIn)
                    return false;
                if (t < tOut)
                    tOut = t;
            }
        }
        return tIn < tOut;
    }
}
