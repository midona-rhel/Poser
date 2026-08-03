using System.Diagnostics;
using System.Numerics;

namespace Poser.UI;

/// <summary>
/// The typed listener set every element may carry. Interactivity is not a
/// species: the PRESENCE of a listener is what reserves a hit rect, so a row,
/// an icon and a card become clickable by stating a handler and by nothing
/// else.
///
/// <para>Each listener names its own edge and its own payload, which is what
/// replaced the dispatch-mode byte and the untyped argument that rode beside
/// it.</para>
/// </summary>
public readonly record struct Listeners
{
    /// <summary>Release-inside activation (or Enter). A trigger that owns a
    /// floating surface fires on the PRESS instead — a menu must claim the
    /// exclusive chain before anything under it answers the same press.</summary>
    public UiHandler OnClick { get; init; }

    /// <summary>The click edge, carrying the NEGATION of the element's
    /// <c>Selected</c>: the element shows the value it has, the handler is
    /// told the value it was just asked for.</summary>
    public UiHandler<bool> OnToggle { get; init; }

    /// <summary>Continuous while the element is active: the pointer's x is
    /// mapped into <see cref="Min"/>..<see cref="Max"/> and dispatched. The
    /// element's own normalized position is updated BEFORE its paint, so the
    /// frame that moves the pointer is the frame that draws the thumb under
    /// it.</summary>
    public UiHandler<float> OnDrag { get; init; }

    /// <summary>Fired BEFORE each <see cref="OnDrag"/> dispatch — the same
    /// per-change edge the imperative rows hand their <c>onBegin</c>, which is
    /// why a session-opening handler must be idempotent (every current one
    /// is).</summary>
    public UiHandler OnDragBegin { get; init; }

    /// <summary>The gesture's commit: fired once when the drag releases.</summary>
    public UiHandler OnDragEnd { get; init; }

    /// <summary>Activation carrying the element's item index.</summary>
    public UiHandler<int> OnPick { get; init; }

    /// <summary>The named NATIVE colour boundary: the picker inside the
    /// popover edits and reports inline, so its value never travels through
    /// the activation buffer.</summary>
    public UiHandler<Vector4> OnColor { get; init; }

    /// <summary>The SECONDARY edge: the element is hovered and the right
    /// button clicks. It is not an activation — it competes with nothing,
    /// cancels nothing and is not release-inside — because a context gesture
    /// opens a surface ABOUT the element rather than acting on it.</summary>
    public UiHandler OnContext { get; init; }

    /// <summary>The range <see cref="OnDrag"/> maps the pointer into.</summary>
    public float Min { get; init; }

    /// <inheritdoc cref="Min"/>
    public float Max { get; init; }

    /// <summary>DEBUG provenance for the reducer tokens a control stored: a
    /// slot index outlives nothing, so one minted by an earlier frame must be
    /// caught where it is written rather than where it misfires.</summary>
    [Conditional("DEBUG")]
    internal void Validate(Reactive.FrameArena arena)
    {
        OnClick.Validate(arena);
        OnToggle.Validate(arena);
        OnDrag.Validate(arena);
        OnDragBegin.Validate(arena);
        OnDragEnd.Validate(arena);
        OnPick.Validate(arena);
        OnColor.Validate(arena);
        OnContext.Validate(arena);
    }

    internal bool Any =>
        !OnClick.IsNone || !OnToggle.IsNone || !OnDrag.IsNone
        || !OnDragBegin.IsNone || !OnDragEnd.IsNone
        || !OnPick.IsNone || !OnColor.IsNone || !OnContext.IsNone;
}
