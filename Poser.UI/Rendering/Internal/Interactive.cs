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
    int Order)
{
    public static InteractionOwner World =>
        new("world", InteractionLayer.World, 0);
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
    private static int _nextOrder;
    private static string? _exclusiveOwner;
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
        _openingBarrier = null;
    }

    public static InteractionOwner BeginOwner(
        string id,
        InteractionLayer layer,
        Vector2 min,
        Vector2 max)
    {
        var owner = new InteractionOwner(id, layer, ++_nextOrder);
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
        _exclusiveOwner = id;
        _openingBarrier = new InteractionOwner(id, layer, int.MaxValue);
    }

    public static bool OwnsExclusive(string id) =>
        string.Equals(_exclusiveOwner, id, StringComparison.Ordinal);

    public static void ReleaseExclusive(string id)
    {
        if (OwnsExclusive(id))
            _exclusiveOwner = null;
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
        InteractionOwner candidate) =>
        !string.Equals(
            occluder.Owner.Id,
            candidate.Id,
            StringComparison.Ordinal)
        && IsHigher(occluder.Owner, candidate);

    private static bool IsHigher(
        InteractionOwner left,
        InteractionOwner right) =>
        left.Layer > right.Layer
        || (left.Layer == right.Layer && left.Order > right.Order);

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
