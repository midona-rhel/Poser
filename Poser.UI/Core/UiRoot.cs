using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Poser.UI.Reactive;

namespace Poser.UI;

/// <summary>
/// One retained declarative surface. A root owns exactly three retained
/// things — the frame arena, the scope table, and the interaction-id cache —
/// and runs build, layout, and paint as one pass per frame. It draws into
/// the CURRENT ImGui window and never begins one, so a root composes inside
/// any existing pane exactly like an imperative control does.
/// </summary>
public sealed class UiRoot
{
    private const ulong FnvOffset = 14695981039346656037UL;
    private const ulong FnvPrime = 1099511628211UL;

    /// <summary>One path's retained cell: the "##rx…" id string, its single
    /// suffixed variant, and the portal body closure. Formatting and closure
    /// construction happen ONCE per path, on first sight; every later frame
    /// reuses the instances, which is what makes a warm frame allocation-free.
    /// Everything a path retains lives here so that pruning the path frees all
    /// of it.</summary>
    private sealed class IdEntry
    {
        internal IdEntry(string id, int frame)
        {
            Id = id;
            LastSeenFrame = frame;
        }

        internal string Id;

        /// <summary>The ONE suffixed name this path needs: a portal handle
        /// ("_popup") on the anchor, a scroll child ("-scroll") on the portal,
        /// a truncation readout ("-full") on the run.</summary>
        internal string? Alternate;

        internal PortalBody? Body;
        internal int LastSeenFrame;
    }

    /// <summary>
    /// One portal's retained body. <see cref="LegacyCrystarium.FloatingSurface.Popup"/>
    /// and <see cref="LegacyCrystarium.ScrollRegion"/> both take delegates, so a
    /// closure per frame would allocate on every warm frame of an open menu.
    /// The two delegates are built once per portal path; the walk writes its
    /// per-frame inputs into the fields instead of capturing them.
    /// </summary>
    private sealed class PortalBody
    {
        internal readonly Action Surface;
        internal readonly Action<LegacyCrystarium.ScrollRegionScope> Scroll;

        internal int Node;
        internal ulong Hash;
        internal float Scale;
        internal ImDrawListPtr DrawList;

        internal PortalBody(UiRoot root)
        {
            Surface = () => root.RunPortalSurface(this);
            Scroll = region => root.RunPortalScroll(this, region);
        }
    }

    /// <summary>
    /// What the walk carries DOWN a subtree. Four of these are the nearest
    /// painter's business rather than the element's own: currentColor and the
    /// glyph opacity it resolved, and — because a truncation readout belongs to
    /// the CONTROL, not to the run inside it — the hover state and reserved
    /// rect of the nearest interactive ancestor. The last two are the surface
    /// the subtree draws on and the reserve-width cap a scrolling portal
    /// imposes on the first interactive layer beneath it.
    /// </summary>
    private readonly struct WalkContext
    {
        internal WalkContext(
            Vector4? foreground,
            float svgOpacity,
            bool parentHovered,
            Vector2 parentMin,
            Vector2 parentMax,
            float hitWidthCap,
            ImDrawListPtr drawList)
        {
            Foreground = foreground;
            SvgOpacity = svgOpacity;
            ParentHovered = parentHovered;
            ParentMin = parentMin;
            ParentMax = parentMax;
            HitWidthCap = hitWidthCap;
            DrawList = drawList;
        }

        internal readonly Vector4? Foreground;
        internal readonly float SvgOpacity;
        internal readonly bool ParentHovered;
        internal readonly Vector2 ParentMin;
        internal readonly Vector2 ParentMax;
        internal readonly float HitWidthCap;
        internal readonly ImDrawListPtr DrawList;

        internal static WalkContext Detached(ImDrawListPtr drawList, float hitWidthCap) =>
            new(null, 1f, false, default, default, hitWidthCap, drawList);
    }

    private readonly FrameArena _arena = new();
    private readonly ScopeTable _scopes = new();
    private readonly Dictionary<ulong, IdEntry> _interactionIds = [];
    // Retained so pruning costs no allocation on a frame that drops a path.
    private readonly List<ulong> _prunedIds = [];
    private int[] _activated = new int[16];
    private int _activatedCount;

    internal static UiRoot? Ambient { get; private set; }

    internal FrameArena Arena => _arena;

    internal ScopeTable Scopes => _scopes;

    /// <summary>Live interaction-id paths; the pruning invariant's probe.</summary>
    internal int DebugInteractionIdCount => _interactionIds.Count;

    internal static UiRoot Require() =>
        Ambient ?? throw new InvalidOperationException(
            "No UI root is active. Components may only be declared inside a UiRoot build callback.");

    /// <summary>
    /// Builds, lays out, and paints one frame into
    /// <paramref name="origin"/> (a screen-space, already physical anchor)
    /// with <paramref name="size"/> physical pixels available. A build that
    /// throws leaves the scope table UNCOMMITTED: the tree is suspended for
    /// the frame, not unmounted.
    /// </summary>
    /// <remarks>
    /// CURSOR CONTRACT: a root paints ABSOLUTELY at
    /// <paramref name="origin"/> and still participates in the caller's
    /// layout flow, by reserving the arranged root extent exactly once at
    /// the end of the pass. That single Dummy is the root's whole
    /// contribution to the surrounding ImGui layout: legacy content written
    /// after the call flows below the tree, and
    /// <c>GetItemRectMin</c>/<c>GetItemRectMax</c> report the root's extent
    /// rather than whichever leaf the walk happened to reserve last.
    /// </remarks>
    public void Render(Vector2 origin, Vector2 size, Func<UiNode> build)
    {
        ArgumentNullException.ThrowIfNull(build);
        // The static trampoline is cached by the compiler, so routing the
        // parameterless form through the typed core costs one call, not one
        // allocation.
        Render(origin, size, in build, static (in Func<UiNode> tree) => tree());
    }

    /// <summary>
    /// As <see cref="Render(Vector2, Vector2, Func{UiNode})"/>, but the build
    /// callback receives <paramref name="props"/> BY REFERENCE. This is the
    /// ALLOCATION-FREE form for a tree whose inputs change per frame: a lambda
    /// that closed over those inputs would allocate on every frame, so the
    /// props travel as an argument and the callback stays static. The
    /// parameterless overload remains the right one for a tree built from
    /// static state alone.
    /// </summary>
    public void Render<TProps>(
        Vector2 origin, Vector2 size, in TProps props, UiBuilder<TProps> build)
    {
        ArgumentNullException.ThrowIfNull(build);
        float scale = ImGuiHelpers.GlobalScale;
        // Queued state is promoted by MountAndRender as each component
        // reaches its own Render, so one build observes one state.
        _arena.Reset();

        FrameArena? previousArena = FrameArena.Current;
        UiRoot? previousRoot = Ambient;
        FrameArena.Current = _arena;
        Ambient = this;
        UiNode root;
        try
        {
            root = build(in props);
            _arena.ValidateNode(root);
        }
        finally
        {
            FrameArena.Current = previousArena;
            Ambient = previousRoot;
        }

        if (!root.IsNone)
        {
            float availWidth = size.X / scale;
            float availHeight = size.Y / scale;
            LayoutSolver.Measure(_arena, root.Index, availWidth, availHeight);
            Vector2 measured = _arena[root.Index].LogicalSize;
            LayoutSolver.Arrange(
                _arena,
                root.Index,
                Vector2.Zero,
                new Vector2(
                    measured.X > 0f ? measured.X : availWidth,
                    measured.Y > 0f ? measured.Y : availHeight));

            _activatedCount = 0;
            Paint(
                root.Index, origin, scale, 0UL, 0,
                WalkContext.Detached(ImGui.GetWindowDrawList(), 0f));
            for (int i = 0; i < _activatedCount; i++)
                InteractionAdapter.Dispatch(this, in _arena[_activated[i]]);

            ImGui.SetCursorScreenPos(origin);
            ImGui.Dummy(_arena[root.Index].LogicalSize * scale);
        }

        PruneInteractionIds(_arena.FrameId);
        _scopes.CommitFrame(_arena.FrameId, rootCompleted: true);
    }

    // Path identity: parent path, element kind, the author's key OR the
    // sibling ordinal, and the owning component scope. A KEYED element drops
    // the ordinal outright — that is what lets a reordered list carry its
    // hover and motion state with it instead of inheriting its neighbour's.
    internal static ulong DebugChain(
        ulong parentHash, int ordinal, ElementKind kind, UiKey key, int scopeId)
    {
        ulong hash = parentHash == 0UL ? FnvOffset : parentHash;
        hash = Mix(hash, (byte)kind);
        hash = key.Kind != UiKeyKind.None
            ? key.HashInto(hash)
            : Mix(hash, (ulong)(uint)ordinal);
        return Mix(hash, (ulong)(uint)scopeId);
    }

    internal static ulong Mix(ulong hash, ulong value)
    {
        for (int i = 0; i < 8; i++)
        {
            hash ^= (byte)(value >> (i * 8));
            hash *= FnvPrime;
        }

        return hash;
    }

    private void Paint(
        int node, Vector2 origin, float scale, ulong parentHash, int ordinal,
        in WalkContext context)
    {
        ref ElementRecord record = ref _arena[node];
        ulong hash = DebugChain(parentHash, ordinal, record.Kind, record.Key, record.ScopeId);
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
                    opacity: (record.SvgOpacity > 0f ? record.SvgOpacity : 1f)
                        * context.SvgOpacity);
                break;
            case ElementKind.Portal:
                // Its children live on the floating surface, so the portal
                // walks them itself and this one never descends.
                PaintPortal(node, in record, hash, parentHash, origin, scale);
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
                AlternateId(hash, "-full"), context.ParentMin, context.ParentMax, text);
    }

    /// <summary>Reserves the element and lets its retained painter draw; the
    /// painter's return value is what the subtree inherits. Nothing here knows
    /// what kind of control it just painted.</summary>
    private WalkContext PaintInteractive(
        int node, ref ElementRecord record, ulong hash, Vector2 min, Vector2 max,
        in WalkContext context)
    {
        string id = InteractionId(hash);
        Vector2 box = max - min;
        Vector2 reserve = box;
        // The cap stops at THIS layer: a scrolling menu narrows its ROWS clear
        // of the scrollbar gutter, not whatever a row happens to contain.
        if (context.HitWidthCap > 0f && context.HitWidthCap < reserve.X)
            reserve.X = context.HitWidthCap;
        Poser.UI.InteractionResult hit = InteractionAdapter.Reserve(
            id, min, reserve, record.Disabled);

        Vector4? foreground = context.Foreground;
        float svgOpacity = context.SvgOpacity;
        if (_arena.GetObject(record.PainterSlot) is IInteractivePainter painter)
        {
            PaintOutput output = painter.Paint(new PaintInput(
                in hit, ImGui.GetID(id), record.PaintArg, record.Disabled,
                box, context.DrawList));
            foreground = output.Foreground ?? foreground;
            svgOpacity = output.SvgOpacity;
        }

        if (!string.IsNullOrEmpty(record.Help) && Poser.UI.LegacyCrystarium.HoverHelp.Gate(
                hit, hit.Disabled, hit.ScreenMin, hit.ScreenMax))
            Poser.UI.LegacyCrystarium.HoverHelp.Explain(id, hit.ScreenMin, hit.ScreenMax, record.Help!);

        // INLINE, before the portal's own Popup call later in this same walk:
        // the open path claims the exclusive chain, so a surface that opened
        // one statement too late would not occlude anything for a frame.
        if (record.OpensPortalNode != 0 && hit.Clicked)
            Poser.UI.LegacyCrystarium.OpenPopover(AlternateId(hash, "_popup"));

        WalkContext childContext = new(
            foreground, svgOpacity, hit.Hovered, hit.ScreenMin, hit.ScreenMax,
            0f, context.DrawList);

        bool fired = record.DispatchMode == Reactive.DispatchMode.Activated
            ? hit.Activated
            : hit.Clicked;
        if (!fired)
            return childContext;

        // Closing is inline because we are inside the popup body's scope; the
        // handler still waits for the post-walk dispatch, so a row that closes
        // the menu without changing anything closes it all the same.
        if (record.ClosesPortal)
            ImGui.CloseCurrentPopup();
        if (_activatedCount == _activated.Length)
            Array.Resize(ref _activated, _activated.Length * 2);
        _activated[_activatedCount++] = node;
        return childContext;
    }

    /// <summary>
    /// Declares the floating surface. Unconditionally: <c>Popup</c> is itself
    /// the open test, so the retained tree never has to know whether the menu
    /// is up — the declaration is the same either way and the surface's
    /// lifetime stays entirely inside the input kernel.
    /// </summary>
    private void PaintPortal(
        int node, in ElementRecord record, ulong hash, ulong anchorHash,
        Vector2 origin, float scale)
    {
        IdEntry entry = Entry(hash);
        PortalBody body = entry.Body ??= new PortalBody(this);
        body.Node = node;
        body.Hash = hash;
        body.Scale = scale;

        // The anchor is the portal's PARENT, so its hash is the one the walk
        // just came through and its rect is derived exactly as the walk
        // derives every other box.
        Vector2 anchorPos = _arena[record.AnchorNode].LogicalPos;
        Vector2 anchorSize = _arena[record.AnchorNode].LogicalSize;
        Vector2 anchorMin = origin + new Vector2(
            MathF.Round(anchorPos.X * scale), MathF.Round(anchorPos.Y * scale));
        Vector2 anchorMax = origin + new Vector2(
            MathF.Round((anchorPos.X + anchorSize.X) * scale),
            MathF.Round((anchorPos.Y + anchorSize.Y) * scale));
        // The shared anchored placement already adds its own gap; a surface
        // that wants a different one carries the rest on the anchor.
        anchorMax.Y += record.PortalAnchorCompensation * scale;

        Vector2 surface = LayoutSolver.PortalSurface(_arena, node);
        Poser.UI.LegacyCrystarium.FloatingSurface.Popup(
            AlternateId(anchorHash, "_popup"),
            new Poser.UI.FloatingSurfaceProps
            {
                Width = surface.X,
                Height = surface.Y,
                Padding = record.PortalPadding,
                AnchorMin = anchorMin,
                AnchorMax = anchorMax,
                Treatment = Poser.UI.FloatingSurfaceTreatment.Unframed,
            },
            body.Surface);
    }

    private void RunPortalSurface(PortalBody body)
    {
        int node = body.Node;
        Vector2 min = ImGui.GetWindowPos();
        body.DrawList = ImGui.GetWindowDrawList();
        if (_arena.GetObject(_arena[node].PainterSlot) is IPortalSurfacePainter surface)
            surface.Paint(body.DrawList, min, min + ImGui.GetWindowSize());

        float viewport = _arena[node].ScrollRegionHeight;
        if (viewport <= 0f)
        {
            WalkPortalChildren(body, ImGui.GetCursorScreenPos(), 0f);
            return;
        }

        Poser.UI.LegacyCrystarium.ScrollRegion(
            AlternateId(body.Hash, "-scroll"),
            ImGui.GetContentRegionAvail().X / body.Scale,
            viewport,
            body.Scroll);
    }

    private void RunPortalScroll(
        PortalBody body, Poser.UI.LegacyCrystarium.ScrollRegionScope region)
    {
        // The subtree places itself, but ItemSpacing.Y would still inflate the
        // scrolled content extent past the last row.
        Vector2 spacing = ImGui.GetStyle().ItemSpacing;
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(spacing.X, 0f));
        float cap = _arena[body.Node].Arg != 0
            ? region.ContentWidth * body.Scale
            : 0f;
        WalkPortalChildren(body, ImGui.GetCursorScreenPos(), cap);
        ImGui.PopStyleVar();
    }

    private void WalkPortalChildren(PortalBody body, Vector2 origin, float hitWidthCap)
    {
        int start = _arena[body.Node].ChildStart;
        int count = _arena[body.Node].ChildCount;
        // A portal is a detached surface in every sense: nothing above it
        // tints its content, and its subtree's boxes belong to the popup.
        WalkContext context = WalkContext.Detached(body.DrawList, hitWidthCap);
        for (int i = 0; i < count; i++)
            Paint(
                _arena.ChildAt(start + i).Index, origin, body.Scale, body.Hash, i,
                in context);
    }

    private IdEntry Entry(ulong hash)
    {
        int frame = _arena.FrameId;
        if (_interactionIds.TryGetValue(hash, out IdEntry? entry))
        {
            entry.LastSeenFrame = frame;
            return entry;
        }

        entry = new IdEntry("##rx" + hash.ToString("x16"), frame);
        _interactionIds[hash] = entry;
        return entry;
    }

    /// <summary>The one derived name a path is allowed. Suffixing is a retained
    /// string, not a per-frame concatenation.</summary>
    private string AlternateId(ulong hash, string suffix)
    {
        IdEntry entry = Entry(hash);
        return entry.Alternate ??= entry.Id + suffix;
    }

    private string InteractionId(ulong hash)
    {
#if DEBUG
        if (_interactionIds.TryGetValue(hash, out IdEntry? existing)
            && existing.LastSeenFrame == _arena.FrameId)
            throw new InvalidOperationException(
                $"Duplicate interaction path {existing.Id}: two siblings of one kind "
                + "resolved to the same identity, so they share a key (or both lack one "
                + "while sharing an ordinal). Give each an explicit stable key.");
#endif
        return Entry(hash).Id;
    }

    // A path the frame did not visit is gone: keeping it would leak one
    // entry per row a long-lived list ever showed.
    private void PruneInteractionIds(int frame)
    {
        _prunedIds.Clear();
        foreach (KeyValuePair<ulong, IdEntry> entry in _interactionIds)
        {
            if (entry.Value.LastSeenFrame < frame)
                _prunedIds.Add(entry.Key);
        }

        for (int i = 0; i < _prunedIds.Count; i++)
            _interactionIds.Remove(_prunedIds[i]);
    }
}
