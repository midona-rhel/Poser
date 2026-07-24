using System.Collections.Generic;
using System.Numerics;

namespace Poser.UI;

/// <summary>
/// One sub-path of a parsed SVG path — a list of points (after Bezier flattening
/// and arc decomposition) plus a close flag.
/// </summary>
internal sealed class SvgSubPath
{
    public readonly List<Vector2> Points = new();
    public bool Closed;
}

/// <summary>
/// A parsed SVG path: one or more sub-paths plus solid paint metadata.
/// </summary>
internal sealed class SvgPath
{
    public readonly List<SvgSubPath> SubPaths = new();
    public Vector4? Fill;
    public Vector4? Stroke;
    public float StrokeWidth = 1f;
    public bool EvenOddFill; // false = nonzero winding rule
}
