namespace Poser.UI;

/// <summary>
/// A text run. Size, tint, weight and cut all come from the sheet chain, so a
/// run states only its role — and a role is a sheet, not six arguments.
/// </summary>
public readonly record struct Label
{
    public required string Text { get; init; }

    /// <summary>The text role: <see cref="SheetFamily.Caption"/>,
    /// <see cref="SheetFamily.FormLabel"/>, and so on. Unset inherits
    /// everything.</summary>
    public SheetRef Sheet { get; init; }

    public ElementSheet? Style { get; init; }

    /// <summary>A cut run offers its full text while its control is hovered.
    /// Separate from <see cref="TextOverflow.Truncate"/> because a composed
    /// body run answers to its layout, not to a hit box.</summary>
    public bool Preview { get; init; }

    public UiKey Key { get; init; }

    /// <summary>A single child needs no collection: user-defined
    /// conversions do not chain, so the one-child form is stated.</summary>
    public static implicit operator UiChildren(Label label) => (UiNode)label;

    public static implicit operator UiNode(Label label) => new Element
    {
        Sheet = label.Sheet,
        Style = label.Style,
        Text = label.Text,
        Preview = label.Preview,
        Key = label.Key,
    };
}
