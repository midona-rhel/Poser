namespace Poser.UI;

/// <summary>
/// Axis-aligned rectangle in pixels, relative to a caller-defined origin.
/// The v2 core's layout/hit currency — solvers emit these, painters and the
/// hit tree consume them. Pure data; no ImGui.
/// </summary>
public readonly record struct RectF(float X, float Y, float W, float H)
{
    public float Right => X + W;
    public float Bottom => Y + H;

    public bool Contains(float px, float py) => px >= X && px < X + W && py >= Y && py < Y + H;

    public RectF Offset(float dx, float dy) => new(X + dx, Y + dy, W, H);

    /// <summary>Intersection; empty (W/H 0) when disjoint.</summary>
    public RectF Intersect(in RectF other)
    {
        float x0 = System.Math.Max(X, other.X);
        float y0 = System.Math.Max(Y, other.Y);
        float x1 = System.Math.Min(Right, other.Right);
        float y1 = System.Math.Min(Bottom, other.Bottom);
        return x1 <= x0 || y1 <= y0 ? new RectF(x0, y0, 0f, 0f) : new RectF(x0, y0, x1 - x0, y1 - y0);
    }
}
