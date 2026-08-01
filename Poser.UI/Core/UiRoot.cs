using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;

namespace Poser.UI.Reactive;

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

    private readonly FrameArena _arena = new();
    private readonly ScopeTable _scopes = new();
    // Path hash → the "##rx…" id string. Formatting happens ONCE per path,
    // on first sight; every later frame reuses the retained instance, which
    // is what makes a warm Reserve allocation-free.
    private readonly Dictionary<ulong, string> _interactionIds = [];
    private int[] _activated = new int[16];
    private int _activatedCount;

    internal static UiRoot? Ambient { get; private set; }

    internal FrameArena Arena => _arena;

    internal ScopeTable Scopes => _scopes;

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
    public void Render(Vector2 origin, Vector2 size, Func<UiNode> build)
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
            root = build();
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
            Paint(root.Index, origin, scale, 0UL, 0);
            for (int i = 0; i < _activatedCount; i++)
                InteractionAdapter.Dispatch(this, in _arena[_activated[i]]);
        }

        _scopes.CommitFrame(_arena.FrameId, rootCompleted: true);
    }

    private void Paint(int node, Vector2 origin, float scale, ulong parentHash, int ordinal)
    {
        ref ElementRecord record = ref _arena[node];
        ulong hash = Chain(parentHash, ordinal, in record);
        // Every edge is rounded from its ABSOLUTE logical coordinate, so a
        // shared edge between siblings rounds to one and the same pixel.
        Vector2 min = origin + new Vector2(
            MathF.Round(record.LogicalPos.X * scale),
            MathF.Round(record.LogicalPos.Y * scale));
        Vector2 max = origin + new Vector2(
            MathF.Round((record.LogicalPos.X + record.LogicalSize.X) * scale),
            MathF.Round((record.LogicalPos.Y + record.LogicalSize.Y) * scale));

        switch (record.Kind)
        {
            case ElementKind.Text:
                Poser.UI.Crystarium.TextAt(
                    min, record.Text ?? string.Empty, LayoutSolver.TextStyleOf(in record));
                break;
            case ElementKind.Svg:
                Poser.UI.Crystarium.IconIn(
                    min,
                    max,
                    record.Text ?? string.Empty,
                    record.HasTextColor ? record.TextColor : null);
                break;
            case ElementKind.Interactive:
                PaintInteractive(node, ref record, hash, min, max);
                break;
        }

        int start = record.ChildStart;
        int count = record.ChildCount;
        for (int i = 0; i < count; i++)
            Paint(_arena.ChildAt(start + i).Index, origin, scale, hash, i);
    }

    private void PaintInteractive(int node, ref ElementRecord record, ulong hash, Vector2 min, Vector2 max)
    {
        string id = InteractionId(hash);
        Poser.UI.InteractionResult hit = InteractionAdapter.Reserve(
            id, min, max - min, record.Disabled);
        Poser.UI.Crystarium.PaintTextButton(
            hit,
            ImGui.GetID(id),
            record.Text ?? string.Empty,
            default,
            (Poser.UI.ButtonVariant)record.Variant,
            record.Disabled);
        if (!string.IsNullOrEmpty(record.Help) && Poser.UI.Crystarium.HoverHelp.Gate(
                hit, hit.Disabled, hit.ScreenMin, hit.ScreenMax))
            Poser.UI.Crystarium.HoverHelp.Explain(id, hit.ScreenMin, hit.ScreenMax, record.Help!);
        if (!hit.Activated)
            return;

        if (_activatedCount == _activated.Length)
            Array.Resize(ref _activated, _activated.Length * 2);
        _activated[_activatedCount++] = node;
    }

    private string InteractionId(ulong hash)
    {
        if (_interactionIds.TryGetValue(hash, out string? id))
            return id;
        id = "##rx" + hash.ToString("x16");
        _interactionIds[hash] = id;
        return id;
    }

    // Path identity: parent path, position among siblings, the author's key,
    // and the owning component scope. Position alone would hand a reordered
    // list its neighbour's hover and motion state.
    private static ulong Chain(ulong parentHash, int ordinal, in ElementRecord record)
    {
        ulong hash = parentHash == 0UL ? FnvOffset : parentHash;
        hash = Mix(hash, (ulong)(uint)ordinal);
        hash = Mix(hash, (ulong)(uint)record.Key.GetHashCode());
        return Mix(hash, (ulong)(uint)record.ScopeId);
    }

    private static ulong Mix(ulong hash, ulong value)
    {
        for (int i = 0; i < 8; i++)
        {
            hash ^= (byte)(value >> (i * 8));
            hash *= FnvPrime;
        }

        return hash;
    }
}
