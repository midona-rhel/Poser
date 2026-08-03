using System;
using System.Numerics;
using Poser.UI.Reactive;

namespace Poser.UI;

/// <summary>Which corners a box rounds. A stylesheet radius is ONE number for
/// four corners, which is the whole of what a window chassis cannot say: a
/// panel rounds only the corners it actually meets the window edge at, and is
/// square everywhere it meets a sibling.</summary>
[Flags]
public enum UiCorners : byte
{
    None = 0,
    TopLeft = 1,
    TopRight = 2,
    BottomLeft = 4,
    BottomRight = 8,
    Top = TopLeft | TopRight,
    Bottom = BottomLeft | BottomRight,
    All = Top | Bottom,
}

/// <summary>
/// A chassis panel: a translucent surface fill rounded on the corners it names
/// and square on the rest. It is a container in every other respect — flow,
/// padding and children are the ordinary sheet's — so the shell's titlebar
/// cells, its sidebar and its rail are declared exactly like any other box.
///
/// <para>DECORATIVE BY CONSTRUCTION: the fill reserves nothing, or a panel
/// would take the hover of every control standing on it.</para>
/// </summary>
public readonly record struct Chassis
{
    /// <summary>The surface colour. Composited over whatever is already
    /// beneath — a chassis adds a fill, it does not replace one.</summary>
    public required Vector4 Fill { get; init; }

    /// <summary>Logical corner radius; 0 is a square box.</summary>
    public float Radius { get; init; }

    public UiCorners Corners { get; init; }

    public ElementSheet? Style { get; init; }

    public UiChildren Children { get; init; }

    public UiKey Key { get; init; }

    /// <summary>A single child needs no collection: user-defined
    /// conversions do not chain, so the one-child form is stated.</summary>
    public static implicit operator UiChildren(Chassis panel) => (UiNode)panel;

    public static implicit operator UiNode(Chassis panel) => new Element
    {
        Style = (panel.Style ?? default) with
        {
            Colors = (panel.Style?.Colors ?? default) with { Fill = panel.Fill },
            Shape = (panel.Style?.Shape ?? default) with { Radius = panel.Radius },
        },
        Painter = ChassisPainter.For(panel.Corners),
        Children = panel.Children,
        Key = panel.Key,
    };
}
