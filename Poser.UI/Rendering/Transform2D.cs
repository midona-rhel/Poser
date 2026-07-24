namespace Poser.UI;

/// <summary>
/// CSS-shaped 2D transform: rotate, scale, plus a normalized origin point inside
/// the element's bounding box.
///
/// <para>Origin is in 0..1 space — (0.5, 0.5) is the box center, (0, 0) is
/// top-left. Default origin is the center.</para>
///
/// <para>Rotation is in radians, clockwise. Scale 1.0 = identity.</para>
/// </summary>
public record struct Transform2D
{
    public float OriginX;
    public float OriginY;
    public float Rotate;
    public float ScaleX;
    public float ScaleY;

    /// <summary>Identity transform (no rotation, scale=1, origin=center).</summary>
    public static Transform2D Identity => new() { OriginX = 0.5f, OriginY = 0.5f, Rotate = 0f, ScaleX = 1f, ScaleY = 1f };

    /// <summary>Rotate around the center.</summary>
    public static Transform2D Rotation(float radians)
        => new() { OriginX = 0.5f, OriginY = 0.5f, Rotate = radians, ScaleX = 1f, ScaleY = 1f };

    /// <summary>Uniform scale around the center.</summary>
    public static Transform2D Scale(float s)
        => new() { OriginX = 0.5f, OriginY = 0.5f, Rotate = 0f, ScaleX = s, ScaleY = s };

    public bool IsIdentity =>
        Rotate == 0f && ScaleX == 1f && ScaleY == 1f;

    /// <summary>Field-by-field linear interpolation. Used by Animator.</summary>
    public static Transform2D Lerp(in Transform2D a, in Transform2D b, float t)
        => new()
        {
            OriginX = a.OriginX + (b.OriginX - a.OriginX) * t,
            OriginY = a.OriginY + (b.OriginY - a.OriginY) * t,
            Rotate  = a.Rotate  + (b.Rotate  - a.Rotate)  * t,
            ScaleX  = a.ScaleX  + (b.ScaleX  - a.ScaleX)  * t,
            ScaleY  = a.ScaleY  + (b.ScaleY  - a.ScaleY)  * t,
        };
}
