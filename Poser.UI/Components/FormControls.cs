using System;
using System.Numerics;
using Poser.UI.Reactive;

namespace Poser.UI;

/// <summary>
/// The four form leaves as retained twins. Each is one interactive (or, for the
/// bar, one decorative) element over the imperative control's own paint seam, so
/// the two paths cannot drift: the twin owns the box, the state and the
/// dispatch, and contributes no pixels of its own.
/// </summary>
public static partial class Crystarium
{
    /// <summary>
    /// The range slider. The value is CONTROLLED: the element carries the range
    /// and the value's normalized position, the runtime turns a drag into the
    /// value under the pointer, and the caller decides what to do with it.
    /// </summary>
    public static UiNode Slider(
        float value,
        float minimum,
        float maximum,
        Action<float> onChange,
        bool disabled = false,
        string? help = null,
        UiStyle sx = default,
        UiKey key = default)
    {
        FrameArena arena = FrameArena.Require();
        return EmitSlider(
            value, minimum, maximum,
            onChange is null ? 0 : arena.AddObject(onChange), 0, 0,
            disabled, help, in sx, key);
    }

    /// <inheritdoc cref="Slider(float, float, float, Action{float}, bool, string, UiStyle, UiKey)"/>
    public static UiNode Slider(
        float value,
        float minimum,
        float maximum,
        UiEvent<float> onChange,
        bool disabled = false,
        string? help = null,
        UiStyle sx = default,
        UiKey key = default)
    {
        FrameArena.Require().ValidateEvent(onChange);
        return EmitSlider(
            value, minimum, maximum, 0, onChange.ScopeId, onChange.ReducerSlot,
            disabled, help, in sx, key);
    }

    /// <summary>The iOS-style toggle. Controlled: the element stores the value it
    /// is SHOWING, and the click reports its negation.</summary>
    public static UiNode Switch(
        bool value,
        Action<bool> onChange,
        bool disabled = false,
        string? help = null,
        UiStyle sx = default,
        UiKey key = default)
    {
        FrameArena arena = FrameArena.Require();
        return EmitSwitch(
            value, onChange is null ? 0 : arena.AddObject(onChange), 0, 0,
            disabled, help, in sx, key);
    }

    /// <inheritdoc cref="Switch(bool, Action{bool}, bool, string, UiStyle, UiKey)"/>
    public static UiNode Switch(
        bool value,
        UiEvent<bool> onChange,
        bool disabled = false,
        string? help = null,
        UiStyle sx = default,
        UiKey key = default)
    {
        FrameArena.Require().ValidateEvent(onChange);
        return EmitSwitch(
            value, 0, onChange.ScopeId, onChange.ReducerSlot,
            disabled, help, in sx, key);
    }

    /// <summary>
    /// The colour well. It has no event form: the picker inside its popover is
    /// the named NATIVE boundary and edits inline, so the value never travels
    /// through the activation buffer a reducer token would need.
    /// </summary>
    public static UiNode ColorWell(
        Vector4 color,
        Action<Vector4> onChange,
        bool disabled = false,
        string? help = null,
        UiKey key = default)
    {
        FrameArena arena = FrameArena.Require();
        float side = ActiveTheme.Controls.ColorWellSize;
        return InteractiveCore(
            default,
            UiChildren.Empty,
            key,
            disabled,
            help,
            onChange is null ? 0 : arena.AddObject(onChange),
            0,
            0,
            ColorWellBoxPainter.Instance,
            paintArg: 0,
            clipChildren: false,
            declaredLogicalSize: new Vector2(side, side),
            dispatchMode: Reactive.DispatchMode.ColorPopup,
            // The one shape the product uses: an RGB well whose alpha the
            // picker may not touch.
            arg: 1,
            tint: color);
    }

    /// <summary>The determinate bar. Purely presentational — no id, no
    /// reservation — so it is a painted box rather than a control.</summary>
    public static UiNode Progress(float fraction, float width) =>
        ProgressCore(fraction, UiDim.Fixed(width));

    /// <summary>As above, with the bar's width left to the solver. A form row
    /// hands the bar what its readout and actions did not take, and that span is
    /// not knowable where the bar is declared.</summary>
    internal static UiNode ProgressCore(float fraction, UiDim width) =>
        PaintedBox(
            UiFlow.Row,
            Sx.Size(width, UiDim.Fixed(ActiveTheme.Controls.SliderHeight)),
            UiChildren.Empty,
            default,
            ProgressPainter.Instance,
            f2: fraction);

    private static UiNode EmitSlider(
        float value,
        float minimum,
        float maximum,
        int behaviorSlot,
        int eventScope,
        int eventReducer,
        bool disabled,
        string? help,
        in UiStyle sx,
        UiKey key) =>
        InteractiveCore(
            in sx,
            UiChildren.Empty,
            key,
            disabled,
            help,
            behaviorSlot,
            eventScope,
            eventReducer,
            SliderPainter.Instance,
            paintArg: 0,
            clipChildren: false,
            // Only the height is intrinsic: a slider is as wide as its row grants
            // it, which is why every form row hands it Fill.
            declaredLogicalSize: new Vector2(
                sx.Width.Kind == UiDimKind.Fixed ? sx.Width.Value : 0f,
                ActiveTheme.Controls.SliderHeight),
            dispatchMode: Reactive.DispatchMode.Drag,
            f0: minimum,
            f1: maximum,
            f2: maximum > minimum
                ? Math.Clamp((value - minimum) / (maximum - minimum), 0f, 1f)
                : 0f);

    private static UiNode EmitSwitch(
        bool value,
        int behaviorSlot,
        int eventScope,
        int eventReducer,
        bool disabled,
        string? help,
        in UiStyle sx,
        UiKey key) =>
        InteractiveCore(
            in sx,
            UiChildren.Empty,
            key,
            disabled,
            help,
            behaviorSlot,
            eventScope,
            eventReducer,
            SwitchPainter.Instance,
            paintArg: (byte)(value ? 1 : 0),
            clipChildren: false,
            declaredLogicalSize: new Vector2(
                ActiveTheme.Controls.SwitchWidth,
                ActiveTheme.Controls.SwitchHeight),
            dispatchMode: Reactive.DispatchMode.Toggled,
            arg: value ? 1 : 0);
}
