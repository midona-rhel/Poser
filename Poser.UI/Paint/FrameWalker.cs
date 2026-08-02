using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace Poser.UI.Reactive;

/// <summary>
/// The paint pass over an already-arranged tree. One recursive walk turns
/// logical boxes into physical pixels, resolves each interactive element's
/// identity and hands it to its retained painter, and collects the activations
/// the root dispatches AFTER the walk. It knows no control kinds: every
/// element-specific pixel belongs to a painter.
/// </summary>
internal sealed class FrameWalker
{
    /// <summary>
    /// What the walk carries DOWN a subtree. Four of these are the nearest
    /// painter's business rather than the element's own: currentColor and the
    /// glyph opacity it resolved, and — because a truncation readout belongs to
    /// the CONTROL, not to the run inside it — the hover state and reserved
    /// rect of the nearest interactive ancestor. The last is the reserve-width
    /// cap a scrolling portal imposes on the first interactive layer beneath it.
    ///
    /// <para>The draw list is deliberately NOT carried: a scrolling portal body
    /// is an ImGui CHILD window with its own list, so a surface threaded down
    /// from above would land an element's box on a different list than the text
    /// inside it. Every paint site resolves the CURRENT window's list where it
    /// paints.</para>
    /// </summary>
    private readonly struct WalkContext
    {
        internal WalkContext(
            Vector4? foreground,
            float svgOpacity,
            bool parentHovered,
            Vector2 parentMin,
            Vector2 parentMax,
            float hitWidthCap)
        {
            Foreground = foreground;
            SvgOpacity = svgOpacity;
            ParentHovered = parentHovered;
            ParentMin = parentMin;
            ParentMax = parentMax;
            HitWidthCap = hitWidthCap;
        }

        internal readonly Vector4? Foreground;
        internal readonly float SvgOpacity;
        internal readonly bool ParentHovered;
        internal readonly Vector2 ParentMin;
        internal readonly Vector2 ParentMax;
        internal readonly float HitWidthCap;

        internal static WalkContext Detached(float hitWidthCap) =>
            new(null, 1f, false, default, default, hitWidthCap);
    }

    private readonly FrameArena _arena;
    private readonly IdentityCache _ids;
    private PortalHost _portals = null!;
    private int[] _activated = new int[16];
    // The one payload an activation cannot recover from its own record: a drag
    // reports where the POINTER was, and the record only keeps where the value
    // ended up. Parallel to _activated, and meaningless for every other mode.
    private float[] _activatedValue = new float[16];
    private int _activatedCount;

    internal FrameWalker(FrameArena arena, IdentityCache ids)
    {
        _arena = arena;
        _ids = ids;
    }

    /// <summary>The one back-edge the constructor cannot close: a portal walks
    /// its own detached subtree, so host and walker each need the other. Wired
    /// ONCE, by the root that owns both.</summary>
    internal void Bind(PortalHost portals) => _portals = portals;

    /// <summary>Activations collected by the walk just finished, in the order
    /// the walk met them. The root dispatches them once the frame is painted,
    /// so a handler can never mutate state the same frame is still drawing.
    /// </summary>
    internal int ActivatedCount => _activatedCount;

    internal int ActivatedNode(int index) => _activated[index];

    /// <inheritdoc cref="_activatedValue"/>
    internal float ActivatedValue(int index) => _activatedValue[index];

    /// <summary>Paints one arranged tree at an already-physical
    /// <paramref name="origin"/>, and refills the activation buffer.</summary>
    internal void Walk(int node, Vector2 origin, float scale)
    {
        _activatedCount = 0;
        Paint(node, origin, scale, 0UL, 0, WalkContext.Detached(0f));
    }

    /// <summary>Walks a PORTAL's children onto whatever window the caller is
    /// standing in. A portal is a detached surface in every sense: nothing above
    /// it tints its content, and its subtree's boxes belong to that window — the
    /// popup, or its scrolling child.</summary>
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
            parentHash, ordinal, record.Kind, record.Key, record.ScopeId);
        // Every BOX edge is rounded from its ABSOLUTE logical coordinate, so
        // a shared edge between siblings rounds to one and the same pixel.
        Vector2 min = origin + new Vector2(
            MathF.Round(record.LogicalPos.X * scale),
            MathF.Round(record.LogicalPos.Y * scale));
        Vector2 max = origin + new Vector2(
            MathF.Round((record.LogicalPos.X + record.LogicalSize.X) * scale),
            MathF.Round((record.LogicalPos.Y + record.LogicalSize.Y) * scale));

        WalkContext childContext = context;
        bool clipped = false;
        ImDrawListPtr draw = default;
        switch (record.Kind)
        {
            case ElementKind.Box:
                // Decoration: a box that names a painter is painted BEFORE its
                // children and reserves nothing, so a bar, a rule or a help
                // registration costs the tree no hit box.
                if (record.PainterSlot != 0)
                    PaintDecoration(in record, hash, min, max);
                break;
            case ElementKind.Text:
                PaintText(in record, hash, origin, scale, in context);
                break;
            case ElementKind.Svg:
                Poser.UI.LegacyCrystarium.IconIn(
                    min,
                    max,
                    record.Text ?? string.Empty,
                    record.HasTextColor
                        ? record.TextColor
                        : (record.SvgInheritsColor ? context.Foreground : null),
                    // SvgCore always writes the element's own opacity. Keep
                    // zero meaningful instead of treating it as an unset
                    // sentinel; inherited opacity is composed separately.
                    opacity: record.SvgOpacity * context.SvgOpacity,
                    strokeWidth: record.SvgStroke > 0f ? record.SvgStroke : null);
                break;
            case ElementKind.Portal:
                // Its children live on the floating surface, so the portal
                // walks them itself and this one never descends.
                _portals.Declare(node, in record, hash, parentHash, origin, scale);
                return;
            case ElementKind.Native:
                // The named escape hatch. The runtime's whole contribution is
                // the identity, the cursor and the rect; a leaf besides, so
                // nothing below it is walked.
                if (_arena.GetObject(record.NativeSlot) is INativeElement island)
                {
                    string nativeId = _ids.InteractionId(hash);
                    ImGui.SetCursorScreenPos(min);
                    island.Draw(nativeId, min, max);
                }

                return;
            case ElementKind.Interactive:
                childContext = PaintInteractive(node, ref record, hash, min, max, in context);
                if (record.ClipChildren)
                {
                    draw = ImGui.GetWindowDrawList();
                    draw.PushClipRect(min, max, true);
                    clipped = true;
                }

                break;
        }

        try
        {
            int start = record.ChildStart;
            int count = record.ChildCount;
            for (int i = 0; i < count; i++)
                Paint(_arena.ChildAt(start + i).Index, origin, scale, hash, i, in childContext);
        }
        finally
        {
            if (clipped)
                draw.PopClipRect();
        }
    }

    /// <summary>A decorative box's paint. The hit is SYNTHESIZED — the element
    /// reserved nothing, so the only true thing about it is the rect — and the
    /// painter is handed the same id the runtime would have minted, which is
    /// what lets a geometric help registration name a stable target.</summary>
    private void PaintDecoration(
        in ElementRecord record, ulong hash, Vector2 min, Vector2 max)
    {
        if (_arena.GetObject(record.PainterSlot) is not IInteractivePainter painter)
            return;
        string id = _ids.InteractionId(hash);
        Poser.UI.InteractionResult hit = new(
            min,
            max,
            record.Disabled ? Poser.UI.PseudoState.Disabled : default,
            clicked: false,
            activated: false,
            doubleClicked: false,
            dragBegan: false,
            dragEnded: false,
            dragDelta: Vector2.Zero,
            owner: default);
        painter.Paint(new PaintInput(
            in hit, ImGui.GetID(id), record.PaintArg, record.Disabled,
            max - min, ImGui.GetWindowDrawList(), id, in record));
    }

    private void PaintText(
        in ElementRecord record, ulong hash, Vector2 origin, float scale,
        in WalkContext context)
    {
        string text = record.Text ?? string.Empty;
        Poser.UI.TextStyle style = LayoutSolver.TextStyleOf(in record, context.Foreground);
        // Text is placed UNROUNDED on purpose: a run has exactly one snapping
        // owner, Optical.Snap inside the text renderer. Rounding the edge here
        // would snap it twice — the centered offset would be computed from an
        // already-quantized box — and the result would drift off the legacy
        // centered label.
        Vector2 position = origin + (record.LogicalPos * scale);
        if (LayoutSolver.TextClip(in record) is not { } logicalClip)
        {
            Poser.UI.LegacyCrystarium.TextAt(position, text, style);
            return;
        }

        // A sized box that collapsed to nothing draws nothing, exactly as the
        // imperative controls skip a label with no room left for it.
        float clip = logicalClip * scale;
        if (clip <= 0f || text.Length == 0)
            return;

        Vector2 measured = Poser.UI.LegacyCrystarium.MeasureText(text, style);
        Poser.UI.LegacyCrystarium.TextAt(
            position, text, style, Poser.UI.TextConstraint.Truncate(clip));
        // Truncation-only readout: same chrome as help, no explanatory delay,
        // and it targets the CONTROL's rect because that is what the pointer
        // is over.
        if (record.TextPreviewOnClip && measured.X > clip && context.ParentHovered)
            Poser.UI.LegacyCrystarium.HoverHelp.Preview(
                _ids.AlternateId(hash, "-full"), context.ParentMin, context.ParentMax, text);
    }

    /// <summary>Reserves the element and lets its retained painter draw; the
    /// painter's return value is what the subtree inherits. Nothing here knows
    /// what kind of control it just painted.</summary>
    private WalkContext PaintInteractive(
        int node, ref ElementRecord record, ulong hash, Vector2 min, Vector2 max,
        in WalkContext context)
    {
        string id = _ids.InteractionId(hash);
        Vector2 box = max - min;
        Vector2 reserve = box;
        // The cap stops at THIS layer: a scrolling menu narrows its ROWS clear
        // of the scrollbar gutter, not whatever a row happens to contain.
        if (context.HitWidthCap > 0f && context.HitWidthCap < reserve.X)
            reserve.X = context.HitWidthCap;
        Poser.UI.InteractionResult hit = InteractionAdapter.Reserve(
            id, min, reserve, record.Disabled);

        // BEFORE the painter, exactly as the imperative slider settles its value
        // before it draws: the thumb must land under the pointer on the frame the
        // pointer moved, not on the next one.
        float dragged = 0f;
        bool dragging = false;
        if (record.DispatchMode == Reactive.DispatchMode.Drag && hit.Active && !hit.Disabled)
        {
            float next = Poser.UI.LegacyCrystarium.SliderValueAt(
                ImGui.GetIO().MousePos.X, hit.ScreenMin, hit.ScreenMax, record.F0, record.F1);
            float normalized = record.F1 > record.F0
                ? Math.Clamp((next - record.F0) / (record.F1 - record.F0), 0f, 1f)
                : 0f;
            // NaN is "no value under the pointer" — the span or the range is
            // empty — and an unchanged position is not an edit, so neither one
            // reports anything.
            if (!float.IsNaN(next) && normalized != record.F2)
            {
                record.F2 = normalized;
                dragged = next;
                dragging = true;
            }
        }

        Vector4? foreground = context.Foreground;
        float svgOpacity = context.SvgOpacity;
        if (_arena.GetObject(record.PainterSlot) is IInteractivePainter painter)
        {
            // Resolved HERE, not threaded: inside a scrolling portal body this
            // is the child window's list, which is the one the element's own
            // box belongs on.
            PaintOutput output = painter.Paint(new PaintInput(
                in hit, ImGui.GetID(id), record.PaintArg, record.Disabled,
                box, ImGui.GetWindowDrawList(), id, in record));
            foreground = output.Foreground ?? foreground;
            // A stated opacity COMPOSES onto what the subtree already had; an
            // unstated one leaves it exactly where it was.
            if (output.SvgOpacity is { } stated)
                svgOpacity *= stated;
        }

        if (!string.IsNullOrEmpty(record.Help) && Poser.UI.LegacyCrystarium.HoverHelp.Gate(
                hit, hit.Disabled, hit.ScreenMin, hit.ScreenMax))
            Poser.UI.LegacyCrystarium.HoverHelp.Explain(id, hit.ScreenMin, hit.ScreenMax, record.Help!);

        // INLINE, before the portal's own Popup call later in this same walk:
        // the open path claims the exclusive chain, so a surface that opened
        // one statement too late would not occlude anything for a frame.
        if (record.OpensPortalNode != 0 && hit.Clicked)
            Poser.UI.LegacyCrystarium.OpenPopover(_ids.AlternateId(hash, "_popup"));

        WalkContext childContext = new(
            foreground, svgOpacity, hit.Hovered, hit.ScreenMin, hit.ScreenMax, 0f);

        if (record.DispatchMode == Reactive.DispatchMode.ColorPopup)
        {
            ColorPopup(in record, id, hit);
            return childContext;
        }

        if (record.DispatchMode == Reactive.DispatchMode.Drag)
        {
            if (dragging)
                Activate(node, dragged);
            return childContext;
        }

        bool fired = record.DispatchMode is Reactive.DispatchMode.Activated
                or Reactive.DispatchMode.ActivatedItem
            ? hit.Activated
            : hit.Clicked;
        if (!fired)
            return childContext;

        // Closing is inline because we are inside the popup body's scope; the
        // handler still waits for the post-walk dispatch, so a row that closes
        // the menu without changing anything closes it all the same.
        if (record.ClosesPortal)
            ImGui.CloseCurrentPopup();
        Activate(node, 0f);
        return childContext;
    }

    private void Activate(int node, float value)
    {
        if (_activatedCount == _activated.Length)
        {
            Array.Resize(ref _activated, _activated.Length * 2);
            Array.Resize(ref _activatedValue, _activatedValue.Length * 2);
        }

        _activatedValue[_activatedCount] = value;
        _activated[_activatedCount++] = node;
    }

    /// <summary>
    /// The colour well's whole behaviour: the click opens the shared popover
    /// INLINE, for the same reason a menu trigger does, and the surface is
    /// declared every frame because the popup call is itself the open test. The
    /// picker inside it edits and reports directly — the named native boundary.
    /// </summary>
    private void ColorPopup(
        in ElementRecord record, string id, in Poser.UI.InteractionResult hit)
    {
        if (hit.Clicked && !hit.Disabled)
            Poser.UI.LegacyCrystarium.OpenPopover(
                Poser.UI.LegacyCrystarium.ColorWellPopupId(id));
        if (_arena.GetObject(record.BehaviorSlot) is not Action<Vector4> onChange)
            return;
        Poser.UI.LegacyCrystarium.DrawColorWellPopup(
            id, hit.ScreenMin, hit.ScreenMax, record.TextColor,
            rgbOnly: record.Arg != 0, onChange);
    }
}
