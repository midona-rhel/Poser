using System;
using System.Numerics;
using Poser.UI.Reactive;

namespace Poser.UI;

public static partial class Crystarium
{
    // The host NAMES its own layout, so the caller's sx can still set size,
    // margin and padding but never how the label is placed inside the box.
    private static readonly UiStyle ButtonHostLayout = new(
        UiStyleFields.Flow | UiStyleFields.Justify | UiStyleFields.Align,
        UiFlow.Stack,
        0f,
        default,
        default,
        default,
        default,
        UiAlign.Center,
        UiAlign.Center);

    // `.btn` at workspace density: 12px per side, the box's own padding rather
    // than a number the caption restates. It is the caption's CLIP width too,
    // so the ellipsis lands exactly where the imperative pre-truncation put it.
    private static UiStyle DenseButtonPadding => Sx.Pad(
        new EdgeInsets(
            Poser.UI.Crystarium.ActiveTheme.Spacing.Six, 0f,
            Poser.UI.Crystarium.ActiveTheme.Spacing.Six, 0f));

    /// <summary>
    /// The Picto text button as a real composition: an interactive HOST that
    /// carries the intrinsic box and the retained painter, with the caption
    /// as an ordinary <see cref="Text"/> child centered by the stack solver.
    /// The box pixels come from the legacy painter, so the retained and
    /// imperative paths are the same button by construction — but nothing in
    /// the runtime knows a button exists.
    /// </summary>
    public static UiNode Button(
        string label,
        Action? onClick = null,
        Poser.UI.ButtonVariant variant = Poser.UI.ButtonVariant.Secondary,
        bool disabled = false,
        string? help = null,
        UiStyle sx = default,
        UiKey key = default)
    {
        FrameArena arena = FrameArena.Require();
        return Emit(
            label, onClick is null ? 0 : arena.AddObject(onClick),
            0, 0, variant, disabled, help, in sx, key, dense: false);
    }

    /// <summary>
    /// The same button at WORKSPACE metrics — 26px tall, 12px side padding, the
    /// label size, and a caption that ellipsises instead of clipping. Every
    /// button inside a form row is one of these, which is why the dense metrics
    /// live here beside the comfortable ones rather than in the compositions.
    /// </summary>
    public static UiNode FormButton(
        string label,
        Action? onClick,
        Poser.UI.ButtonVariant variant = Poser.UI.ButtonVariant.Secondary,
        bool disabled = false,
        string? help = null,
        UiStyle sx = default,
        UiKey key = default)
    {
        FrameArena arena = FrameArena.Require();
        return Emit(
            label, onClick is null ? 0 : arena.AddObject(onClick),
            0, 0, variant, disabled, help, in sx, key, dense: true);
    }

    /// <summary>The workspace button's own logical width, for a composition that
    /// must RESERVE its slot before deciding whether to show it.</summary>
    internal static float FormButtonWidth(string label) =>
        Poser.UI.LegacyCrystarium.IntrinsicButtonWidth(
            label, Poser.UI.ControlStyle.Workspace);

    /// <summary>Component-event form: the token is two ints, so binding a
    /// reducer to a button boxes nothing.</summary>
    public static UiNode Button(
        string label,
        UiEvent onClick,
        Poser.UI.ButtonVariant variant = Poser.UI.ButtonVariant.Secondary,
        bool disabled = false,
        string? help = null,
        UiStyle sx = default,
        UiKey key = default)
    {
        FrameArena.Require().ValidateEvent(onClick);
        return Emit(
            label, 0, onClick.ScopeId, onClick.ReducerSlot,
            variant, disabled, help, in sx, key, dense: false);
    }

    /// <summary>
    /// A button that also OWNS a floating surface: the portal is declared as
    /// the trigger's own child, so the popup handle and the anchor rect are
    /// both read off the button's path — the same trigger-owns-portal shape
    /// <see cref="Dropdown"/> uses, reached here so a picker's trigger can be
    /// an ordinary button rather than a second button implementation.
    ///
    /// <para>The press edge opens; the handler, when there is one, is the
    /// caller's chance to LOAD what the surface is about to show.</para>
    /// </summary>
    internal static UiNode PortalButton(
        string label,
        UiNode portal,
        Action? onOpen,
        bool dense,
        bool disabled = false,
        string? help = null,
        UiStyle sx = default,
        UiKey key = default)
    {
        FrameArena arena = FrameArena.Require();
        UiNode trigger = Emit(
            label, onOpen is null ? 0 : arena.AddObject(onOpen), 0, 0,
            Poser.UI.ButtonVariant.Secondary, disabled, help, in sx, key, dense,
            portal);
        AnchorPortal(portal, trigger);
        return trigger;
    }

    private static UiNode Emit(
        string label,
        int behaviorSlot,
        int eventScope,
        int eventReducer,
        Poser.UI.ButtonVariant variant,
        bool disabled,
        string? help,
        in UiStyle sx,
        UiKey key,
        bool dense,
        UiNode portal = default)
    {
        Poser.UI.ControlStyle metrics =
            dense ? Poser.UI.ControlStyle.Workspace : default;
        // The caption states NO color: it takes the foreground the painter
        // resolves for the whole subtree, which is what makes the disabled
        // group's compensated label a property of the button, not the text.
        // A dense caption fills the padded content box and is CUT to it, which
        // is what makes a workspace button in a form row ellipsise its value.
        UiNode captionNode = dense
            ? TextCore(
                label,
                Poser.UI.Crystarium.ActiveTheme.Typography.LabelSize,
                null,
                Sx.Size(UiDim.Fill, default),
                default,
                Poser.UI.TextOverflow.Truncate,
                previewOnClip: true)
            : Text(label);
        // The surface is out of flow, so it costs the caption's own centring
        // nothing — but it must be a CHILD, because that is where the anchor
        // rect and the popup handle are read from.
        UiChildren caption = portal.IsNone
            ? captionNode
            : [captionNode, portal];
        // Fill is the solver's business; Content and Fixed are known here.
        float width = sx.Width.Kind switch
        {
            UiDimKind.Fixed => sx.Width.Value,
            UiDimKind.Fill => 0f,
            _ => Poser.UI.LegacyCrystarium.IntrinsicButtonWidth(label, metrics),
        };
        UiStyle host = UiStyle.Extend(sx, ButtonHostLayout);
        if (dense)
            host = UiStyle.Extend(host, DenseButtonPadding);
        return InteractiveCore(
            host,
            caption,
            key,
            disabled,
            help,
            behaviorSlot,
            eventScope,
            eventReducer,
            TextButtonPainter.Instance,
            (byte)variant,
            // A trigger may not clip: its surface is a child and would be cut
            // to the button's own box. A plain button still clips its caption.
            clipChildren: portal.IsNone,
            new Vector2(width, Poser.UI.LegacyCrystarium.ButtonHeight(metrics)),
            // Menus open on the PRESS edge so the surface claims the exclusive
            // chain before anything under it can answer the same press.
            dispatchMode: portal.IsNone
                ? Reactive.DispatchMode.Activated
                : Reactive.DispatchMode.Clicked,
            opensPortalNode: portal.IsNone ? 0 : portal.Index);
    }
}
