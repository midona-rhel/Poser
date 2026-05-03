using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace Poser.UI;

public static partial class Crystarium
{
    /// <summary>
    /// Card: padded surface with shadow, rounded corners. Common container for grouped content.
    /// <code>
    ///   Crystarium.Card(() => {
    ///       Crystarium.Heading("Settings");
    ///       Crystarium.Text("Body");
    ///   });
    /// </code>
    /// </summary>
    public static void Card(Action children)
        => Card(default, children);

    public static void Card(StyleClassSet classes, Action children)
    {
        Element(new ElementProps
        {
            Classes = Cls.Card + classes,
            Style = new ElementStyle
            {
                BackgroundColor = Theme.Color.SurfaceRaised,
                BorderColor = Theme.Color.Border,
                BorderWidth = 1,
                BorderRadius = Theme.Radius.Md,
                BoxShadow = Theme.Shadow.Md,
                Padding = new Spacing(Theme.Spacing.Lg),
                Margin = new Spacing(0, 0, Theme.Spacing.Md, 0),
            },
        }, children);
    }

    /// <summary>
    /// Panel: flat surface, no shadow. For embedded grouping.
    /// </summary>
    public static void Panel(Action children) => Panel(default, children);

    public static void Panel(StyleClassSet classes, Action children)
    {
        Element(new ElementProps
        {
            Classes = Cls.Panel + classes,
            Style = new ElementStyle
            {
                BackgroundColor = Theme.Color.SurfaceSunken,
                BorderRadius = Theme.Radius.Sm,
                Padding = new Spacing(Theme.Spacing.Md),
                Margin = new Spacing(0, 0, Theme.Spacing.Sm, 0),
            },
        }, children);
    }

    /// <summary>
    /// Heading: section title text using Theme.Typo.Heading size and bold-ish color.
    /// </summary>
    public static void Heading(string text)
        => Text(text, new TextProps
        {
            Style = new TextStyle
            {
                Color = Theme.Color.Text,
                FontSize = Theme.Typo.Heading,
                Margin = new Spacing(0, 0, Theme.Spacing.Sm, 0),
            },
        });

    /// <summary>
    /// Section: heading + body in a single block, with a thin divider above when not first.
    /// </summary>
    public static void Section(string title, Action body)
    {
        Heading(title);
        body();
    }

    /// <summary>
    /// Tooltip attached to the most recently drawn item (call immediately after a tag).
    /// </summary>
    public static void Tooltip(string text)
    {
        if (ImGui.IsItemHovered() && !string.IsNullOrEmpty(text))
            ImGui.SetTooltip(text);
    }

    /// <summary>
    /// Labeled row: <c>label</c> on the left at <c>labelWidth</c>, <c>input</c> filling the rest.
    /// One-line replacement for the recurring "row { label, input }" pattern.
    /// </summary>
    public static void LabelRow(string label, Action input, float labelWidth = 70f)
    {
        Element(new ElementProps { Classes = Cls.Row }, () =>
        {
            Element(new ElementProps { Style = new ElementStyle { Width = Sizing.Fixed(labelWidth) } }, () =>
            {
                Text(label, new TextProps { Style = new TextStyle { Color = Theme.Color.TextDim } });
            });
            Element(new ElementProps { Style = new ElementStyle { Width = Sizing.Fill } }, input);
        });
    }

    /// <summary>
    /// Spacer: fixed-size empty element for adding gap.
    /// </summary>
    public static void Spacer(float size = 8f)
    {
        Element(new ElementProps
        {
            Style = new ElementStyle { Height = Sizing.Fixed(size), Width = Sizing.Fill },
        });
    }

    /// <summary>
    /// Divider: full-width horizontal line.
    /// </summary>
    public static void Divider()
    {
        Element(new ElementProps
        {
            Classes = Cls.Separator,
            Style = new ElementStyle
            {
                Width = Sizing.Fill,
                Height = Sizing.Fixed(1),
                BackgroundColor = Theme.Color.Border with { W = Theme.Color.Border.W * 0.5f },
                Margin = new Spacing(Theme.Spacing.Sm, 0, Theme.Spacing.Md, 0),
            },
        });
    }

    /// <summary>
    /// Badge: small pill-shaped label. Use for counts, statuses, tags.
    /// </summary>
    public static void Badge(string text, Vector4? color = null)
    {
        Element(new ElementProps
        {
            Classes = Cls.Badge,
            Style = new ElementStyle
            {
                Width = Sizing.Auto,
                Height = Sizing.Fixed(18),
                BackgroundColor = color ?? Theme.Color.Accent,
                BorderRadius = Theme.Radius.Pill,
                Padding = new Spacing(0, Theme.Spacing.Md),
            },
        }, () =>
        {
            Text(text, new TextProps
            {
                Style = new TextStyle { Color = Theme.Color.TextInverse, FontSize = Theme.Typo.Caption },
            });
        });
    }
}
