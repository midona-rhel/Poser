using System;
using System.Numerics;
using Poser.UI.Reactive;

namespace Poser.UI;

public static partial class Crystarium
{
    /// <summary>
    /// The ONE place an Interactive record is assembled. Every control that
    /// wants a hit box — the text button, and whatever comes after it — states
    /// its painter, its clip, its declared box and its handler wiring here and
    /// touches no record field itself, so the element's shape has exactly one
    /// definition to change.
    /// </summary>
    /// <param name="declaredLogicalSize">The control's intrinsic box. ZERO
    /// means "measure me like a box": the solver then derives the size from
    /// the composed children and <paramref name="sx"/>, which is what makes a
    /// hit box out of arbitrary content. A declared box still wins.</param>
    /// <param name="dispatchMode">Which input edge fires, and whether
    /// <paramref name="arg"/> rides along. See
    /// <see cref="Reactive.DispatchMode"/>.</param>
    /// <param name="opensPortalNode">The portal this element opens, 0 for
    /// none. The portal must already be declared, which it is: children are
    /// written into the arena before their parent.</param>
    internal static UiNode InteractiveCore(
        in UiStyle sx,
        UiChildren children,
        UiKey key,
        bool disabled,
        string? help,
        int behaviorSlot,
        int eventScope,
        int eventReducer,
        IInteractivePainter? painter,
        byte paintArg,
        bool clipChildren,
        Vector2 declaredLogicalSize,
        byte dispatchMode = Reactive.DispatchMode.Activated,
        int arg = 0,
        bool closesPortal = false,
        int opensPortalNode = 0)
    {
        FrameArena arena = FrameArena.Require();
        arena.ValidateChildren(children);
        ElementRecord record = default;
        record.Kind = ElementKind.Interactive;
        record.Style = sx;
        record.Key = key;
        record.Disabled = disabled;
        record.Help = help;
        record.BehaviorSlot = behaviorSlot;
        record.EventScope = eventScope;
        record.EventReducer = eventReducer;
        record.PainterSlot = painter is null ? 0 : arena.AddObject(painter);
        record.PaintArg = paintArg;
        record.ClipChildren = clipChildren;
        record.ChildStart = children.Start;
        record.ChildCount = children.Count;
        record.LogicalSize = declaredLogicalSize;
        record.DispatchMode = dispatchMode;
        record.Arg = arg;
        record.ClosesPortal = closesPortal;
        record.OpensPortalNode = opensPortalNode;
        return arena.AddElement(record);
    }

    /// <summary>
    /// A hit box around composed content and nothing else: no painter, no
    /// clip, no intrinsic box. This is the primitive every clickable thing is
    /// made of — a row, an icon, a card — so authoring one costs no access to
    /// anything internal.
    /// </summary>
    public static UiNode Interactive(
        UiChildren children = default,
        Action? onClick = null,
        bool disabled = false,
        string? help = null,
        UiStyle sx = default,
        UiKey key = default)
    {
        FrameArena arena = FrameArena.Require();
        return InteractiveCore(
            in sx,
            children,
            key,
            disabled,
            help,
            onClick is null ? 0 : arena.AddObject(onClick),
            0,
            0,
            painter: null,
            paintArg: 0,
            clipChildren: false,
            declaredLogicalSize: Vector2.Zero);
    }

    /// <summary>Component-event form: the token is two ints, so binding a
    /// reducer to a composed clickable boxes nothing.</summary>
    public static UiNode Interactive(
        UiChildren children,
        UiEvent onClick,
        bool disabled = false,
        string? help = null,
        UiStyle sx = default,
        UiKey key = default)
    {
        FrameArena.Require().ValidateEvent(onClick);
        return InteractiveCore(
            in sx,
            children,
            key,
            disabled,
            help,
            0,
            onClick.ScopeId,
            onClick.ReducerSlot,
            painter: null,
            paintArg: 0,
            clipChildren: false,
            declaredLogicalSize: Vector2.Zero);
    }
}
