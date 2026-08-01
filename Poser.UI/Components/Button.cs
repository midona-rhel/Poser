using System;
using System.Numerics;

namespace Poser.UI.Reactive;

public static partial class Crystarium
{
    /// <summary>
    /// The Picto text button as ONE interactive leaf. The declaration
    /// carries the intrinsic box and the paint parameters; the pixels come
    /// from the legacy painter during the root's walk, so the retained and
    /// imperative paths are the same button by construction.
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
            arena, label, onClick is null ? 0 : arena.AddObject(onClick),
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
        UiKey key = default) =>
        Emit(
            FrameArena.Require(), label, 0, onClick.ScopeId, onClick.ReducerSlot,
            variant, disabled, help, in sx, key);

    private static UiNode Emit(
        FrameArena arena,
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
        ElementRecord record = default;
        record.Kind = ElementKind.Interactive;
        record.Style = sx;
        record.Text = label;
        record.Key = key;
        record.Disabled = disabled;
        record.Variant = (byte)variant;
        record.Help = help;
        record.BehaviorSlot = behaviorSlot;
        record.EventScope = eventScope;
        record.EventReducer = eventReducer;
        // Fill is the solver's business; Content and Fixed are known here.
        float width = sx.Width.Kind switch
        {
            UiDimKind.Fixed => sx.Width.Value,
            UiDimKind.Fill => 0f,
            _ => Poser.UI.Crystarium.IntrinsicButtonWidth(label, default),
        };
        record.LogicalSize = new Vector2(
            width, Poser.UI.Crystarium.ButtonHeight(default));
        return new UiNode(arena.AddElement(record));
    }
}
