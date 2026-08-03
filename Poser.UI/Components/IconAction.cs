using Poser.UI.Reactive;

namespace Poser.UI;

/// <summary>
/// Picto's shared <c>.iconBtn</c>: a square icon-sized action whose overlay
/// fill and glyph lift are the sheet's states. The glyph rides currentColor,
/// so hover's tone change reaches it without a second declaration.
///
/// <para>Stating <see cref="Selected"/> — with EITHER value — or
/// <see cref="Slashed"/> makes it the PERSISTENT twin instead: a toggle carries
/// a state, so its box is the selection fill at the control radius rather than
/// a momentary overlay, and an unstated selection is the whole difference
/// between the two. The box and the slash are the imperative toggle's own paint
/// seam.</para>
/// </summary>
public readonly record struct IconAction
{
    /// <summary>The glyph. Optional only because <see cref="Named"/> names one
    /// the enum does not carry; exactly one of the two is stated.</summary>
    public TablerIcon? Icon { get; init; }

    /// <summary>The registry NAME form, set through <see cref="Named"/>.
    /// </summary>
    internal string? IconName { get; init; }

    public UiHandler OnClick { get; init; }

    public bool Disabled { get; init; }

    public string? Help { get; init; }

    /// <summary>Mirrors the glyph: a redo arrow IS the undo arrow reflected,
    /// and the registry carries one of them.</summary>
    public bool FlipX { get; init; }

    /// <summary>The toggle's state. Unstated means "not a toggle" — a
    /// momentary action has no selection to be false.</summary>
    public bool? Selected { get; init; }

    /// <summary>The toggle's "off" diagonal. Implies the toggle treatment for
    /// the same reason a selection does.</summary>
    public bool Slashed { get; init; }

    /// <summary>The square's logical side; 0 takes the family's own — 24 for
    /// the window chassis' close action, the shell's 28 for a toggle.</summary>
    public float Size { get; init; }

    public UiKey Key { get; init; }

    /// <summary>The registry-name form, for the glyphs the enum does not carry
    /// ("chevron-down", "x"). Every other facet is stated with a
    /// <c>with</c>-expression, exactly as the enum form's are.</summary>
    public static IconAction Named(string name) => new() { IconName = name };

    /// <summary>A single child needs no collection: user-defined
    /// conversions do not chain, so the one-child form is stated.</summary>
    public static implicit operator UiChildren(IconAction action) =>
        (UiNode)action;

    public static implicit operator UiNode(IconAction action) => action.Emit();

    private UiNode Emit()
    {
        var controls = Crystarium.ActiveTheme.Controls;
        bool toggle = Selected is not null || Slashed;
        float side = Size > 0f
            ? Size
            : toggle
                ? controls.ShellIconAction
                : Crystarium.ActiveTheme.Floating.CloseActionSize;
        // The toggle inherits the legacy content inset — the glyph is a
        // fraction of its BOX — while the momentary button carries the icon
        // size and the heavier stroke .iconBtn draws its 16px mark with.
        float glyphSize = toggle ? side * controls.IconContentScale : 16f;
        float stroke = toggle ? 0f : 1.5f;

        UiNode mark = FlipX
            ? Mirrored(glyphSize, stroke)
            : new Glyph
            {
                Icon = Icon,
                Name = IconName,
                Size = glyphSize,
                Stroke = stroke,
            };
        // The slash is a LATER SIBLING, not a flag on the box hook: the walk
        // paints a box before its content, and the diagonal crosses the glyph.
        UiChildren children = mark;
        if (Slashed)
            children =
            [
                mark,
                new Element
                {
                    Style = Element.Sized(UiDim.Fill, UiDim.Fill),
                    Painter = ToggleSlashPainter.Instance,
                },
            ];

        return new Element
        {
            Sheet = toggle ? SheetFamily.IconActionToggle : SheetFamily.IconAction,
            Style = Size > 0f
                ? Element.Sized(UiDim.Fixed(Size), UiDim.Fixed(Size))
                : null,
            On = new Listeners { OnClick = OnClick },
            Disabled = Disabled,
            Selected = Selected ?? false,
            Painter = toggle ? IconToggleBoxPainter.Instance : null,
            Help = Help,
            Key = Key,
            Children = children,
        };
    }

    /// <summary>
    /// The mirrored mark. The base glyph path cannot flip, so the icon moves to
    /// a hook — and the element must then NOT state a glyph, or the base would
    /// draw the unflipped one underneath. The stroke rides the record; the fade
    /// is stated inline, because a hook sees only its OWN resolved style and
    /// the disabled group it hangs under is an ancestor's.
    /// </summary>
    private UiNode Mirrored(float glyphSize, float stroke) => new Element
    {
        Style = new()
        {
            Layout = new()
            {
                Width = UiDim.Fixed(glyphSize),
                Height = UiDim.Fixed(glyphSize),
            },
            Colors = Disabled
                ? new ColorSheet
                {
                    Opacity = Selected is not null || Slashed
                        ? LegacyCrystarium.ToggleOpacity(true)
                        : LegacyCrystarium.IconButtonDisabledOpacity,
                }
                : null,
        },
        GlyphStroke = stroke,
        Painter = FlippedGlyphPainter.For(
            IconName ?? Tabler.NameFor(Icon ?? default)),
    };
}
