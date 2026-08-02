namespace Poser.UI;

/// <summary>
/// Picto's shared <c>.iconBtn</c>: a square icon-sized action whose overlay
/// fill and glyph lift are the sheet's states. The glyph rides currentColor,
/// so hover's tone change reaches it without a second declaration.
/// </summary>
public readonly record struct IconAction
{
    public required TablerIcon Icon { get; init; }

    public UiHandler OnClick { get; init; }

    public bool Disabled { get; init; }

    public string? Help { get; init; }

    public UiKey Key { get; init; }

    /// <summary>A single child needs no collection: user-defined
    /// conversions do not chain, so the one-child form is stated.</summary>
    public static implicit operator UiChildren(IconAction action) =>
        (UiNode)action;

    public static implicit operator UiNode(IconAction action) => new Element
    {
        Sheet = SheetFamily.IconAction,
        On = new Listeners { OnClick = action.OnClick },
        Disabled = action.Disabled,
        Help = action.Help,
        Key = action.Key,
        Children = new Glyph
        {
            Icon = action.Icon,
            Size = 16f,
            Stroke = 1.5f,
        },
    };
}
