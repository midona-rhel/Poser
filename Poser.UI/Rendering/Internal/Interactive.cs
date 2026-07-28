using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace Poser.UI;

public enum InteractionLayer
{
    World = 0,
    OverlaySurface = 50,
    Window = 100,
    FloatingWindow = 200,
    Popup = 300,
    Modal = 400,
    HoverSurface = 500,
}

public readonly record struct InteractionOwner(
    string Id,
    InteractionLayer Layer,
    int Order,
    int SurfaceToken = 0)
{
    public static InteractionOwner World =>
        new("world", InteractionLayer.World, 0, 0);
}

/// <summary>
/// Hit-test result from <see cref="Interactive.Reserve"/>: the placed rect, the
/// pseudo-state derived from hover/active/disabled, and a click flag.
/// </summary>
public readonly struct InteractionResult
{
    public readonly Vector2 ScreenMin;
    public readonly Vector2 ScreenMax;
    public readonly PseudoState State;
    public readonly bool Clicked;
    public readonly InteractionOwner Owner;

    public InteractionResult(
        Vector2 min,
        Vector2 max,
        PseudoState state,
        bool clicked,
        InteractionOwner owner)
    {
        ScreenMin = min;
        ScreenMax = max;
        State = state;
        Clicked = clicked;
        Owner = owner;
    }

    public Vector2 Size => ScreenMax - ScreenMin;
    public bool Hovered => (State & PseudoState.Hover) != 0;
    public bool Active => (State & PseudoState.Active) != 0;
    public bool Disabled => (State & PseudoState.Disabled) != 0;
}

/// <summary>
/// Ordered input ownership shared by windows, floating surfaces, controls,
/// hover help, and world overlays. Current and previous geometry are retained
/// so an overlay drawn before normal windows still sees their last complete
/// ownership map.
/// </summary>
public static class Interactive
{
    private readonly record struct Occluder(
        InteractionOwner Owner,
        Vector2 Min,
        Vector2 Max);

    private static List<Occluder> _previousOccluders = new();
    private static List<Occluder> _currentOccluders = new();
    private static readonly List<InteractionOwner> OwnerStack = new();
    private sealed class ExclusiveNode
    {
        public required string Id;
        public required InteractionLayer Layer;
        public required int Token;
        public int LastSeenFrame;
    }

    private static readonly List<ExclusiveNode> ExclusiveChain = new();
    private static int _nextOrder;
    private static int _nextSurfaceToken;
    private static int _frame;
    private static InteractionOwner? _openingBarrier;

    public static InteractionOwner CurrentOwner =>
        OwnerStack.Count == 0
            ? InteractionOwner.World
            : OwnerStack[^1];

    public static void BeginFrame()
    {
        (_previousOccluders, _currentOccluders) =
            (_currentOccluders, _previousOccluders);
        _currentOccluders.Clear();
        OwnerStack.Clear();
        _nextOrder = 0;
        _frame = ImGui.GetFrameCount();
        _openingBarrier = null;
    }

    public static void EndFrame()
    {
        for (int i = 0; i < ExclusiveChain.Count; i++)
        {
            if (ExclusiveChain[i].LastSeenFrame == _frame)
                continue;
            ExclusiveChain.RemoveRange(i, ExclusiveChain.Count - i);
            break;
        }
    }

    public static InteractionOwner BeginOwner(
        string id,
        InteractionLayer layer,
        Vector2 min,
        Vector2 max)
    {
        int surfaceToken = 0;
        for (int i = ExclusiveChain.Count - 1; i >= 0; i--)
        {
            if (!string.Equals(
                    ExclusiveChain[i].Id,
                    id,
                    StringComparison.Ordinal))
                continue;
            surfaceToken = ExclusiveChain[i].Token;
            ExclusiveChain[i].LastSeenFrame = _frame;
            break;
        }
        var owner = new InteractionOwner(
            id, layer, ++_nextOrder, surfaceToken);
        OwnerStack.Add(owner);
        _currentOccluders.Add(new Occluder(owner, min, max));
        return owner;
    }

    public static void EndOwner(InteractionOwner owner)
    {
        if (OwnerStack.Count == 0 || OwnerStack[^1] != owner)
            throw new InvalidOperationException(
                $"Interaction owner stack mismatch for '{owner.Id}'.");
        OwnerStack.RemoveAt(OwnerStack.Count - 1);
    }

    public static void RegisterOccluder(Vector2 min, Vector2 max) =>
        _currentOccluders.Add(new Occluder(CurrentOwner, min, max));

    public static void RegisterOccluder(
        InteractionOwner owner,
        Vector2 min,
        Vector2 max) =>
        _currentOccluders.Add(new Occluder(owner, min, max));

    public static void ClaimExclusive(
        string id,
        InteractionLayer layer = InteractionLayer.Popup)
    {
        int parentIndex = -1;
        int parentToken = CurrentOwner.SurfaceToken;
        if (parentToken != 0)
            parentIndex = ExclusiveChain.FindIndex(
                node => node.Token == parentToken);

        int existingIndex = ExclusiveChain.FindIndex(
            node => string.Equals(node.Id, id, StringComparison.Ordinal));
        ExclusiveNode node;
        if (existingIndex >= 0
            && (existingIndex == parentIndex
                || existingIndex == parentIndex + 1))
        {
            if (existingIndex + 1 < ExclusiveChain.Count)
                ExclusiveChain.RemoveRange(
                    existingIndex + 1,
                    ExclusiveChain.Count - existingIndex - 1);
            node = ExclusiveChain[existingIndex];
        }
        else
        {
            int keep = parentIndex + 1;
            if (keep < ExclusiveChain.Count)
                ExclusiveChain.RemoveRange(
                    keep, ExclusiveChain.Count - keep);
            node = new ExclusiveNode
            {
                Id = id,
                Layer = layer,
                Token = ++_nextSurfaceToken,
            };
            ExclusiveChain.Add(node);
        }
        node.LastSeenFrame = _frame;
        _openingBarrier = new InteractionOwner(
            id, layer, int.MaxValue, node.Token);
    }

    public static bool OwnsExclusive(string id) =>
        ExclusiveChain.Exists(
            node => string.Equals(node.Id, id, StringComparison.Ordinal));

    public static void TouchExclusive(string id)
    {
        var node = ExclusiveChain.Find(
            candidate => string.Equals(
                candidate.Id, id, StringComparison.Ordinal));
        if (node != null)
            node.LastSeenFrame = _frame;
    }

    public static void ReleaseExclusive(string id)
    {
        int index = ExclusiveChain.FindIndex(
            node => string.Equals(node.Id, id, StringComparison.Ordinal));
        if (index >= 0)
            ExclusiveChain.RemoveRange(
                index, ExclusiveChain.Count - index);
    }

    public static bool PointerOccluded() =>
        PointerOccluded(CurrentOwner, ImGui.GetMousePos());

    public static bool PointerOccluded(
        InteractionOwner owner,
        Vector2 point)
    {
        if (_openingBarrier is { } barrier
            && IsHigher(barrier, owner))
            return true;
        return HighestAt(point, owner) is not null;
    }

    public static bool RectOccluded(
        InteractionOwner owner,
        Vector2 min,
        Vector2 max)
    {
        foreach (var occluder in _currentOccluders)
            if (Blocks(occluder, owner) && Intersects(occluder, min, max))
                return true;
        foreach (var occluder in _previousOccluders)
            if (Blocks(occluder, owner) && Intersects(occluder, min, max))
                return true;
        return false;
    }

    public static InteractionResult Reserve(
        string id,
        Vector2 size,
        bool disabled)
    {
        var owner = CurrentOwner;
        var min = ImGui.GetCursorScreenPos();
        ImGui.InvisibleButton(id, size);
        var max = min + size;

        bool occluded = PointerOccluded(owner, ImGui.GetMousePos());
        bool hovered = ImGui.IsItemHovered() && !disabled && !occluded;
        bool active = ImGui.IsItemActive() && !disabled && !occluded;
        bool clicked = ImGui.IsItemClicked() && !disabled && !occluded;

        PseudoState state = PseudoState.None;
        if (hovered) state |= PseudoState.Hover;
        if (active) state |= PseudoState.Active;
        if (disabled) state |= PseudoState.Disabled;

        return new InteractionResult(
            min, max, state, clicked, owner);
    }

    private static Occluder? HighestAt(
        Vector2 point,
        InteractionOwner candidate)
    {
        Occluder? highest = null;
        Consider(_previousOccluders);
        Consider(_currentOccluders);
        return highest;

        void Consider(List<Occluder> occluders)
        {
            foreach (var occluder in occluders)
            {
                if (!Contains(occluder, point)
                    || !Blocks(occluder, candidate))
                    continue;
                if (highest == null
                    || IsHigher(occluder.Owner, highest.Value.Owner))
                    highest = occluder;
            }
        }
    }

    private static bool Blocks(
        in Occluder occluder,
        InteractionOwner candidate)
    {
        if (occluder.Owner.SurfaceToken != 0
            && !SurfaceIsActive(occluder.Owner.SurfaceToken))
            return false;
        return occluder.Owner.SurfaceToken != candidate.SurfaceToken
            && !string.Equals(
                occluder.Owner.Id,
                candidate.Id,
                StringComparison.Ordinal)
            && IsHigher(occluder.Owner, candidate);
    }

    private static bool IsHigher(
        InteractionOwner left,
        InteractionOwner right)
    {
        if (left.SurfaceToken != 0 || right.SurfaceToken != 0)
        {
            if (left.SurfaceToken == 0)
                return false;
            if (right.SurfaceToken == 0)
                return SurfaceIsActive(left.SurfaceToken);
            int leftIndex = SurfaceIndex(left.SurfaceToken);
            int rightIndex = SurfaceIndex(right.SurfaceToken);
            if (leftIndex != rightIndex)
                return leftIndex > rightIndex;
        }
        return left.Layer > right.Layer
            || (left.Layer == right.Layer && left.Order > right.Order);
    }

    private static bool SurfaceIsActive(int token) =>
        SurfaceIndex(token) >= 0;

    private static int SurfaceIndex(int token) =>
        ExclusiveChain.FindIndex(node => node.Token == token);

    private static bool Contains(in Occluder rect, Vector2 point) =>
        point.X >= rect.Min.X && point.X < rect.Max.X
        && point.Y >= rect.Min.Y && point.Y < rect.Max.Y;

    private static bool Intersects(
        in Occluder rect,
        Vector2 min,
        Vector2 max) =>
        rect.Min.X < max.X && rect.Max.X > min.X
        && rect.Min.Y < max.Y && rect.Max.Y > min.Y;
}
