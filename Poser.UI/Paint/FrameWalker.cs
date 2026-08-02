using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace Poser.UI.Reactive;

/// <summary>
/// The paint pass over an already-arranged tree. ONE per-element pass turns a
/// logical box into pixels: it reserves a hit rect when the element carries
/// listeners, resolves the element's state, flattens its sheet chain, draws
/// the base box, its label and its glyph, descends, and collects the
/// activations the root dispatches AFTER the walk.
///
/// <para>It knows no control kinds. Everything a control used to contribute —
/// a painter class, a dispatch byte, an untyped argument — is either a typed
/// facet of the one element or a named escape hatch for geometry a sheet
/// cannot express.</para>
/// </summary>
internal sealed class FrameWalker
{
    private enum Fired : byte
    {
        Click,
        Toggle,
        Drag,
        Pick,
    }

    /// <summary>
    /// What the walk carries DOWN a subtree: the inherited style (currentColor,
    /// the accumulated fade, typography), the hover state and rect of the
    /// nearest RESERVING ancestor — because a truncation readout belongs to the
    /// control, not to the run inside it — and the reserve-width cap a
    /// scrolling portal imposes on the first interactive layer beneath it.
    ///
    /// <para>The draw list is deliberately NOT carried: a scrolling portal body
    /// is an ImGui CHILD window with its own list, so every paint site resolves
    /// the CURRENT window's list where it paints.</para>
    /// </summary>
    private readonly struct WalkContext
    {
        internal WalkContext(
            Vector4? foreground,
            float glyphOpacity,
            bool parentHovered,
            Vector2 parentMin,
            Vector2 parentMax,
            float hitWidthCap)
        {
            Foreground = foreground;
            GlyphOpacity = glyphOpacity;
            ParentHovered = parentHovered;
            ParentMin = parentMin;
            ParentMax = parentMax;
            HitWidthCap = hitWidthCap;
        }

        /// <summary>currentColor, resolved by the nearest ancestor that had an
        /// opinion.</summary>
        internal readonly Vector4? Foreground;

        internal readonly float GlyphOpacity;
        internal readonly bool ParentHovered;
        internal readonly Vector2 ParentMin;
        internal readonly Vector2 ParentMax;
        internal readonly float HitWidthCap;

        internal static WalkContext Detached(float hitWidthCap) =>
            new(null, 1f, false, default, default, hitWidthCap);
    }

    /// <summary>
    /// The colour popover's retained trampoline. The picker inside the
    /// popover is the named NATIVE boundary: it edits DURING the walk, so its
    /// value cannot ride the activation buffer and its callback must be a
    /// plain delegate. One instance per walker, rebound per call, so an open
    /// well costs a warm frame no closure.
    /// </summary>
    private sealed class ColorSink
    {
        internal readonly Action<Vector4> Action;
        internal Poser.UI.UiRoot? Root;
        internal Poser.UI.UiHandler<Vector4> Handler;

        internal ColorSink() => Action = value =>
        {
            if (Root is { } root)
                Handler.Invoke(root, value);
        };
    }

    private readonly FrameArena _arena;
    private readonly IdentityCache _ids;
    private readonly ColorSink _color = new();
    private Poser.UI.UiRoot _root = null!;
    private PortalHost _portals = null!;
    private int[] _activated = new int[16];
    private float[] _activatedValue = new float[16];
    private Fired[] _activatedKind = new Fired[16];
    private int _activatedCount;

    internal FrameWalker(FrameArena arena, IdentityCache ids)
    {
        _arena = arena;
        _ids = ids;
    }

    /// <summary>The one back-edge the constructor cannot close: a portal walks
    /// its own detached subtree, so host and walker each need the other. Wired
    /// ONCE, by the root that owns both.</summary>
    internal void Bind(PortalHost portals, Poser.UI.UiRoot root)
    {
        _portals = portals;
        _root = root;
        _color.Root = root;
    }

    /// <summary>Activations collected by the walk just finished, in the order
    /// the walk met them. The root dispatches them once the frame is painted,
    /// so a handler can never mutate state the same frame is still drawing.
    /// </summary>
    internal int ActivatedCount => _activatedCount;

    /// <summary>Runs one collected activation through its typed listener.</summary>
    internal void Dispatch(int index)
    {
        ref ElementRecord record = ref _arena[_activated[index]];
        Poser.UI.Listeners on = record.On;
        switch (_activatedKind[index])
        {
            case Fired.Toggle:
                on.OnToggle.Invoke(_root, !record.Selected);
                break;
            case Fired.Drag:
                on.OnDrag.Invoke(_root, _activatedValue[index]);
                break;
            case Fired.Pick:
                on.OnPick.Invoke(_root, record.Index);
                break;
            default:
                on.OnClick.Invoke(_root);
                break;
        }
    }

    /// <summary>Paints one arranged tree at an already-physical
    /// <paramref name="origin"/>, and refills the activation buffer.</summary>
    internal void Walk(int node, Vector2 origin, float scale)
    {
        _activatedCount = 0;
        Paint(node, origin, scale, 0UL, 0, WalkContext.Detached(0f));
    }

    /// <summary>Walks a PORTAL's children onto whatever window the caller is
    /// standing in. A portal is a detached surface in every sense: nothing above
    /// it tints its content, and its subtree's boxes belong to that window.</summary>
    /// <param name="first">First child to walk, and <paramref name="last"/> is
    /// one past the last. A surface whose head does not scroll walks the range
    /// TWICE, onto two different windows — but the ordinal each child hands the
    /// identity chain is its position among ALL the portal's children, so a
    /// row's identity does not move when a caption is added above it.</param>
    internal void WalkDetachedChildren(
        int node, Vector2 origin, float scale, ulong parentHash, float hitWidthCap,
        int first, int last)
    {
        int start = _arena[node].ChildStart;
        WalkContext context = WalkContext.Detached(hitWidthCap);
        for (int i = first; i < last; i++)
            Paint(
                _arena.ChildAt(start + i).Index, origin, scale, parentHash, i,
                in context);
    }

    private void Paint(
        int node, Vector2 origin, float scale, ulong parentHash, int ordinal,
        in WalkContext context)
    {
        ref ElementRecord record = ref _arena[node];
        ulong hash = IdentityCache.Chain(
            parentHash, ordinal, record.Key, record.ScopeId);
        // Every BOX edge is rounded from its ABSOLUTE logical coordinate, so a
        // shared edge between siblings rounds to one and the same pixel.
        Vector2 min = origin + new Vector2(
            MathF.Round(record.LogicalPos.X * scale),
            MathF.Round(record.LogicalPos.Y * scale));
        Vector2 max = origin + new Vector2(
            MathF.Round((record.LogicalPos.X + record.LogicalSize.X) * scale),
            MathF.Round((record.LogicalPos.Y + record.LogicalSize.Y) * scale));

        if (record.PortalSlot != 0)
        {
            // Its children live on the floating surface, so the portal walks
            // them itself and this one never descends.
            _portals.Declare(node, in record, hash, parentHash, origin, scale);
            return;
        }

        // What makes an element interactive is EVIDENCE in its declaration: a
        // listener, portal wiring, a sheet that varies by pseudo state, or a
        // hook that reads pointer state. An element with none of the four — a
        // bar, a rule, a run, a plain row — reserves nothing, which is what
        // keeps the controls under an overlay reachable.
        bool reserves = record.On.Any
            || record.OpensPortalNode != 0
            || record.Painter is { NeedsHit: true }
            || Poser.UI.ThemeStyles.Stateful(record.Sheet)
            || (_arena.HasPatch(record.PatchSlot)
                && Poser.UI.ThemeStyles.Stateful(_arena.Patch(record.PatchSlot)));
        string? id = reserves || record.Help is not null || record.NativeSlot != 0
            ? _ids.InteractionId(hash)
            : null;
        Poser.UI.InteractionResult hit = default;
        if (reserves)
        {
            Vector2 reserve = max - min;
            // The cap stops at THIS layer: a scrolling menu narrows its ROWS
            // clear of the scrollbar gutter, not whatever a row contains.
            if (context.HitWidthCap > 0f && context.HitWidthCap < reserve.X)
                reserve.X = context.HitWidthCap;
            hit = InteractionAdapter.Reserve(id!, min, reserve, record.Disabled);
        }

        if (record.NativeSlot != 0)
        {
            // The named escape hatch. The runtime's whole contribution is the
            // identity, the cursor and the rect; a leaf besides.
            if (_arena.GetObject(record.NativeSlot) is INativeElement island)
            {
                ImGui.SetCursorScreenPos(min);
                island.Draw(id!, min, max);
            }

            return;
        }

        bool disabled = record.Disabled;
        bool hovered = hit.Hovered && !disabled;
        // BEFORE the paint, exactly as the imperative slider settles its value
        // before it draws: the thumb must land under the pointer on the frame
        // the pointer moved, not on the next one.
        float dragged = Drag(node, ref record, in hit, disabled);

        Poser.UI.ResolvedPaint style = Poser.UI.StyleResolver.Paint(
            record.Sheet,
            _arena.HasPatch(record.PatchSlot) ? _arena.Patch(record.PatchSlot) : null,
            hovered,
            hit.Active && !disabled,
            disabled,
            record.Selected,
            context.Foreground);

        ImDrawListPtr draw = ImGui.GetWindowDrawList();
        uint identity = id is null ? 0u : ImGui.GetID(id);
        // THE GUTTER IS THE PADDING (user rule): inside a gutter-capped
        // scroll, a row's fill PAINTS across the bar's reserved space to the
        // window edge — the bar overlays it, no dead strip beside the thumb.
        // The child clip would cut the fill at the content region, so it is
        // widened HORIZONTALLY only; the vertical bounds stay the viewport's.
        bool underGutter = context.HitWidthCap > 0f && record.Painter is null;
        if (underGutter)
            draw.PushClipRect(
                draw.GetClipRectMin(),
                new Vector2(
                    ImGui.GetWindowPos().X + ImGui.GetWindowSize().X,
                    draw.GetClipRectMax().Y),
                false);
        BoxPaint.Result box = BoxPaint.Draw(
            draw, min, max, in style, identity, hovered, disabled,
            ownsBox: record.Painter is null);
        if (underGutter)
            draw.PopClipRect();

        Vector4? foreground = box.Foreground;
        float glyphOpacity = context.GlyphOpacity * box.GlyphOpacity;
        if (record.Painter is { } painter)
        {
            PaintResult result = painter.Paint(new PaintContext(
                in record, in style, in hit, identity, id ?? string.Empty,
                min, max, draw));
            foreground = result.Foreground ?? foreground;
            // A stated opacity COMPOSES onto what the subtree already had.
            if (result.Opacity is { } stated)
                glyphOpacity *= stated;
        }

        // The hovered rect a readout and a geometric registration answer to:
        // this element's own when it reserved one, the nearest reserving
        // ancestor's when it did not.
        bool ownHover = reserves ? hit.Hovered : context.ParentHovered;
        Vector2 hoverMin = reserves ? hit.ScreenMin : context.ParentMin;
        Vector2 hoverMax = reserves ? hit.ScreenMax : context.ParentMax;
        Help(in record, id, reserves, in hit, hoverMin, hoverMax);

        // INLINE, before the portal's own Popup call later in this same walk:
        // the open path claims the exclusive chain, so a surface that opened
        // one statement too late would not occlude anything for a frame.
        if (record.OpensPortalNode != 0 && hit.Clicked)
            Poser.UI.LegacyCrystarium.OpenPopover(_ids.AlternateId(hash, "_popup"));

        if (!record.On.OnColor.IsNone)
            ColorPopup(in record, in style, id!, in hit);

        // The element's own content takes the foreground the BOX resolved —
        // the disabled group's compensated label colour, or a hook's override —
        // not the raw sheet value the group was computed from.
        Label(
            in record, foreground, hash, origin, scale, min, max, ownHover,
            hoverMin, hoverMax);
        Glyph(in record, foreground, min, max, glyphOpacity);

        if (record.ChildCount > 0)
        {
            // The cap is CONSUMED by the first reserving layer — a scrolling
            // menu narrows its rows, not whatever a row contains — but a
            // plain container (the list's column) passes it through, or the
            // rows underneath would never see it.
            WalkContext childContext = new(
                foreground ?? context.Foreground,
                glyphOpacity,
                ownHover,
                hoverMin,
                hoverMax,
                reserves ? 0f : context.HitWidthCap);
            bool clipped = record.ClipChildren;
            if (clipped)
                draw.PushClipRect(min, max, true);
            try
            {
                int start = record.ChildStart;
                int count = record.ChildCount;
                for (int i = 0; i < count; i++)
                    Paint(
                        _arena.ChildAt(start + i).Index, origin, scale, hash, i,
                        in childContext);
            }
            finally
            {
                if (clipped)
                    draw.PopClipRect();
            }
        }

        Fire(node, ref record, in hit, dragged);
    }

    /// <summary>
    /// The colour well's whole behaviour: the click opens the shared popover
    /// INLINE, for the same reason a menu trigger does, and the surface is
    /// declared every frame because the popup call is itself the open test.
    /// </summary>
    private void ColorPopup(
        in ElementRecord record, in Poser.UI.ResolvedPaint style, string id,
        in Poser.UI.InteractionResult hit)
    {
        if (hit.Clicked && !hit.Disabled)
            Poser.UI.LegacyCrystarium.OpenPopover(
                Poser.UI.LegacyCrystarium.ColorWellPopupId(id));
        _color.Handler = record.On.OnColor;
        Poser.UI.LegacyCrystarium.DrawColorWellPopup(
            id,
            hit.ScreenMin,
            hit.ScreenMax,
            style.Fill ?? default,
            // The one shape the product uses: an RGB well whose alpha the
            // picker may not touch.
            rgbOnly: true,
            _color.Action);
    }

    /// <summary>
    /// The drag update, run before paint. NaN is "no value under the pointer" —
    /// the span or the range is empty — and an unchanged position is not an
    /// edit, so neither one reports anything.
    /// </summary>
    private float Drag(
        int node, ref ElementRecord record, in Poser.UI.InteractionResult hit,
        bool disabled)
    {
        if (record.On.OnDrag.IsNone || !hit.Active || disabled)
            return float.NaN;

        float min = record.On.Min;
        float max = record.On.Max;
        float next = Poser.UI.LegacyCrystarium.SliderValueAt(
            ImGui.GetIO().MousePos.X, hit.ScreenMin, hit.ScreenMax, min, max);
        float normalized = max > min
            ? Math.Clamp((next - min) / (max - min), 0f, 1f)
            : 0f;
        if (float.IsNaN(next) || normalized == record.Value)
            return float.NaN;
        record.Value = normalized;
        return next;
    }

    /// <summary>
    /// An element with listeners gates its help on the live item; an element
    /// with help and NOTHING else registers it GEOMETRICALLY, because a row
    /// that owns no hit box has no item to gate on. That inversion is what
    /// makes a form row's own help win over the help a control inside it
    /// registered: the overlay is the last thing the row paints.
    /// </summary>
    private static void Help(
        in ElementRecord record, string? id, bool reserves,
        in Poser.UI.InteractionResult hit, Vector2 min, Vector2 max)
    {
        if (record.Help is not { Length: > 0 } help || id is null)
            return;
        bool shown = reserves
            ? Poser.UI.LegacyCrystarium.HoverHelp.Gate(
                hit, hit.Disabled, hit.ScreenMin, hit.ScreenMax)
            : Poser.UI.LegacyCrystarium.HoverHelp.HelpHovered(min, max);
        if (shown)
            Poser.UI.LegacyCrystarium.HoverHelp.Explain(id, min, max, help);
    }

    /// <summary>
    /// The element's own run, placed inside its padded content box by the same
    /// alignment the sheet gives its children. Text is placed UNROUNDED on
    /// purpose: a run has exactly one snapping owner, Optical.Snap inside the
    /// renderer, and rounding the edge here would snap it twice.
    /// </summary>
    private void Label(
        in ElementRecord record, Vector4? foreground, ulong hash,
        Vector2 origin, float scale, Vector2 min, Vector2 max,
        bool hovered, Vector2 hoverMin, Vector2 hoverMax)
    {
        if (record.Text is not { Length: > 0 } text
            || record.Painter is { OwnsText: true })
            return;

        Poser.UI.TextStyle textStyle = record.Type.Text(foreground);
        // Measured ONCE, by the pass whose job that is: the run's intrinsic box
        // is what the solver already asked for, and measuring twice would put a
        // second shaping pass on every warm frame.
        Vector2 measured = record.TextSize;
        ref readonly Poser.UI.ResolvedLayout layout = ref record.Layout;
        Vector2 contentOrigin = record.LogicalPos
            + new Vector2(layout.Padding.Left, layout.Padding.Top);
        Vector2 span = new(
            MathF.Max(0f, record.LogicalSize.X - layout.Padding.Horizontal),
            MathF.Max(0f, record.LogicalSize.Y - layout.Padding.Vertical));
        // Sizing says how much room a run occupies; only Truncate/Clip say it
        // may not spill. A cut run therefore fills its box and a visible one
        // takes its intrinsic width, and the alignment places whichever it is.
        bool cut = record.Type.Overflow != Poser.UI.TextOverflow.Visible;
        Vector2 box = new(
            cut ? span.X : measured.X / scale, measured.Y / scale);
        Vector2 position = origin + (contentOrigin
            + new Vector2(
                Offset(layout.Justify, span.X, box.X),
                // The sheet's optical rise: centred row ink leans below the
                // midline, so a list band lifts its run by the stated amount.
                Offset(layout.Align, span.Y, box.Y) + record.Type.InkRise)) * scale;

        bool clipped = record.ClipChildren;
        ImDrawListPtr draw = default;
        if (clipped)
        {
            draw = ImGui.GetWindowDrawList();
            draw.PushClipRect(min, max, true);
        }

        try
        {
            if (!cut)
            {
                Poser.UI.LegacyCrystarium.TextAt(position, text, textStyle);
                return;
            }

            // A sized box that collapsed to nothing draws nothing, exactly as
            // the imperative controls skip a label with no room left for it.
            float clip = box.X * scale;
            if (clip <= 0f)
                return;
            // Truncate constrains ONLY on overflow — the clip's snapped edge
            // can shave a fitting run's descender. Clip mode keeps the
            // legacy always-clip behavior for byte-frozen twins.
            if (measured.X <= clip
                && record.Type.Overflow == Poser.UI.TextOverflow.Truncate)
            {
                Poser.UI.LegacyCrystarium.TextAt(position, text, textStyle);
                return;
            }

            Poser.UI.LegacyCrystarium.TextAt(
                position, text, textStyle, Poser.UI.TextConstraint.Truncate(clip));
            // Truncation-only readout: same chrome as help, no explanatory
            // delay, and it targets the CONTROL's rect because that is what the
            // pointer is over.
            if (record.Preview && measured.X > clip && hovered)
                Poser.UI.LegacyCrystarium.HoverHelp.Preview(
                    _ids.PreviewId(hash), hoverMin, hoverMax, text);
        }
        finally
        {
            if (clipped)
                draw.PopClipRect();
        }
    }

    private static void Glyph(
        in ElementRecord record, Vector4? foreground,
        Vector2 min, Vector2 max, float opacity)
    {
        if (record.Glyph is not { } glyph)
            return;
        Poser.UI.LegacyCrystarium.IconIn(
            min,
            max,
            glyph,
            record.GlyphNoInherit ? null : foreground,
            opacity: opacity,
            strokeWidth: record.GlyphStroke > 0f ? record.GlyphStroke : null);
    }

    /// <summary>
    /// Typed dispatch. Each listener names its own edge: a toggle and a menu
    /// trigger answer the PRESS — the trigger because a surface must claim the
    /// exclusive chain before anything under it answers the same press — while
    /// a click and a pick answer release-inside.
    /// </summary>
    private void Fire(
        int node, ref ElementRecord record, in Poser.UI.InteractionResult hit,
        float dragged)
    {
        if (!float.IsNaN(dragged))
        {
            Activate(node, Fired.Drag, dragged);
            return;
        }

        if (!record.On.OnToggle.IsNone && hit.Clicked)
        {
            Close(in record);
            Activate(node, Fired.Toggle, 0f);
            return;
        }

        bool fired = record.ActivateOn == Poser.UI.Activation.Press
            ? hit.Clicked
            : hit.Activated;
        if (!fired)
            return;
        if (!record.On.OnPick.IsNone)
        {
            Close(in record);
            Activate(node, Fired.Pick, 0f);
            return;
        }

        if (record.On.OnClick.IsNone)
        {
            // A row that reports nothing still closes: the close is the
            // ELEMENT's, so the missing handler costs it nothing.
            Close(in record);
            return;
        }

        Close(in record);
        Activate(node, Fired.Click, 0f);
    }

    // Inline because we are inside the popup body's scope; the handler still
    // waits for the post-walk dispatch, so a row that closes the menu without
    // changing anything closes it all the same.
    private static void Close(in ElementRecord record)
    {
        if (record.ClosesPortal)
            ImGui.CloseCurrentPopup();
    }

    private void Activate(int node, Fired kind, float value)
    {
        if (_activatedCount == _activated.Length)
        {
            Array.Resize(ref _activated, _activated.Length * 2);
            Array.Resize(ref _activatedValue, _activatedValue.Length * 2);
            Array.Resize(ref _activatedKind, _activatedKind.Length * 2);
        }

        _activatedValue[_activatedCount] = value;
        _activatedKind[_activatedCount] = kind;
        _activated[_activatedCount++] = node;
    }

    // Stretch has no meaning for a run, so it reads as Start.
    private static float Offset(Poser.UI.UiAlign align, float available, float used) =>
        align switch
        {
            Poser.UI.UiAlign.Center => (available - used) * 0.5f,
            Poser.UI.UiAlign.End => available - used,
            _ => 0f,
        };
}
