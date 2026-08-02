using System.Numerics;
using Poser.UI.Reactive;

namespace Poser.UI;

/// <summary>
/// THE element. There is one, and every control is a projection onto it: a
/// stylesheet reference plus an optional inline patch, a child range, a typed
/// listener set, leaf content (a text run or a glyph), the two state flags the
/// sheet's looks key off, help, and a key.
///
/// <para>Layout, iteration, state resolution, painting, motion ramps, help and
/// dispatch are implemented once on this base; a control never reimplements
/// any of them, and the conversion below is the whole of a control's
/// "implementation".</para>
/// </summary>
/// <summary>
/// Which input edge activates an element. Release-inside is the default and
/// what a button, a row and a list item mean; a floating surface's trigger and
/// its menu rows answer the PRESS, because a surface must claim the exclusive
/// chain before anything under it answers the same press.
/// </summary>
public enum Activation
{
    Release,
    Press,
}

public readonly record struct Element
{
    /// <summary>The family sheet this element resolves against.</summary>
    public SheetRef Sheet { get; init; }

    /// <summary>The inline patch: the highest-priority link of the chain.</summary>
    public ElementSheet? Style { get; init; }

    public UiChildren Children { get; init; }

    public Listeners On { get; init; }

    /// <summary>The element's own text run, drawn by the base inside its
    /// padded content box.</summary>
    public string? Text { get; init; }

    /// <summary>The element's own glyph, tinted with currentColor.</summary>
    public TablerIcon? Glyph { get; init; }

    /// <summary>The registry name, for the glyphs the enum does not
    /// carry. Wins over <see cref="Glyph"/> when both are stated.</summary>
    internal string? GlyphName { get; init; }

    public bool Disabled { get; init; }

    /// <summary>Double duty: it selects the sheet's Selected look AND it is
    /// the value <see cref="Listeners.OnToggle"/> negates.</summary>
    public bool Selected { get; init; }

    public string? Help { get; init; }

    public UiKey Key { get; init; }

    /// <summary>Which input edge fires this element's listeners.</summary>
    public Activation ActivateOn { get; init; }

    /// <summary>A cut run offers its full text while the element is hovered.
    /// Separate from <see cref="TextOverflow.Truncate"/> because a composed
    /// body run answers to its layout, not to a hit box.</summary>
    public bool Preview { get; init; }

    /// <summary>The normalized 0..1 position a ranged control shows — the
    /// slider's thumb, the bar's fill.</summary>
    public float Value { get; init; }

    /// <summary>The index <see cref="Listeners.OnPick"/> reports.</summary>
    public int Index { get; init; }

    /// <summary>Logical side of the glyph; 0 leaves it to the renderer.</summary>
    internal float GlyphSize { get; init; }

    /// <summary>Glyph stroke in the icon's own 24-unit viewBox; 0 is the
    /// renderer's default.</summary>
    internal float GlyphStroke { get; init; }

    /// <summary>Opts the glyph OUT of currentColor, for a control whose
    /// foreground is a compensated LABEL colour the glyph must not borrow.
    /// </summary>
    internal bool GlyphNoInherit { get; init; }

    /// <summary>The escape hatch, for geometry a sheet cannot express.</summary>
    internal IPainter? Painter { get; init; }

    internal bool ClipChildren { get; init; }

    /// <summary>The portal this element opens, 0 for none.</summary>
    internal int OpensPortalNode { get; init; }

    /// <summary>Closing is the ELEMENT's business, not the handler's: a menu
    /// row closes on every click, including the one that changes nothing.
    /// </summary>
    internal bool ClosesPortal { get; init; }

    internal INativeElement? Native { get; init; }

    /// <summary>A single child needs no collection: user-defined
    /// conversions do not chain, so the one-child form is stated.</summary>
    public static implicit operator UiChildren(Element element) => (UiNode)element;

    public static implicit operator UiNode(Element element) => Emit(element);

    private static UiNode Emit(in Element element)
    {
        FrameArena arena = FrameArena.Require();
        arena.ValidateChildren(element.Children);
        element.On.Validate(arena);
        ElementRecord record = default;
        record.Sheet = element.Sheet;
        record.PatchSlot = element.Style is { } patch ? arena.AddPatch(in patch) : 0;
        record.On = element.On;
        record.Text = element.Text;
        record.Glyph = element.GlyphName
            ?? (element.Glyph is { } glyph ? Tabler.NameFor(glyph) : null);
        record.GlyphSize = element.GlyphSize;
        record.GlyphStroke = element.GlyphStroke;
        record.GlyphNoInherit = element.GlyphNoInherit;
        record.Preview = element.Preview;
        record.Disabled = element.Disabled;
        record.Selected = element.Selected;
        record.Help = element.Help;
        record.Key = element.Key;
        record.Value = element.Value;
        record.Index = element.Index;
        record.Painter = element.Painter;
        record.ClipChildren = element.ClipChildren;
        record.OpensPortalNode = element.OpensPortalNode;
        record.ClosesPortal = element.ClosesPortal;
        record.ActivateOn = element.ActivateOn;
        record.NativeSlot = element.Native is { } island ? arena.AddObject(island) : 0;
        record.ChildStart = element.Children.Start;
        record.ChildCount = element.Children.Count;
        return arena.AddElement(in record);
    }

    /// <summary>Convenience for the controls whose only inline patch is a
    /// size the sheet cannot know — a measured button, a native island.
    /// </summary>
    internal static ElementSheet Sized(UiDim? width, UiDim? height) =>
        new() { Layout = new() { Width = width, Height = height } };

    internal static ElementSheet Tinted(Vector4 fill) =>
        new() { Colors = new() { Fill = fill } };
}
