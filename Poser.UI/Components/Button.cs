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
            0, 0, variant, disabled, help, in sx, key);
    }

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
            variant, disabled, help, in sx, key);
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
        UiKey key)
    {
        // The caption states NO color: it takes the foreground the painter
        // resolves for the whole subtree, which is what makes the disabled
        // group's compensated label a property of the button, not the text.
        UiChildren caption = Text(label);
        // Fill is the solver's business; Content and Fixed are known here.
        float width = sx.Width.Kind switch
        {
            UiDimKind.Fixed => sx.Width.Value,
            UiDimKind.Fill => 0f,
            _ => Poser.UI.LegacyCrystarium.IntrinsicButtonWidth(label, default),
        };
        return InteractiveCore(
            UiStyle.Extend(sx, ButtonHostLayout),
            caption,
            key,
            disabled,
            help,
            behaviorSlot,
            eventScope,
            eventReducer,
            TextButtonPainter.Instance,
            (byte)variant,
            clipChildren: true,
            new Vector2(width, Poser.UI.LegacyCrystarium.ButtonHeight(default)));
    }
}
