using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;

namespace Poser.UI;

/// <summary>
/// Parses an SVG path "d" attribute into a list of <see cref="SvgSubPath"/>s,
/// flattening Béziers + arcs into line segments at a target chord error.
/// Supports: M m L l H h V v Z z C c S s Q q T t A a.
/// </summary>
internal static class SvgPathParser
{
    public static List<SvgSubPath> Parse(string d, float chordErrorPx = 0.5f)
    {
        var sub = new List<SvgSubPath>();
        if (string.IsNullOrWhiteSpace(d)) return sub;

        var tokens = Tokenize(d);
        var cur = new Vector2();
        var startOfSub = new Vector2();
        var lastCubic = new Vector2();
        var lastQuadratic = new Vector2();
        bool hasLastCubic = false;
        bool hasLastQuadratic = false;
        char? lastCmd = null;

        SvgSubPath? current = null;

        int i = 0;
        while (i < tokens.Count)
        {
            char cmd;
            if (char.IsLetter(tokens[i].Letter))
            {
                cmd = tokens[i].Letter;
                i++;
            }
            else
            {
                // Implicit repeat of previous command.
                if (lastCmd == null) { i++; continue; }
                cmd = lastCmd.Value;
                // M followed by implicit args treats as L.
                if (cmd == 'M') cmd = 'L';
                else if (cmd == 'm') cmd = 'l';
            }
            lastCmd = cmd;

            switch (cmd)
            {
                case 'M': case 'm':
                {
                    var pt = ReadVec(tokens, ref i);
                    if (cmd == 'm' && current != null) pt += cur;
                    cur = pt;
                    current = new SvgSubPath();
                    current.Points.Add(cur);
                    sub.Add(current);
                    startOfSub = cur;
                    hasLastCubic = false; hasLastQuadratic = false;
                    break;
                }
                case 'L': case 'l':
                {
                    var pt = ReadVec(tokens, ref i);
                    if (cmd == 'l') pt += cur;
                    current ??= NewSub(sub, cur);
                    current.Points.Add(pt);
                    cur = pt;
                    hasLastCubic = false; hasLastQuadratic = false;
                    break;
                }
                case 'H': case 'h':
                {
                    float x = ReadFloat(tokens, ref i);
                    if (cmd == 'h') x += cur.X;
                    var pt = new Vector2(x, cur.Y);
                    current ??= NewSub(sub, cur);
                    current.Points.Add(pt);
                    cur = pt;
                    hasLastCubic = false; hasLastQuadratic = false;
                    break;
                }
                case 'V': case 'v':
                {
                    float y = ReadFloat(tokens, ref i);
                    if (cmd == 'v') y += cur.Y;
                    var pt = new Vector2(cur.X, y);
                    current ??= NewSub(sub, cur);
                    current.Points.Add(pt);
                    cur = pt;
                    hasLastCubic = false; hasLastQuadratic = false;
                    break;
                }
                case 'Z': case 'z':
                {
                    if (current != null && current.Points.Count > 0)
                    {
                        current.Closed = true;
                        cur = startOfSub;
                    }
                    hasLastCubic = false; hasLastQuadratic = false;
                    break;
                }
                case 'C': case 'c':
                {
                    var c1 = ReadVec(tokens, ref i);
                    var c2 = ReadVec(tokens, ref i);
                    var p  = ReadVec(tokens, ref i);
                    if (cmd == 'c') { c1 += cur; c2 += cur; p += cur; }
                    current ??= NewSub(sub, cur);
                    FlattenCubic(current.Points, cur, c1, c2, p, chordErrorPx);
                    lastCubic = c2;
                    hasLastCubic = true; hasLastQuadratic = false;
                    cur = p;
                    break;
                }
                case 'S': case 's':
                {
                    var c2 = ReadVec(tokens, ref i);
                    var p  = ReadVec(tokens, ref i);
                    if (cmd == 's') { c2 += cur; p += cur; }
                    var c1 = hasLastCubic ? cur + (cur - lastCubic) : cur;
                    current ??= NewSub(sub, cur);
                    FlattenCubic(current.Points, cur, c1, c2, p, chordErrorPx);
                    lastCubic = c2;
                    hasLastCubic = true; hasLastQuadratic = false;
                    cur = p;
                    break;
                }
                case 'Q': case 'q':
                {
                    var c1 = ReadVec(tokens, ref i);
                    var p  = ReadVec(tokens, ref i);
                    if (cmd == 'q') { c1 += cur; p += cur; }
                    current ??= NewSub(sub, cur);
                    FlattenQuadratic(current.Points, cur, c1, p, chordErrorPx);
                    lastQuadratic = c1;
                    hasLastQuadratic = true; hasLastCubic = false;
                    cur = p;
                    break;
                }
                case 'T': case 't':
                {
                    var p = ReadVec(tokens, ref i);
                    if (cmd == 't') p += cur;
                    var c1 = hasLastQuadratic ? cur + (cur - lastQuadratic) : cur;
                    current ??= NewSub(sub, cur);
                    FlattenQuadratic(current.Points, cur, c1, p, chordErrorPx);
                    lastQuadratic = c1;
                    hasLastQuadratic = true; hasLastCubic = false;
                    cur = p;
                    break;
                }
                case 'A': case 'a':
                {
                    float rx     = ReadFloat(tokens, ref i);
                    float ry     = ReadFloat(tokens, ref i);
                    float xRot   = ReadFloat(tokens, ref i);
                    float largeArcFlag = ReadFloat(tokens, ref i);
                    float sweepFlag    = ReadFloat(tokens, ref i);
                    var p = ReadVec(tokens, ref i);
                    if (cmd == 'a') p += cur;
                    current ??= NewSub(sub, cur);
                    FlattenArc(current.Points, cur, p, rx, ry, xRot * MathF.PI / 180f,
                        largeArcFlag != 0f, sweepFlag != 0f, chordErrorPx);
                    cur = p;
                    hasLastCubic = false; hasLastQuadratic = false;
                    break;
                }
                default:
                    // Unknown — skip.
                    break;
            }
        }
        return sub;
    }

    private static SvgSubPath NewSub(List<SvgSubPath> list, Vector2 start)
    {
        var s = new SvgSubPath();
        s.Points.Add(start);
        list.Add(s);
        return s;
    }

    // --- Bezier flattening (adaptive) ---

    private static void FlattenCubic(List<Vector2> output, Vector2 p0, Vector2 p1, Vector2 p2, Vector2 p3, float tol)
    {
        // De Casteljau subdivision until the segment chord error is below tol.
        var stack = new Stack<(Vector2, Vector2, Vector2, Vector2, int)>();
        stack.Push((p0, p1, p2, p3, 0));
        while (stack.Count > 0)
        {
            var (a, b, c, d, depth) = stack.Pop();
            float flatness = SegmentDistanceMax(a, d, b, c);
            if (flatness < tol || depth > 16)
            {
                output.Add(d);
                continue;
            }
            var ab = (a + b) * 0.5f;
            var bc = (b + c) * 0.5f;
            var cd = (c + d) * 0.5f;
            var abc = (ab + bc) * 0.5f;
            var bcd = (bc + cd) * 0.5f;
            var abcd = (abc + bcd) * 0.5f;
            stack.Push((abcd, bcd, cd, d, depth + 1));
            stack.Push((a, ab, abc, abcd, depth + 1));
        }
    }

    private static void FlattenQuadratic(List<Vector2> output, Vector2 p0, Vector2 p1, Vector2 p2, float tol)
    {
        // Convert to cubic and flatten.
        var c1 = p0 + (p1 - p0) * (2f / 3f);
        var c2 = p2 + (p1 - p2) * (2f / 3f);
        FlattenCubic(output, p0, c1, c2, p2, tol);
    }

    private static float SegmentDistanceMax(Vector2 a, Vector2 d, Vector2 b, Vector2 c)
    {
        // Max perpendicular distance of b/c from the chord a..d.
        float dx = d.X - a.X;
        float dy = d.Y - a.Y;
        float ad = MathF.Sqrt(dx * dx + dy * dy);
        if (ad < 1e-6f)
            return MathF.Max(Vector2.Distance(b, a), Vector2.Distance(c, a));
        float distB = MathF.Abs((b.X - a.X) * dy - (b.Y - a.Y) * dx) / ad;
        float distC = MathF.Abs((c.X - a.X) * dy - (c.Y - a.Y) * dx) / ad;
        return MathF.Max(distB, distC);
    }

    // --- Arc → cubic ---

    private static void FlattenArc(List<Vector2> output, Vector2 p0, Vector2 p1,
        float rx, float ry, float xRot, bool largeArc, bool sweep, float tol)
    {
        if (rx == 0f || ry == 0f) { output.Add(p1); return; }

        // SVG endpoint to center parameterization.
        rx = MathF.Abs(rx);
        ry = MathF.Abs(ry);
        float cosR = MathF.Cos(xRot), sinR = MathF.Sin(xRot);

        var dxy = (p0 - p1) * 0.5f;
        var x1p = cosR * dxy.X + sinR * dxy.Y;
        var y1p = -sinR * dxy.X + cosR * dxy.Y;

        float rx2 = rx * rx, ry2 = ry * ry;
        float x1p2 = x1p * x1p, y1p2 = y1p * y1p;

        float radiiCheck = x1p2 / rx2 + y1p2 / ry2;
        if (radiiCheck > 1f)
        {
            float scale = MathF.Sqrt(radiiCheck);
            rx *= scale; ry *= scale;
            rx2 = rx * rx; ry2 = ry * ry;
        }

        float sign = (largeArc == sweep) ? -1f : 1f;
        float sq = (rx2 * ry2 - rx2 * y1p2 - ry2 * x1p2) / (rx2 * y1p2 + ry2 * x1p2);
        sq = MathF.Max(0f, sq);
        float coef = sign * MathF.Sqrt(sq);
        float cxp = coef * (rx * y1p) / ry;
        float cyp = coef * -(ry * x1p) / rx;

        var center = new Vector2(
            cosR * cxp - sinR * cyp + (p0.X + p1.X) * 0.5f,
            sinR * cxp + cosR * cyp + (p0.Y + p1.Y) * 0.5f);

        float ux = (x1p - cxp) / rx, uy = (y1p - cyp) / ry;
        float vx = (-x1p - cxp) / rx, vy = (-y1p - cyp) / ry;

        float startAngle = MathF.Atan2(uy, ux);
        float deltaSign = (ux * vy - uy * vx) >= 0f ? 1f : -1f;
        float dot = ux * vx + uy * vy;
        dot = MathF.Min(1f, MathF.Max(-1f, dot));
        float deltaAngle = deltaSign * MathF.Acos(dot);
        if (!sweep && deltaAngle > 0f) deltaAngle -= MathF.Tau;
        else if (sweep && deltaAngle < 0f) deltaAngle += MathF.Tau;

        // Sample the arc — number of segments based on sweep size and tol.
        int steps = MathF.Max(8, MathF.Abs(deltaAngle) * MathF.Max(rx, ry) / tol) > 256 ? 256 :
                    (int)MathF.Max(8, MathF.Abs(deltaAngle) * MathF.Max(rx, ry) / tol);
        for (int s = 1; s <= steps; s++)
        {
            float t = s / (float)steps;
            float a = startAngle + deltaAngle * t;
            float ca = MathF.Cos(a), sa = MathF.Sin(a);
            output.Add(new Vector2(
                cosR * (rx * ca) - sinR * (ry * sa) + center.X,
                sinR * (rx * ca) + cosR * (ry * sa) + center.Y));
        }
    }

    // --- Tokenizer ---

    private struct Token
    {
        public char Letter; // 0 if numeric
        public float Number;
    }

    private static List<Token> Tokenize(string d)
    {
        var list = new List<Token>(d.Length / 2);
        int i = 0;
        while (i < d.Length)
        {
            char c = d[i];
            if (char.IsLetter(c))
            {
                list.Add(new Token { Letter = c });
                i++;
            }
            else if (c == ',' || c == ' ' || c == '\n' || c == '\r' || c == '\t')
            {
                i++;
            }
            else
            {
                int start = i;
                if (c == '+' || c == '-') i++;
                while (i < d.Length && (char.IsDigit(d[i]) || d[i] == '.')) i++;
                if (i < d.Length && (d[i] == 'e' || d[i] == 'E'))
                {
                    i++;
                    if (i < d.Length && (d[i] == '+' || d[i] == '-')) i++;
                    while (i < d.Length && char.IsDigit(d[i])) i++;
                }
                if (i == start) { i++; continue; }
                if (float.TryParse(d.AsSpan(start, i - start), NumberStyles.Float, CultureInfo.InvariantCulture, out var n))
                    list.Add(new Token { Number = n });
            }
        }
        return list;
    }

    private static float ReadFloat(List<Token> tokens, ref int i)
    {
        if (i >= tokens.Count) return 0f;
        var t = tokens[i];
        if (t.Letter != 0) return 0f;
        i++;
        return t.Number;
    }

    private static Vector2 ReadVec(List<Token> tokens, ref int i)
        => new(ReadFloat(tokens, ref i), ReadFloat(tokens, ref i));
}
