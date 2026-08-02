using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Poser.UI.Reactive;

namespace Poser.UI;

/// <summary>
/// One retained declarative surface. A root owns the retained state — the
/// frame arena, the scope table, and the identity cache — and the collaborators
/// that read it — the frame walker and the portal host — and runs build,
/// layout, and paint as one pass per frame. It draws into the CURRENT ImGui
/// window and never begins one, so a root composes inside any existing pane
/// exactly like an imperative control does.
/// </summary>
public sealed class UiRoot
{
    private readonly FrameArena _arena = new();
    private readonly ScopeTable _scopes = new();
    private readonly IdentityCache _ids;
    private readonly FrameWalker _walker;
    private readonly PortalHost _portals;

    public UiRoot()
    {
        _ids = new IdentityCache(_arena);
        _walker = new FrameWalker(_arena, _ids);
        // The walker meets portals on the way down and the host walks a portal's
        // subtree on the way out, so the pair is mutually referential by nature.
        // Wired here, once, rather than resolved anywhere at paint time.
        _portals = new PortalHost(_arena, _ids, _walker);
        _walker.Bind(_portals);
    }

    internal static UiRoot? Ambient { get; private set; }

    internal FrameArena Arena => _arena;

    internal ScopeTable Scopes => _scopes;

    /// <inheritdoc cref="IdentityCache.Count"/>
    internal int DebugInteractionIdCount => _ids.Count;

    /// <inheritdoc cref="IdentityCache.Chain"/>
    internal static ulong DebugChain(
        ulong parentHash, int ordinal, ElementKind kind, UiKey key, int scopeId) =>
        IdentityCache.Chain(parentHash, ordinal, kind, key, scopeId);

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

            _walker.Walk(root.Index, origin, scale);
            for (int i = 0; i < _walker.ActivatedCount; i++)
                InteractionAdapter.Dispatch(
                    this,
                    in _arena[_walker.ActivatedNode(i)],
                    _walker.ActivatedValue(i));

            ImGui.SetCursorScreenPos(origin);
            ImGui.Dummy(_arena[root.Index].LogicalSize * scale);
        }

        _ids.Prune(_arena.FrameId);
        _scopes.CommitFrame(_arena.FrameId, rootCompleted: true);
    }
}
