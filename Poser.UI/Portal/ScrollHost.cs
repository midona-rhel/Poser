using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace Poser.UI.Reactive;

/// <summary>
/// The IN-WINDOW half of the same trick <see cref="PortalHost"/> plays for a
/// floating surface: a scrolling container's children are arranged past its
/// box, so the walk cannot descend into them where it met them. The host opens
/// the scroll child window at the container's content origin and re-anchors the
/// subtree at the SCROLLED cursor, which is the whole of what "scrolling" means
/// to the rest of the runtime — the solver still arranges one flat tree and the
/// painter still paints absolute logical boxes.
/// </summary>
internal sealed class ScrollHost
{
    /// <summary>
    /// One container's retained body, for the same reason
    /// <see cref="PortalHost.PortalBody"/> is retained:
    /// <see cref="LegacyCrystarium.ScrollRegion"/> takes a delegate, and a
    /// closure per frame would allocate on every warm frame of a live list. The
    /// delegate is built once per path; the walk writes its per-frame inputs
    /// into the fields instead of capturing them.
    /// </summary>
    internal sealed class ScrollBody
    {
        internal readonly Action<LegacyCrystarium.ScrollRegionScope> Scroll;

        internal int Node;
        internal ulong Hash;
        internal float Scale;

        /// <summary>The container's content origin in PHYSICAL pixels relative
        /// to the walk's own origin, rounded exactly as the paint pass rounds
        /// every box edge — subtracted from the region's cursor to give the
        /// anchor the children's absolute arranged positions are measured
        /// from.</summary>
        internal Vector2 ContentOffset;

        /// <summary>The style context the walk was carrying when it met the
        /// container. A scroll region is in FLOW, so its children inherit it.
        /// </summary>
        internal FrameWalker.WalkContext Context;

        internal ScrollBody(ScrollHost host) =>
            Scroll = region => host.RunScroll(this, region);
    }

    private readonly FrameArena _arena;
    private readonly IdentityCache _ids;
    private readonly FrameWalker _walker;

    internal ScrollHost(FrameArena arena, IdentityCache ids, FrameWalker walker)
    {
        _arena = arena;
        _ids = ids;
        _walker = walker;
    }

    /// <summary>
    /// Opens the viewport at the container's already-arranged content box. The
    /// cursor is placed ABSOLUTELY rather than left wherever the last
    /// reservation ended, exactly as every other placement in the paint pass:
    /// the box the solver resolved is the box the child window occupies.
    /// </summary>
    internal void Declare(
        int node, ulong hash, Vector2 origin, float scale,
        in FrameWalker.WalkContext context)
    {
        IdentityCache.IdEntry entry = _ids.Entry(hash);
        ScrollBody body = entry.Scroll ??= new ScrollBody(this);
        ref ElementRecord record = ref _arena[node];
        EdgeInsets padding = record.Layout.Padding;
        Vector2 contentOrigin = record.LogicalPos
            + new Vector2(padding.Left, padding.Top);
        Vector2 offset = new(
            MathF.Round(contentOrigin.X * scale),
            MathF.Round(contentOrigin.Y * scale));

        body.Node = node;
        body.Hash = hash;
        body.Scale = scale;
        body.ContentOffset = offset;
        body.Context = context;

        float width = MathF.Max(0f, record.LogicalSize.X - padding.Horizontal);
        float gutter = record.ScrollGutter;
        ImGui.SetCursorScreenPos(origin + offset);
        LegacyCrystarium.ScrollRegion(
            _ids.AlternateId(hash, "-scroll"),
            width,
            record.ScrollViewport,
            body.Scroll,
            gutter > 0f ? gutter : null);
    }

    private void RunScroll(
        ScrollBody body, LegacyCrystarium.ScrollRegionScope region)
    {
        // The subtree places itself, but ItemSpacing.Y would still inflate the
        // scrolled content extent past the last row. Balanced under a finally
        // because a throwing build leaves the style stack the caller's problem
        // otherwise, and ImGui never recovers from an unbalanced one.
        Vector2 spacing = ImGui.GetStyle().ItemSpacing;
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(spacing.X, 0f));
        try
        {
            float cap = _arena[body.Node].ScrollCapsHitWidth
                ? region.ContentWidth * body.Scale
                : 0f;
            Vector2 cursor = ImGui.GetCursorScreenPos();
            // The children's positions are ABSOLUTE and root-relative, so the
            // anchor that makes them land at the scrolled cursor is that cursor
            // less the container's own content origin.
            Vector2 origin = cursor - body.ContentOffset;
            _walker.WalkScrollChildren(
                body.Node, origin, body.Scale, body.Hash, cap, in body.Context);

            // Trailing breathing — a last row's margin, the list's bottom
            // padding — is INVISIBLE to ImGui's scroll extent: no item covers
            // it, so max-scroll would end at the last reservation and pin the
            // last row to the viewport edge. A zero-size reservation at the
            // content's true bottom keeps the stated gap scrollable.
            float bottom = 0f;
            ref ElementRecord node = ref _arena[body.Node];
            int start = node.ChildStart;
            int count = node.ChildCount;
            for (int i = 0; i < count; i++)
            {
                int child = _arena.ChildAt(start + i).Index;
                bottom = MathF.Max(
                    bottom,
                    _arena[child].LogicalPos.Y + _arena[child].LogicalSize.Y
                        + _arena[child].Layout.Margin.Bottom);
            }

            ImGui.SetCursorScreenPos(new Vector2(
                cursor.X, origin.Y + MathF.Round(bottom * body.Scale)));
            ImGui.Dummy(Vector2.Zero);
        }
        finally
        {
            ImGui.PopStyleVar();
        }
    }
}
