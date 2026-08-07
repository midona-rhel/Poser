using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Numerics;
using System.Xml;
using Dalamud.Bindings.ImGui;

namespace Poser.UI;

/// <summary>
/// A parsed SVG document. Use <see cref="Parse"/> to obtain one, then call
/// <see cref="Render"/>.
///
/// <para>Cache the document — parsing isn't free. Re-render every frame.</para>
/// </summary>
public sealed class SvgDocument
{
    private readonly List<SvgPath> Paths = new();
    public Vector2 ViewBoxMin { get; private set; } = Vector2.Zero;
    public Vector2 ViewBoxSize { get; private set; } = new Vector2(100, 100);

    private static int _nextCacheId;

    /// <summary>Process-stable identity for the baked-icon texture cache.
    /// Documents are parsed once and kept, so this is the cheapest key part
    /// there is.</summary>
    internal int CacheId { get; } =
        System.Threading.Interlocked.Increment(ref _nextCacheId);

    /// <summary>Parse SVG XML text.</summary>
    public static SvgDocument Parse(string xml)
    {
        var doc = new SvgDocument();
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore };
        using var reader = XmlReader.Create(new StringReader(xml), settings);
        var transformStack = new Stack<Matrix3x2>();
        transformStack.Push(Matrix3x2.Identity);
        var inheritedFill   = new Stack<Vector4?>(); inheritedFill.Push(new Vector4(0f, 0f, 0f, 1f));
        var inheritedStroke = new Stack<Vector4?>(); inheritedStroke.Push(null);
        var inheritedStrokeWidth = new Stack<float>(); inheritedStrokeWidth.Push(1f);
        // SVG defaults: stroke-linecap="butt", stroke-linejoin="miter".
        var inheritedRoundCaps = new Stack<bool>(); inheritedRoundCaps.Push(false);
        var inheritedRoundJoins = new Stack<bool>(); inheritedRoundJoins.Push(false);

        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.EndElement)
            {
                if (reader.Name == "g")
                {
                    if (transformStack.Count > 1) transformStack.Pop();
                    if (inheritedFill.Count > 1) inheritedFill.Pop();
                    if (inheritedStroke.Count > 1) inheritedStroke.Pop();
                    if (inheritedStrokeWidth.Count > 1) inheritedStrokeWidth.Pop();
                    if (inheritedRoundCaps.Count > 1) inheritedRoundCaps.Pop();
                    if (inheritedRoundJoins.Count > 1) inheritedRoundJoins.Pop();
                }
                continue;
            }
            if (reader.NodeType != XmlNodeType.Element) continue;

            switch (reader.Name)
            {
                case "svg":
                    ParseRoot(doc, reader);
                    // Push the root's fill/stroke/stroke-width onto the inheritance stacks.
                    // Tabler outline icons rely on this — root sets fill="none" stroke="currentColor"
                    // stroke-width="2" and child <path> elements inherit all three.
                    {
                        var rootFill = ParseColorAttr(reader, "fill", inheritedFill.Peek());
                        inheritedFill.Pop();
                        inheritedFill.Push(rootFill);

                        var rootStroke = ParseColorAttr(reader, "stroke", inheritedStroke.Peek());
                        inheritedStroke.Pop();
                        inheritedStroke.Push(rootStroke);

                        var rootSw = ParseFloatAttr(reader, "stroke-width", inheritedStrokeWidth.Peek());
                        inheritedStrokeWidth.Pop();
                        inheritedStrokeWidth.Push(rootSw);

                        var rootCaps = ParseRoundAttr(reader, "stroke-linecap", inheritedRoundCaps.Peek());
                        inheritedRoundCaps.Pop();
                        inheritedRoundCaps.Push(rootCaps);

                        var rootJoins = ParseRoundAttr(reader, "stroke-linejoin", inheritedRoundJoins.Peek());
                        inheritedRoundJoins.Pop();
                        inheritedRoundJoins.Push(rootJoins);
                    }
                    break;
                case "g":
                    ParseGroup(reader, transformStack, inheritedFill, inheritedStroke, inheritedStrokeWidth, inheritedRoundCaps, inheritedRoundJoins);
                    if (reader.IsEmptyElement)
                    {
                        if (transformStack.Count > 1) transformStack.Pop();
                        if (inheritedFill.Count > 1) inheritedFill.Pop();
                        if (inheritedStroke.Count > 1) inheritedStroke.Pop();
                        if (inheritedStrokeWidth.Count > 1) inheritedStrokeWidth.Pop();
                        if (inheritedRoundCaps.Count > 1) inheritedRoundCaps.Pop();
                        if (inheritedRoundJoins.Count > 1) inheritedRoundJoins.Pop();
                    }
                    break;
                case "path":
                    AddPath(doc, ParseAttr(reader, "d"), reader, transformStack, inheritedFill, inheritedStroke, inheritedStrokeWidth, inheritedRoundCaps, inheritedRoundJoins);
                    break;
                case "rect":
                    AddRect(doc, reader, transformStack, inheritedFill, inheritedStroke, inheritedStrokeWidth, inheritedRoundCaps, inheritedRoundJoins);
                    break;
                case "circle":
                    AddCircle(doc, reader, transformStack, inheritedFill, inheritedStroke, inheritedStrokeWidth, inheritedRoundCaps, inheritedRoundJoins);
                    break;
                case "ellipse":
                    AddEllipse(doc, reader, transformStack, inheritedFill, inheritedStroke, inheritedStrokeWidth, inheritedRoundCaps, inheritedRoundJoins);
                    break;
                case "line":
                    AddLine(doc, reader, transformStack, inheritedFill, inheritedStroke, inheritedStrokeWidth, inheritedRoundCaps, inheritedRoundJoins);
                    break;
                case "polyline":
                    AddPolyShape(doc, reader, transformStack, inheritedFill, inheritedStroke, inheritedStrokeWidth, inheritedRoundCaps, inheritedRoundJoins, false);
                    break;
                case "polygon":
                    AddPolyShape(doc, reader, transformStack, inheritedFill, inheritedStroke, inheritedStrokeWidth, inheritedRoundCaps, inheritedRoundJoins, true);
                    break;
            }
        }
        return doc;
    }


    /// <summary>Render the document inside the rect [<paramref name="min"/>..<paramref name="max"/>],
    /// fitting the viewBox uniformly. <paramref name="tint"/> multiplies fill colors.
    /// <paramref name="flipX"/> mirrors geometry inside the SVG viewBox.
    /// <paramref name="strokeWidth"/> (viewBox units) replaces every path's own
    /// stroke width, exactly like the Tabler React <c>stroke</c> prop.</summary>
    public void Render(ImDrawListPtr drawList, Vector2 min, Vector2 max, Vector4? tint = null, bool flipX = false, float? strokeWidth = null)
        => RenderCore(drawList, min, max, tint, flipX, strokeWidth, false);

    internal void RenderComposited(
        ImDrawListPtr drawList, Vector2 min, Vector2 max,
        Vector4? tint = null, bool flipX = false, float? strokeWidth = null,
        float groupOpacity = 1f, Vector4 groupBackground = default)
        => RenderCore(
            drawList, min, max, tint, flipX, strokeWidth, true,
            groupOpacity, groupBackground);

    private void RenderCore(
        ImDrawListPtr drawList, Vector2 min, Vector2 max, Vector4? tint,
        bool flipX, float? strokeWidth, bool compositeStroke,
        float groupOpacity = 1f, Vector4 groupBackground = default)
    {
        if (!Fits(min, max)) return;

        // Icons draw on the WHOLE-PIXEL grid. The bake cache keys on the
        // box's sub-pixel phase, and a dragged window slides that phase
        // continuously — every visible icon became a first-seen key every
        // frame (painted in software, then re-baked, then the full-cache
        // nuke), which Dalamud logged as 150-400ms UiBuilder hitches. A
        // floored box keeps the phase at zero, so movement re-uses the
        // standing bake; the size is preserved exactly.
        var snapped = new Vector2(MathF.Floor(min.X), MathF.Floor(min.Y));
        max = snapped + (max - min);
        min = snapped;

        // Warm path: one cached quad. No geometry, no closure, no
        // per-sub-path point buffers, no per-pixel rect.
        if (SvgIconTextureCache.TryDraw(
                drawList, this, min, max, tint, flipX, strokeWidth,
                groupOpacity, groupBackground))
            return;

        var (toScreen, scale) = Geometry(min, max, flipX);
        SvgRenderer.Render(
            drawList, Paths, toScreen, scale, tint, strokeWidth,
            compositeStroke, groupOpacity, groupBackground);
    }

    /// <summary>The painter's own coverage for this exact draw, as an RGBA8
    /// bitmap. False means the document is not one the mask can express, so
    /// the painter owns it; true with a zero size means it paints nothing.
    /// </summary>
    internal bool TryBakeMask(
        Vector2 min, Vector2 max, Vector4? tint, bool flipX,
        float? strokeWidth, float groupOpacity, Vector4 groupBackground,
        out Vector2 origin, out int width, out int height, out byte[] rgba)
    {
        origin = default;
        width = 0;
        height = 0;
        rgba = [];
        if (!Fits(min, max) || !SvgRenderer.UsesStrokeMask(Paths))
            return false;
        var (toScreen, scale) = Geometry(min, max, flipX);
        SvgStrokeMask.Bake(
            Paths, toScreen, scale, tint, strokeWidth,
            groupOpacity, groupBackground,
            out origin, out width, out height, out rgba);
        return true;
    }

    private bool Fits(Vector2 min, Vector2 max) =>
        max.X - min.X > 0f && max.Y - min.Y > 0f
        && ViewBoxSize.X > 0f && ViewBoxSize.Y > 0f;

    /// <summary>Uniform fit: scale = min(scaleX, scaleY); centered.</summary>
    private (Func<Vector2, Vector2> ToScreen, float Scale) Geometry(
        Vector2 min, Vector2 max, bool flipX)
    {
        var size = max - min;
        float scaleX = size.X / ViewBoxSize.X;
        float scaleY = size.Y / ViewBoxSize.Y;
        float scale = MathF.Min(scaleX, scaleY);
        float renderedW = ViewBoxSize.X * scale;
        float renderedH = ViewBoxSize.Y * scale;
        var origin = min + new Vector2((size.X - renderedW) * 0.5f, (size.Y - renderedH) * 0.5f);

        Vector2 ToScreen(Vector2 svgPt)
        {
            float localX = svgPt.X - ViewBoxMin.X;
            if (flipX)
                localX = ViewBoxSize.X - localX;
            return origin + new Vector2(localX * scale, (svgPt.Y - ViewBoxMin.Y) * scale);
        }

        return (ToScreen, scale);
    }

    // ---------- helpers ----------

    private static void ParseRoot(SvgDocument doc, XmlReader r)
    {
        var viewBox = ParseAttr(r, "viewBox");
        if (!string.IsNullOrEmpty(viewBox))
        {
            var parts = viewBox.Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 4
                && float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var vx)
                && float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var vy)
                && float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var vw)
                && float.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var vh))
            {
                doc.ViewBoxMin = new Vector2(vx, vy);
                doc.ViewBoxSize = new Vector2(vw, vh);
                return;
            }
        }

        var w = ParseFloatAttr(r, "width", 100f);
        var h = ParseFloatAttr(r, "height", 100f);
        doc.ViewBoxSize = new Vector2(w, h);
    }

    private static void ParseGroup(XmlReader r,
        Stack<Matrix3x2> transformStack,
        Stack<Vector4?> fillStack,
        Stack<Vector4?> strokeStack,
        Stack<float> strokeWidthStack,
        Stack<bool> capsStack,
        Stack<bool> joinsStack)
    {
        var tx = ParseTransform(ParseAttr(r, "transform"));
        transformStack.Push(transformStack.Peek() * tx);

        var fill = ParseColorAttr(r, "fill", fillStack.Peek());
        fillStack.Push(fill);

        var stroke = ParseColorAttr(r, "stroke", strokeStack.Peek());
        strokeStack.Push(stroke);

        var sw = ParseFloatAttr(r, "stroke-width", strokeWidthStack.Peek());
        strokeWidthStack.Push(sw);

        capsStack.Push(ParseRoundAttr(r, "stroke-linecap", capsStack.Peek()));
        joinsStack.Push(ParseRoundAttr(r, "stroke-linejoin", joinsStack.Peek()));
    }

    private static void AddPath(SvgDocument doc, string d, XmlReader r,
        Stack<Matrix3x2> transformStack,
        Stack<Vector4?> fillStack,
        Stack<Vector4?> strokeStack,
        Stack<float> strokeWidthStack,
        Stack<bool> capsStack,
        Stack<bool> joinsStack)
    {
        var subPaths = SvgPathParser.Parse(d);
        if (subPaths.Count == 0) return;

        var matrix = transformStack.Peek() * ParseTransform(ParseAttr(r, "transform"));
        ApplyMatrix(subPaths, matrix);

        var path = new SvgPath
        {
            Fill = ParseColorAttr(r, "fill", fillStack.Peek()),
            Stroke = ParseColorAttr(r, "stroke", strokeStack.Peek()),
            StrokeWidth = ParseFloatAttr(r, "stroke-width", strokeWidthStack.Peek()),
            RoundCaps = ParseRoundAttr(r, "stroke-linecap", capsStack.Peek()),
            RoundJoins = ParseRoundAttr(r, "stroke-linejoin", joinsStack.Peek()),
            EvenOddFill = ParseAttr(r, "fill-rule") == "evenodd",
        };
        path.SubPaths.AddRange(subPaths);
        doc.Paths.Add(path);
    }

    private static void AddRect(SvgDocument doc, XmlReader r,
        Stack<Matrix3x2> transformStack,
        Stack<Vector4?> fillStack,
        Stack<Vector4?> strokeStack,
        Stack<float> strokeWidthStack,
        Stack<bool> capsStack,
        Stack<bool> joinsStack)
    {
        float x = ParseFloatAttr(r, "x", 0f);
        float y = ParseFloatAttr(r, "y", 0f);
        float w = ParseFloatAttr(r, "width", 0f);
        float h = ParseFloatAttr(r, "height", 0f);

        var sub = new SvgSubPath();
        sub.Points.Add(new Vector2(x, y));
        sub.Points.Add(new Vector2(x + w, y));
        sub.Points.Add(new Vector2(x + w, y + h));
        sub.Points.Add(new Vector2(x, y + h));
        sub.Closed = true;
        var subList = new List<SvgSubPath> { sub };

        var matrix = transformStack.Peek() * ParseTransform(ParseAttr(r, "transform"));
        ApplyMatrix(subList, matrix);

        var path = new SvgPath
        {
            Fill = ParseColorAttr(r, "fill", fillStack.Peek()),
            Stroke = ParseColorAttr(r, "stroke", strokeStack.Peek()),
            StrokeWidth = ParseFloatAttr(r, "stroke-width", strokeWidthStack.Peek()),
            RoundCaps = ParseRoundAttr(r, "stroke-linecap", capsStack.Peek()),
            RoundJoins = ParseRoundAttr(r, "stroke-linejoin", joinsStack.Peek()),
        };
        path.SubPaths.AddRange(subList);
        doc.Paths.Add(path);
    }

    private static void AddCircle(SvgDocument doc, XmlReader r,
        Stack<Matrix3x2> transformStack,
        Stack<Vector4?> fillStack,
        Stack<Vector4?> strokeStack,
        Stack<float> strokeWidthStack,
        Stack<bool> capsStack,
        Stack<bool> joinsStack)
    {
        float cx = ParseFloatAttr(r, "cx", 0f);
        float cy = ParseFloatAttr(r, "cy", 0f);
        float radius = ParseFloatAttr(r, "r", 0f);
        if (radius <= 0f) return;
        AddEllipseShape(doc, r, transformStack, fillStack, strokeStack, strokeWidthStack, capsStack, joinsStack, cx, cy, radius, radius);
    }

    private static void AddEllipse(SvgDocument doc, XmlReader r,
        Stack<Matrix3x2> transformStack,
        Stack<Vector4?> fillStack,
        Stack<Vector4?> strokeStack,
        Stack<float> strokeWidthStack,
        Stack<bool> capsStack,
        Stack<bool> joinsStack)
    {
        float cx = ParseFloatAttr(r, "cx", 0f);
        float cy = ParseFloatAttr(r, "cy", 0f);
        float rx = ParseFloatAttr(r, "rx", 0f);
        float ry = ParseFloatAttr(r, "ry", 0f);
        AddEllipseShape(doc, r, transformStack, fillStack, strokeStack, strokeWidthStack, capsStack, joinsStack, cx, cy, rx, ry);
    }

    private static void AddEllipseShape(SvgDocument doc, XmlReader r,
        Stack<Matrix3x2> transformStack, Stack<Vector4?> fillStack, Stack<Vector4?> strokeStack, Stack<float> strokeWidthStack, Stack<bool> capsStack, Stack<bool> joinsStack,
        float cx, float cy, float rx, float ry)
    {
        const int segments = 64;
        var sub = new SvgSubPath();
        for (int i = 0; i < segments; i++)
        {
            float t = (i / (float)segments) * MathF.Tau;
            sub.Points.Add(new Vector2(cx + MathF.Cos(t) * rx, cy + MathF.Sin(t) * ry));
        }
        sub.Closed = true;
        var subList = new List<SvgSubPath> { sub };

        var matrix = transformStack.Peek() * ParseTransform(ParseAttr(r, "transform"));
        ApplyMatrix(subList, matrix);

        var path = new SvgPath
        {
            Fill = ParseColorAttr(r, "fill", fillStack.Peek()),
            Stroke = ParseColorAttr(r, "stroke", strokeStack.Peek()),
            StrokeWidth = ParseFloatAttr(r, "stroke-width", strokeWidthStack.Peek()),
            RoundCaps = ParseRoundAttr(r, "stroke-linecap", capsStack.Peek()),
            RoundJoins = ParseRoundAttr(r, "stroke-linejoin", joinsStack.Peek()),
        };
        path.SubPaths.AddRange(subList);
        doc.Paths.Add(path);
    }

    private static void AddLine(SvgDocument doc, XmlReader r,
        Stack<Matrix3x2> transformStack, Stack<Vector4?> fillStack, Stack<Vector4?> strokeStack, Stack<float> strokeWidthStack, Stack<bool> capsStack, Stack<bool> joinsStack)
    {
        float x1 = ParseFloatAttr(r, "x1", 0f);
        float y1 = ParseFloatAttr(r, "y1", 0f);
        float x2 = ParseFloatAttr(r, "x2", 0f);
        float y2 = ParseFloatAttr(r, "y2", 0f);

        var sub = new SvgSubPath();
        sub.Points.Add(new Vector2(x1, y1));
        sub.Points.Add(new Vector2(x2, y2));
        var subList = new List<SvgSubPath> { sub };

        var matrix = transformStack.Peek() * ParseTransform(ParseAttr(r, "transform"));
        ApplyMatrix(subList, matrix);

        var path = new SvgPath
        {
            Fill = null,
            Stroke = ParseColorAttr(r, "stroke", strokeStack.Peek()),
            StrokeWidth = ParseFloatAttr(r, "stroke-width", strokeWidthStack.Peek()),
            RoundCaps = ParseRoundAttr(r, "stroke-linecap", capsStack.Peek()),
            RoundJoins = ParseRoundAttr(r, "stroke-linejoin", joinsStack.Peek()),
        };
        path.SubPaths.AddRange(subList);
        doc.Paths.Add(path);
    }

    private static void AddPolyShape(SvgDocument doc, XmlReader r,
        Stack<Matrix3x2> transformStack, Stack<Vector4?> fillStack, Stack<Vector4?> strokeStack, Stack<float> strokeWidthStack, Stack<bool> capsStack, Stack<bool> joinsStack,
        bool closed)
    {
        var pts = ParseAttr(r, "points");
        var sub = new SvgSubPath();
        var parts = pts.Split(new[] { ' ', ',', '\n', '\r', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i + 1 < parts.Length; i += 2)
        {
            if (float.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out var x)
             && float.TryParse(parts[i + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out var y))
            {
                sub.Points.Add(new Vector2(x, y));
            }
        }
        sub.Closed = closed;
        var subList = new List<SvgSubPath> { sub };

        var matrix = transformStack.Peek() * ParseTransform(ParseAttr(r, "transform"));
        ApplyMatrix(subList, matrix);

        var path = new SvgPath
        {
            Fill = closed ? ParseColorAttr(r, "fill", fillStack.Peek()) : null,
            Stroke = ParseColorAttr(r, "stroke", strokeStack.Peek()),
            StrokeWidth = ParseFloatAttr(r, "stroke-width", strokeWidthStack.Peek()),
            RoundCaps = ParseRoundAttr(r, "stroke-linecap", capsStack.Peek()),
            RoundJoins = ParseRoundAttr(r, "stroke-linejoin", joinsStack.Peek()),
        };
        path.SubPaths.AddRange(subList);
        doc.Paths.Add(path);
    }

    // ---------- attribute parsers ----------

    private static string ParseAttr(XmlReader r, string name)
    {
        var saved = r.MoveToAttribute(name);
        var value = saved ? r.Value : string.Empty;
        r.MoveToElement();
        return value;
    }

    private static float ParseFloatAttr(XmlReader r, string name, float fallback)
    {
        var s = ParseAttr(r, name);
        if (string.IsNullOrEmpty(s)) return fallback;
        // Strip "px", "%" etc — accept the leading number.
        int i = 0;
        if (i < s.Length && (s[i] == '+' || s[i] == '-')) i++;
        while (i < s.Length && (char.IsDigit(s[i]) || s[i] == '.')) i++;
        if (i < s.Length && (s[i] == 'e' || s[i] == 'E'))
        {
            i++;
            if (i < s.Length && (s[i] == '+' || s[i] == '-')) i++;
            while (i < s.Length && char.IsDigit(s[i])) i++;
        }
        if (i == 0) return fallback;
        return float.TryParse(s.AsSpan(0, i), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : fallback;
    }

    /// <summary>"round" toggles the flag on; any other explicit value
    /// (butt, square, miter, bevel) toggles it off; absent inherits.</summary>
    private static bool ParseRoundAttr(XmlReader r, string name, bool inherited)
    {
        var value = ParseAttr(r, name);
        if (string.IsNullOrEmpty(value)) return inherited;
        return value == "round";
    }

    private static Vector4? ParseColorAttr(XmlReader r, string name, Vector4? inherited)
    {
        var s = ParseAttr(r, name);
        return ParseColorString(s, inherited);
    }

    private static Vector4? ParseColorAttr(XmlReader r, string name, Vector4 inherited)
        => ParseColorAttr(r, name, (Vector4?)inherited);

    private static Vector4? ParseColorString(string s, Vector4? inherited)
    {
        if (string.IsNullOrEmpty(s)) return inherited;
        if (s == "none") return null;
        // CSS-shaped: "currentColor" means "use whatever color the renderer is tinting with".
        // Returning white here is the multiplicative-identity for tint — Multiply(white, tint) = tint.
        if (s == "currentColor") return new Vector4(1f, 1f, 1f, 1f);
        if (s.StartsWith("url(")) return inherited;
        if (s.StartsWith("#"))
        {
            return ParseHexColor(s);
        }
        if (s.StartsWith("rgb(") || s.StartsWith("rgba("))
        {
            int o = s.IndexOf('(') + 1;
            int e = s.IndexOf(')');
            var inner = s.Substring(o, e - o);
            var parts = inner.Split(',');
            if (parts.Length >= 3
             && float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var rr)
             && float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var gg)
             && float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var bb))
            {
                float a = 1f;
                if (parts.Length >= 4) float.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out a);
                return new Vector4(rr / 255f, gg / 255f, bb / 255f, a);
            }
        }
        // Named colors — minimal subset.
        return s.ToLowerInvariant() switch
        {
            "black" => new Vector4(0, 0, 0, 1),
            "white" => new Vector4(1, 1, 1, 1),
            "red" => new Vector4(1, 0, 0, 1),
            "green" => new Vector4(0, 0.5f, 0, 1),
            "blue" => new Vector4(0, 0, 1, 1),
            "yellow" => new Vector4(1, 1, 0, 1),
            "transparent" => new Vector4(0, 0, 0, 0),
            _ => inherited,
        };
    }


    private static Vector4? ParseHexColor(string s)
    {
        if (s.Length == 4)
        {
            byte r = (byte)(HexNibble(s[1]) * 17);
            byte g = (byte)(HexNibble(s[2]) * 17);
            byte b = (byte)(HexNibble(s[3]) * 17);
            return new Vector4(r / 255f, g / 255f, b / 255f, 1f);
        }
        if (s.Length == 7)
        {
            byte r = (byte)((HexNibble(s[1]) << 4) | HexNibble(s[2]));
            byte g = (byte)((HexNibble(s[3]) << 4) | HexNibble(s[4]));
            byte b = (byte)((HexNibble(s[5]) << 4) | HexNibble(s[6]));
            return new Vector4(r / 255f, g / 255f, b / 255f, 1f);
        }
        return null;
    }

    private static int HexNibble(char c) => c switch
    {
        >= '0' and <= '9' => c - '0',
        >= 'a' and <= 'f' => c - 'a' + 10,
        >= 'A' and <= 'F' => c - 'A' + 10,
        _ => 0,
    };

    private static Matrix3x2 ParseTransform(string transform)
    {
        if (string.IsNullOrEmpty(transform)) return Matrix3x2.Identity;
        var m = Matrix3x2.Identity;
        int i = 0;
        while (i < transform.Length)
        {
            // Read function name.
            int nameStart = i;
            while (i < transform.Length && char.IsLetter(transform[i])) i++;
            if (i == nameStart) { i++; continue; }
            string name = transform.Substring(nameStart, i - nameStart);
            while (i < transform.Length && transform[i] != '(') i++;
            if (i >= transform.Length) break;
            i++; // skip '('
            int argStart = i;
            while (i < transform.Length && transform[i] != ')') i++;
            string args = transform.Substring(argStart, i - argStart);
            if (i < transform.Length) i++; // skip ')'
            var nums = ParseNumberList(args);

            Matrix3x2 op = name switch
            {
                "translate" => Matrix3x2.CreateTranslation(nums.Count > 0 ? nums[0] : 0f, nums.Count > 1 ? nums[1] : 0f),
                "scale"     => Matrix3x2.CreateScale(nums.Count > 0 ? nums[0] : 1f, nums.Count > 1 ? nums[1] : (nums.Count > 0 ? nums[0] : 1f)),
                "rotate"    => MakeRotate(nums),
                "matrix"    => nums.Count >= 6 ? new Matrix3x2(nums[0], nums[1], nums[2], nums[3], nums[4], nums[5]) : Matrix3x2.Identity,
                _ => Matrix3x2.Identity,
            };
            m = m * op;
        }
        return m;
    }

    private static Matrix3x2 MakeRotate(List<float> nums)
    {
        if (nums.Count < 1) return Matrix3x2.Identity;
        float deg = nums[0];
        float rad = deg * MathF.PI / 180f;
        if (nums.Count >= 3)
        {
            float cx = nums[1], cy = nums[2];
            return Matrix3x2.CreateRotation(rad, new Vector2(cx, cy));
        }
        return Matrix3x2.CreateRotation(rad);
    }

    private static List<float> ParseNumberList(string s)
    {
        var result = new List<float>();
        int i = 0;
        while (i < s.Length)
        {
            while (i < s.Length && (s[i] == ' ' || s[i] == ',' || s[i] == '\n' || s[i] == '\t' || s[i] == '\r')) i++;
            if (i >= s.Length) break;
            int start = i;
            if (s[i] == '+' || s[i] == '-') i++;
            while (i < s.Length && (char.IsDigit(s[i]) || s[i] == '.')) i++;
            if (i < s.Length && (s[i] == 'e' || s[i] == 'E'))
            {
                i++;
                if (i < s.Length && (s[i] == '+' || s[i] == '-')) i++;
                while (i < s.Length && char.IsDigit(s[i])) i++;
            }
            if (i == start) { i++; continue; }
            if (float.TryParse(s.AsSpan(start, i - start), NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
                result.Add(v);
        }
        return result;
    }

    private static void ApplyMatrix(List<SvgSubPath> subPaths, Matrix3x2 m)
    {
        if (m.IsIdentity) return;
        foreach (var sp in subPaths)
        {
            for (int i = 0; i < sp.Points.Count; i++)
                sp.Points[i] = Vector2.Transform(sp.Points[i], m);
        }
    }
}
