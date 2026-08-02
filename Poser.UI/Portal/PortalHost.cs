using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace Poser.UI.Reactive;

/// <summary>
/// The floating half of the tree. A portal's children do not live under their
/// declaration site: the host declares the surface, and the surface's own body
/// callback walks the detached subtree onto whichever window ImGui put it in.
/// Placement is the host's; pixels are a painter's; the walk is the walker's.
/// </summary>
internal sealed class PortalHost
{
    /// <summary>
    /// One portal's retained body. <see cref="LegacyCrystarium.FloatingSurface.Popup"/>
    /// and <see cref="LegacyCrystarium.ScrollRegion"/> both take delegates, so a
    /// closure per frame would allocate on every warm frame of an open menu.
    /// The two delegates are built once per portal path; the walk writes its
    /// per-frame inputs into the fields instead of capturing them.
    /// </summary>
    internal sealed class PortalBody
    {
        internal readonly Action Surface;
        internal readonly Action<LegacyCrystarium.ScrollRegionScope> Scroll;

        internal int Node;
        internal ulong Hash;
        internal float Scale;

        internal PortalBody(PortalHost host)
        {
            Surface = () => host.RunPortalSurface(this);
            Scroll = region => host.RunPortalScroll(this, region);
        }
    }

    private readonly FrameArena _arena;
    private readonly IdentityCache _ids;
    private readonly FrameWalker _walker;

    internal PortalHost(FrameArena arena, IdentityCache ids, FrameWalker walker)
    {
        _arena = arena;
        _ids = ids;
        _walker = walker;
    }

    /// <summary>
    /// Declares the floating surface. Unconditionally: <c>Popup</c> is itself
    /// the open test, so the retained tree never has to know whether the menu
    /// is up — the declaration is the same either way and the surface's
    /// lifetime stays entirely inside the input kernel.
    /// </summary>
    internal void Declare(
        int node, in ElementRecord record, ulong hash, ulong anchorHash,
        Vector2 origin, float scale)
    {
        IdentityCache.IdEntry entry = _ids.Entry(hash);
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
            _ids.AlternateId(anchorHash, "_popup"),
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
        if (_arena.GetObject(_arena[node].PainterSlot) is IPortalSurfacePainter surface)
            surface.Paint(
                ImGui.GetWindowDrawList(), min, min + ImGui.GetWindowSize());

        float viewport = _arena[node].ScrollRegionHeight;
        if (viewport <= 0f)
        {
            WalkPortalChildren(body, ImGui.GetCursorScreenPos(), 0f);
            return;
        }

        Poser.UI.LegacyCrystarium.ScrollRegion(
            _ids.AlternateId(body.Hash, "-scroll"),
            ImGui.GetContentRegionAvail().X / body.Scale,
            viewport,
            body.Scroll);
    }

    private void RunPortalScroll(
        PortalBody body, Poser.UI.LegacyCrystarium.ScrollRegionScope region)
    {
        // The subtree places itself, but ItemSpacing.Y would still inflate the
        // scrolled content extent past the last row. Balanced under a finally
        // because a throwing build leaves the style stack the caller's problem
        // otherwise, and ImGui never recovers from an unbalanced one.
        Vector2 spacing = ImGui.GetStyle().ItemSpacing;
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(spacing.X, 0f));
        try
        {
            float cap = _arena[body.Node].Arg != 0
                ? region.ContentWidth * body.Scale
                : 0f;
            WalkPortalChildren(body, ImGui.GetCursorScreenPos(), cap);
        }
        finally
        {
            ImGui.PopStyleVar();
        }
    }

    private void WalkPortalChildren(PortalBody body, Vector2 origin, float hitWidthCap)
        => _walker.WalkDetachedChildren(
            body.Node, origin, body.Scale, body.Hash, hitWidthCap);
}
