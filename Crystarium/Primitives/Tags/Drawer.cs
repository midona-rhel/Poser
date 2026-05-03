using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Poser.UI.Controls;

namespace Poser.UI;

public enum DrawerSide { Left, Right, Top, Bottom }

public static partial class Crystarium
{
    /// <summary>
    /// Side drawer — a panel anchored to one edge of the parent positioning context.
    /// Toggle visibility with <paramref name="open"/>; the content slides in/out
    /// based on transition policy (currently snap; transitions wire-up coming).
    /// </summary>
    public static void Drawer(string id, bool open, DrawerSide side, float size, Action body)
    {
        if (!open) return;

        float scale = PoserUI.Scale;
        float sizePx = size * scale;

        var style = new ElementStyle
        {
            Position = Position.Absolute,
            BackgroundColor = Theme.Color.SurfaceRaised,
            BorderColor = Theme.Color.Border,
            BorderWidth = 1f,
            BoxShadow = Theme.Shadow.Lg,
            Padding = new Spacing(Theme.Spacing.Lg),
        };

        switch (side)
        {
            case DrawerSide.Left:
                style.Top = 0; style.Left = 0; style.Bottom = 0;
                style.Width = Sizing.Fixed(size);
                style.Height = Sizing.Fill;
                break;
            case DrawerSide.Right:
                style.Top = 0; style.Right = 0; style.Bottom = 0;
                style.Width = Sizing.Fixed(size);
                style.Height = Sizing.Fill;
                break;
            case DrawerSide.Top:
                style.Top = 0; style.Left = 0; style.Right = 0;
                style.Height = Sizing.Fixed(size);
                style.Width = Sizing.Fill;
                break;
            case DrawerSide.Bottom:
                style.Bottom = 0; style.Left = 0; style.Right = 0;
                style.Height = Sizing.Fixed(size);
                style.Width = Sizing.Fill;
                break;
        }

        Element(new ElementProps { Id = id, Style = style }, body);
    }
}
