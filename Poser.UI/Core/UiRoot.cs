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

    /// <summary>The retained "##rx…" id string for one path, plus the frame
    /// that last used it. Formatting happens ONCE per path, on first sight;
    /// every later frame reuses the instance, which is what makes a warm
    /// Reserve allocation-free.</summary>
    private sealed class IdEntry
    {
        internal IdEntry(string id, int frame)
        {
            Id = id;
            LastSeenFrame = frame;
        }

        internal string Id;
        internal int LastSeenFrame;
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
            Paint(root.Index, origin, scale, 0UL, 0, null);
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
        Vector4? inheritedForeground)
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

        Vector4? childForeground = inheritedForeground;
        bool clipped = false;
        ImDrawListPtr draw = default;
        switch (record.Kind)
        {
            case ElementKind.Text:
                // Text is placed UNROUNDED on purpose: a run has exactly one
                // snapping owner, Optical.Snap inside the text renderer.
                // Rounding the edge here would snap it twice — the centered
                // offset would be computed from an already-quantized box —
                // and the result would drift off the legacy centered label.
                Poser.UI.LegacyCrystarium.TextAt(
                    origin + (record.LogicalPos * scale),
                    record.Text ?? string.Empty,
                    LayoutSolver.TextStyleOf(in record, inheritedForeground));
                break;
            case ElementKind.Svg:
                Poser.UI.LegacyCrystarium.IconIn(
                    min,
                    max,
                    record.Text ?? string.Empty,
                    record.HasTextColor ? record.TextColor : null);
                break;
            case ElementKind.Interactive:
                if (PaintInteractive(node, ref record, hash, min, max) is { } painted)
                    childForeground = painted;
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
                Paint(_arena.ChildAt(start + i).Index, origin, scale, hash, i, childForeground);
        }
        finally
        {
            if (clipped)
                draw.PopClipRect();
        }
    }

    /// <summary>Reserves the element and lets its retained painter draw;
    /// the painter's return value is the subtree's resolved foreground.
    /// Nothing here knows what kind of control it just painted.</summary>
    private Vector4? PaintInteractive(
        int node, ref ElementRecord record, ulong hash, Vector2 min, Vector2 max)
    {
        string id = InteractionId(hash);
        Poser.UI.InteractionResult hit = InteractionAdapter.Reserve(
            id, min, max - min, record.Disabled);
        Vector4? foreground = null;
        if (_arena.GetObject(record.PainterSlot) is IInteractivePainter painter)
            foreground = painter.Paint(in hit, ImGui.GetID(id), record.PaintArg, record.Disabled);
        if (!string.IsNullOrEmpty(record.Help) && Poser.UI.LegacyCrystarium.HoverHelp.Gate(
                hit, hit.Disabled, hit.ScreenMin, hit.ScreenMax))
            Poser.UI.LegacyCrystarium.HoverHelp.Explain(id, hit.ScreenMin, hit.ScreenMax, record.Help!);
        if (!hit.Activated)
            return foreground;

        if (_activatedCount == _activated.Length)
            Array.Resize(ref _activated, _activated.Length * 2);
        _activated[_activatedCount++] = node;
        return foreground;
    }

    private string InteractionId(ulong hash)
    {
        int frame = _arena.FrameId;
        if (_interactionIds.TryGetValue(hash, out IdEntry? entry))
        {
#if DEBUG
            if (entry.LastSeenFrame == frame)
                throw new InvalidOperationException(
                    $"Duplicate interaction path {entry.Id}: two siblings of one kind "
                    + "resolved to the same identity, so they share a key (or both lack one "
                    + "while sharing an ordinal). Give each an explicit stable key.");
#endif
            entry.LastSeenFrame = frame;
            return entry.Id;
        }

        entry = new IdEntry("##rx" + hash.ToString("x16"), frame);
        _interactionIds[hash] = entry;
        return entry.Id;
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
