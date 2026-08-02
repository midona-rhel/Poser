namespace Poser.UI;

/// <summary>The Picto action-button tones. Each names a theme SHEET; there is
/// no palette switch anywhere behind them.</summary>
public enum ButtonStyle
{
    Secondary,
    Primary,
    Danger,
}

/// <summary>
/// The Picto text button. Its whole implementation is the conversion below:
/// a family sheet, a measured width the sheet cannot know, and the caption as
/// the element's own run. The box, the hover ramp, the disabled group and the
/// centred label are the base's, once.
/// </summary>
public readonly record struct Button
{
    public required string Label { get; init; }

    public ButtonStyle Style { get; init; }

    /// <summary>Workspace density: 26px tall, 12px per side, the label size,
    /// and a caption that ellipsises. Every button inside a form row is one.
    /// </summary>
    public bool Dense { get; init; }

    public ElementSheet? StyleSheet { get; init; }

    public UiHandler OnClick { get; init; }

    public bool Disabled { get; init; }

    public string? Help { get; init; }

    public UiKey Key { get; init; }

    /// <summary>A single child needs no collection: user-defined
    /// conversions do not chain, so the one-child form is stated.</summary>
    public static implicit operator UiChildren(Button button) => (UiNode)button;

    public static implicit operator UiNode(Button button) =>
        button.Emit(default);

    /// <summary>
    /// The same button OWNING a floating surface: the portal is the trigger's
    /// own child, so the popup handle and the anchor rect are both read off the
    /// button's path. The press edge opens it.
    /// </summary>
    internal UiNode WithPortal(UiNode portal)
    {
        UiNode trigger = Emit(portal);
        Crystarium.AnchorPortal(portal, trigger);
        return trigger;
    }

    /// <summary>The workspace button's own logical width, for a composition
    /// that must RESERVE its slot before deciding whether to show it.</summary>
    internal static float DenseWidth(string label) =>
        LegacyCrystarium.IntrinsicButtonWidth(label, ControlStyle.Workspace);

    private UiNode Emit(UiNode portal)
    {
        ControlStyle metrics = Dense ? ControlStyle.Workspace : default;
        ElementSheet patch = StyleSheet ?? default;
        LayoutSheet layout = patch.Layout ?? default;
        // Fill is the solver's business; everything else is the label's own
        // intrinsic border-box width, which no sheet can know.
        if (layout.Width is not { } width || width.Kind == UiDimKind.Content)
            layout = layout with
            {
                Width = UiDim.Fixed(
                    LegacyCrystarium.IntrinsicButtonWidth(Label, metrics)),
            };

        return new Element
        {
            Sheet = Family(),
            Style = patch with { Layout = layout },
            Text = Label,
            // A dense caption is cut to its padded box, so it offers the full
            // text while the button is hovered.
            Preview = Dense,
            On = new Listeners { OnClick = OnClick },
            Disabled = Disabled,
            Help = Help,
            Key = Key,
            // A trigger may not clip: its surface is a child and would be cut
            // to the button's own box. A plain button still clips its caption.
            ClipChildren = portal.IsNone,
            // A menu trigger opens on the press so the surface claims the
            // exclusive chain before anything under it answers it.
            ActivateOn = portal.IsNone ? Activation.Release : Activation.Press,
            Children = portal.IsNone ? default : portal,
            OpensPortalNode = portal.IsNone ? 0 : portal.Index,
        };
    }

    private SheetFamily Family() => (Style, Dense) switch
    {
        (ButtonStyle.Primary, false) => SheetFamily.ButtonPrimary,
        (ButtonStyle.Danger, false) => SheetFamily.ButtonDanger,
        (ButtonStyle.Primary, true) => SheetFamily.ButtonDensePrimary,
        (ButtonStyle.Danger, true) => SheetFamily.ButtonDenseDanger,
        (_, true) => SheetFamily.ButtonDense,
        _ => SheetFamily.Button,
    };
}
