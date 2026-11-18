using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Poser.UI.Widgets;

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
/// What <see cref="Interactive.Reserve"/> found for one control: where it was
/// placed, whether it is hovered, pressed or disabled, whether it was clicked,
/// and how a drag on it is going. Controls never ask ImGui for this themselves.
/// Everything here already ignores input aimed at a window or popup on top.
/// </summary>
public readonly struct InteractionResult
{
    public readonly Vector2 ScreenMin;
    public readonly Vector2 ScreenMax;
    public readonly PseudoState State;
    public readonly bool Clicked;
    public readonly bool Activated;
    /// <summary>Right click while enabled, hovered and not covered by a window or popup.</summary>
    public readonly bool RightClicked;
    /// <summary>Left double-click while hovered, with the same checks as
    /// <see cref="Clicked"/>.</summary>
    public readonly bool DoubleClicked;
    /// <summary>True on the frame the control was pressed, if the press
    /// counted: the control is enabled and nothing is drawn over it. A
    /// press that did not count reports neither this nor
    /// <see cref="DragEnded"/>.</summary>
    public readonly bool DragBegan;
    /// <summary>True on the frame a counted drag is released. It comes exactly
    /// once after each <see cref="DragBegan"/>, even if a popup opens over the
    /// control during the drag.</summary>
    public readonly bool DragEnded;
    /// <summary>How far the mouse moved this frame during a counted drag on
    /// this control; zero otherwise.</summary>
    public readonly Vector2 DragDelta;
    public readonly InteractionOwner Owner;

    public InteractionResult(
        Vector2 min,
        Vector2 max,
        PseudoState state,
        bool clicked,
        bool activated,
        bool doubleClicked,
        bool dragBegan,
        bool dragEnded,
        Vector2 dragDelta,
        InteractionOwner owner,
        bool rightClicked = false)
    {
        ScreenMin = min;
        ScreenMax = max;
        State = state;
        Clicked = clicked;
        Activated = activated;
        RightClicked = rightClicked;
        DoubleClicked = doubleClicked;
        DragBegan = dragBegan;
        DragEnded = dragEnded;
        DragDelta = dragDelta;
        Owner = owner;
    }

    public Vector2 Size => ScreenMax - ScreenMin;
    public bool Hovered => (State & PseudoState.Hover) != 0;
    public bool Active => (State & PseudoState.Active) != 0;
    public bool Disabled => (State & PseudoState.Disabled) != 0;
}

/// <summary>
/// Decides which window, popup, control, tooltip or in-world overlay gets the
/// mouse and keyboard, by drawing order. It keeps where everything was this
/// frame and last frame, so an overlay drawn before the normal windows can
/// still see what they covered last frame.
/// </summary>
public static class Interactive
{
    /// <summary>Where windows and popups cover the screen, which one is being drawn, the
    /// chain of open popups that hold the keyboard, and the drag in progress.</summary>
    internal sealed class InteractionState
    {
        internal List<Occluder> PreviousOccluders = new();
        internal List<Occluder> CurrentOccluders = new();
        internal readonly PointerQuery[] PointerQueries =
            new PointerQuery[PointerQueryCapacity];
        internal int PointerQueryGeneration = 1;
        internal readonly List<InteractionOwner> OwnerStack = new();
        internal readonly List<ExclusiveNode> ExclusiveChain = new();
        internal int NextOrder;
        internal int NextSurfaceToken;
        internal int Frame;
        internal InteractionOwner? OpeningBarrier;
        // The ImGui id of the control whose press counted (enabled, nothing
        // drawn over it). This is what pairs DragBegan with DragEnded: a press
        // on a covered control records nothing, so it never reports a release,
        // while a counted drag still reports its release if a popup opens over
        // the control during the drag.
        internal uint? DragOwner;
        // An action is running (see ActionGate): controls start nothing new.
        internal bool Busy;
        // The running action is a long one: controls look disabled.
        internal bool Greyed;
        // How many AllowWhileBusy scopes are open.
        internal int AllowedDepth;
    }

    private static InteractionState State => UiContext.Current.Interaction;

    internal readonly record struct Occluder(
        InteractionOwner Owner,
        Vector2 Min,
        Vector2 Max);

    // Reserve asks the same question, about the same window and mouse position,
    // once for every control in a window. The answers are kept in a small fixed
    // table that allocates nothing. Any change to where windows are, or to which
    // popup holds the keyboard, clears it.
    internal struct PointerQuery
    {
        public int Generation;
        public InteractionOwner Owner;
        public Vector2 Point;
        public bool Result;
    }

    private const int PointerQueryCapacity = 64;
    internal sealed class ExclusiveNode
    {
        public required string Id;
        public required InteractionLayer Layer;
        public required int Token;
        public int LastSeenFrame;
    }

    public static InteractionOwner CurrentOwner =>
        State.OwnerStack.Count == 0
            ? InteractionOwner.World
            : State.OwnerStack[^1];

    /// <summary>True while an action is running and this control may not start another one.
    /// A drag that was already accepted still reports its movement and its release.</summary>
    public static bool Blocked => State.Busy && State.AllowedDepth == 0;

    /// <summary>True while a long action runs: a control that can look disabled should.
    /// Controls read this next to their own disabled state.</summary>
    public static bool GreyedOut => State.Greyed && State.AllowedDepth == 0;

    /// <summary>Tells the controls, for this frame, whether an action is running and whether
    /// it is a long one.</summary>
    public static void SetBusy(bool busy, bool greyOut)
    {
        State.Busy = busy;
        State.Greyed = greyOut;
    }

    /// <summary>Ends what <see cref="SetBusy"/> began for this frame.</summary>
    public static void EndBusy() => SetBusy(false, false);

    /// <summary>Controls drawn inside this scope stay usable while an action runs. It is for
    /// the running action's own controls, such as its Cancel button.</summary>
    public static AllowedScope AllowWhileBusy()
    {
        State.AllowedDepth++;
        return default;
    }

    public readonly struct AllowedScope : IDisposable
    {
        public void Dispose() => State.AllowedDepth--;
    }

    public static void BeginFrame()
    {
        (State.PreviousOccluders, State.CurrentOccluders) =
            (State.CurrentOccluders, State.PreviousOccluders);
        State.CurrentOccluders.Clear();
        State.OwnerStack.Clear();
        State.NextOrder = 0;
        State.Frame = ImGui.GetFrameCount();
        State.OpeningBarrier = null;
        ClearPointerQueries();
    }

    public static void EndFrame()
    {
        for (int i = 0; i < State.ExclusiveChain.Count; i++)
        {
            if (State.ExclusiveChain[i].LastSeenFrame == State.Frame)
                continue;
            State.ExclusiveChain.RemoveRange(i, State.ExclusiveChain.Count - i);
            break;
        }
        ClearPointerQueries();
    }

    public static InteractionOwner BeginOwner(
        string id,
        InteractionLayer layer,
        Vector2 min,
        Vector2 max)
    {
        int surfaceToken = 0;
        for (int i = State.ExclusiveChain.Count - 1; i >= 0; i--)
        {
            if (!string.Equals(
                    State.ExclusiveChain[i].Id,
                    id,
                    StringComparison.Ordinal))
                continue;
            surfaceToken = State.ExclusiveChain[i].Token;
            State.ExclusiveChain[i].LastSeenFrame = State.Frame;
            break;
        }
        var owner = new InteractionOwner(
            id, layer, ++State.NextOrder, surfaceToken);
        State.OwnerStack.Add(owner);
        State.CurrentOccluders.Add(new Occluder(owner, min, max));
        ClearPointerQueries();
        return owner;
    }

    public static void EndOwner(InteractionOwner owner)
    {
        if (State.OwnerStack.Count > 0 && State.OwnerStack[^1] == owner)
        {
            State.OwnerStack.RemoveAt(State.OwnerStack.Count - 1);
            return;
        }

        // During exception unwinding an inner window or popup may still be on the stack.
        // Pop through it without throwing so End does not replace the original
        // exception.
        for (int i = State.OwnerStack.Count - 1; i >= 0; i--)
        {
            if (State.OwnerStack[i] != owner)
                continue;
            State.OwnerStack.RemoveRange(i, State.OwnerStack.Count - i);
            return;
        }

        // Not on the stack at all: a genuine misuse, worth its exception.
        throw new InvalidOperationException(
            $"Interaction owner stack mismatch for '{owner.Id}'.");
    }

    public static void RegisterOccluder(Vector2 min, Vector2 max)
    {
        State.CurrentOccluders.Add(new Occluder(CurrentOwner, min, max));
        ClearPointerQueries();
    }

    public static void RegisterOccluder(
        InteractionOwner owner,
        Vector2 min,
        Vector2 max) =>
        AddOccluder(owner, min, max);

    private static void AddOccluder(
        InteractionOwner owner,
        Vector2 min,
        Vector2 max)
    {
        State.CurrentOccluders.Add(new Occluder(owner, min, max));
        ClearPointerQueries();
    }

    public static void ClaimExclusive(
        string id,
        InteractionLayer layer = InteractionLayer.Popup)
    {
        int parentIndex = -1;
        int parentToken = CurrentOwner.SurfaceToken;
        if (parentToken != 0)
            parentIndex = State.ExclusiveChain.FindIndex(
                node => node.Token == parentToken);

        int existingIndex = State.ExclusiveChain.FindIndex(
            node => string.Equals(node.Id, id, StringComparison.Ordinal));
        ExclusiveNode node;
        if (existingIndex >= 0
            && (existingIndex == parentIndex
                || existingIndex == parentIndex + 1))
        {
            if (existingIndex + 1 < State.ExclusiveChain.Count)
                State.ExclusiveChain.RemoveRange(
                    existingIndex + 1,
                    State.ExclusiveChain.Count - existingIndex - 1);
            node = State.ExclusiveChain[existingIndex];
        }
        else
        {
            int keep = parentIndex + 1;
            if (keep < State.ExclusiveChain.Count)
                State.ExclusiveChain.RemoveRange(
                    keep, State.ExclusiveChain.Count - keep);
            node = new ExclusiveNode
            {
                Id = id,
                Layer = layer,
                Token = ++State.NextSurfaceToken,
            };
            State.ExclusiveChain.Add(node);
        }
        node.LastSeenFrame = State.Frame;
        State.OpeningBarrier = new InteractionOwner(
            id, layer, int.MaxValue, node.Token);
        ClearPointerQueries();
    }

    public static bool OwnsExclusive(string id) =>
        State.ExclusiveChain.Exists(
            node => string.Equals(node.Id, id, StringComparison.Ordinal));

    public static void TouchExclusive(string id)
    {
        var node = State.ExclusiveChain.Find(
            candidate => string.Equals(
                candidate.Id, id, StringComparison.Ordinal));
        if (node != null)
            node.LastSeenFrame = State.Frame;
    }

    public static void ReleaseExclusive(string id)
    {
        int index = State.ExclusiveChain.FindIndex(
            node => string.Equals(node.Id, id, StringComparison.Ordinal));
        if (index >= 0)
        {
            State.ExclusiveChain.RemoveRange(
                index, State.ExclusiveChain.Count - index);
            ClearPointerQueries();
        }
    }

    public static bool PointerOccluded() =>
        PointerOccluded(CurrentOwner, ImGui.GetMousePos());

    public static bool PointerOccluded(
        InteractionOwner owner,
        Vector2 point)
    {
        ref readonly var cached = ref State.PointerQueries[
            PointerQueryIndex(owner, point)];
        if (cached.Generation == State.PointerQueryGeneration
            && cached.Owner == owner
            && cached.Point == point)
            return cached.Result;

        if (State.OpeningBarrier is { } barrier
            && IsHigher(barrier, owner))
        {
            StorePointerQuery(owner, point, true);
            return true;
        }

        bool result = HighestAt(point, owner) is not null;
        StorePointerQuery(owner, point, result);
        return result;
    }

    /// <summary>
    /// Who gets the keyboard does not depend on where things are on screen.
    /// While popups that hold the keyboard are open, the newest one gets it,
    /// whether or not it covers the control: a popup off to one side still
    /// takes Enter away from the button behind it. On the frame a popup opens
    /// it has not registered its area yet, so position could not decide anyway.
    ///
    /// Only the newest popup gets the keyboard, not the ones it opened from. A
    /// modal with a dropdown open inside it gives the keyboard to the dropdown,
    /// so Enter cannot reach the modal's own controls behind it. Those controls
    /// still react to the mouse as usual, and get the keyboard back as soon as
    /// the dropdown closes.
    /// </summary>
    private static bool KeyboardDisowned(InteractionOwner owner)
    {
        if (State.OpeningBarrier is { } barrier && IsHigher(barrier, owner))
            return true;
        return State.ExclusiveChain.Count > 0
            && SurfaceIndex(owner.SurfaceToken) != State.ExclusiveChain.Count - 1;
    }

    public static bool RectOccluded(
        InteractionOwner owner,
        Vector2 min,
        Vector2 max)
    {
        foreach (var occluder in State.CurrentOccluders)
            if (Blocks(occluder, owner) && Intersects(occluder, min, max))
                return true;
        foreach (var occluder in State.PreviousOccluders)
            if (Blocks(occluder, owner) && Intersects(occluder, min, max))
                return true;
        return false;
    }

    public static bool TryGetOwnerBounds(
        string id,
        out Vector2 min,
        out Vector2 max)
    {
        bool found = false;
        var boundsMin = new Vector2(float.MaxValue);
        var boundsMax = new Vector2(float.MinValue);
        Include(State.PreviousOccluders);
        Include(State.CurrentOccluders);
        min = found ? boundsMin : default;
        max = found ? boundsMax : default;
        return found;

        void Include(List<Occluder> occluders)
        {
            foreach (var occluder in occluders)
            {
                if (!string.Equals(
                        occluder.Owner.Id,
                        id,
                        StringComparison.Ordinal))
                    continue;
                boundsMin = Vector2.Min(boundsMin, occluder.Min);
                boundsMax = Vector2.Max(boundsMax, occluder.Max);
                found = true;
            }
        }
    }

    public static InteractionResult Reserve(
        string id,
        Vector2 size,
        bool disabled,
        bool activateOnSpace = false)
    {
        var owner = CurrentOwner;
        var min = ImGui.GetCursorScreenPos();
        // Disabled controls cannot take keyboard focus or activate; the
        // item still reserves layout and hit-tests for HoverHelp.
        if (disabled)
            ImGui.BeginDisabled();
        bool pressed = ImGui.InvisibleButton(id, size);
        bool focused = ImGui.IsItemFocused();
        if (disabled)
            ImGui.EndDisabled();
        var max = min + size;

        var mouse = ImGui.GetMousePos();
        // A running action swallows presses the same way a surface on top does.
        bool occluded = PointerOccluded(owner, mouse) || Blocked;
        // Only one control is hovered at a time: the mouse must be inside this
        // control's own area, whatever ImGui says about its neighbours. The area
        // includes its top and left edges but not its bottom and right edges, so
        // two rows that touch never both light up.
        bool inside = mouse.X >= min.X && mouse.X < max.X
            && mouse.Y >= min.Y && mouse.Y < max.Y;
        bool hovered = ImGui.IsItemHovered() && inside && !disabled && !occluded;
        bool active = ImGui.IsItemActive() && !disabled && !occluded;
        bool clicked = ImGui.IsItemClicked() && !disabled && !occluded;
        bool rightClicked = hovered && ImGui.IsMouseClicked(ImGuiMouseButton.Right);
        bool doubleClicked = hovered
            && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left);
        // Drags use ImGui's own press and release, but only for the control
        // whose press counted, not for whatever is uncovered now. The mouse
        // movement is reported while that control is held. A disabled control,
        // or a press on a control with a popup over it, never counts and
        // reports neither the start nor the end.
        uint itemId = ImGui.GetID(id);
        bool dragBegan = false;
        if (!disabled && !occluded && ImGui.IsItemActivated())
        {
            dragBegan = true;
            State.DragOwner = itemId;
        }
        bool dragEnded = ImGui.IsItemDeactivated() && State.DragOwner == itemId;
        if (dragEnded)
            State.DragOwner = null;
        var dragDelta = active && State.DragOwner == itemId
            ? ImGui.GetIO().MouseDelta
            : Vector2.Zero;
        // A key press has no mouse position to check, so it gets two other
        // checks. The main one: while a popup that holds the keyboard is open,
        // only that popup's controls get keys, wherever they are on screen. The
        // second: a control covered by an ordinary window or popup gets no keys
        // either. Both apply to Enter and Space below, and to ImGui's own
        // keyboard activation, which arrives through `pressed` on the same frame.
        bool enterPressed = focused && !disabled
            && (ImGui.IsKeyPressed(ImGuiKey.Enter)
                || ImGui.IsKeyPressed(ImGuiKey.KeypadEnter));
        bool spacePressed = focused && !disabled
            && ImGui.IsKeyPressed(ImGuiKey.Space);
        bool keyboardBlocked = (enterPressed || spacePressed)
            && (Blocked || KeyboardDisowned(owner)
                || RectOccluded(owner, min, max));
        // ImGui's button counts a click only when the mouse is released over
        // it, so pressing, dragging off and releasing does nothing. Enter also
        // activates. Space activates only the controls that ask for it with
        // activateOnSpace, so other controls are not affected.
        bool activated =
            pressed && !disabled && !occluded && !keyboardBlocked;
        if (!keyboardBlocked
            && (enterPressed || (activateOnSpace && spacePressed)))
            activated = true;

        PseudoState state = PseudoState.None;
        if (hovered) state |= PseudoState.Hover;
        if (active) state |= PseudoState.Active;
        if (disabled) state |= PseudoState.Disabled;

        return new InteractionResult(
            min, max, state, clicked, activated,
            doubleClicked, dragBegan, dragEnded, dragDelta, owner, rightClicked);
    }

    private static Occluder? HighestAt(
        Vector2 point,
        InteractionOwner candidate)
    {
        Occluder? highest = null;
        Consider(State.PreviousOccluders);
        Consider(State.CurrentOccluders);
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
        State.ExclusiveChain.FindIndex(node => node.Token == token);

    private static void ClearPointerQueries()
    {
        State.PointerQueryGeneration++;
    }

    private static void StorePointerQuery(
        InteractionOwner owner,
        Vector2 point,
        bool result)
    {
        int index = PointerQueryIndex(owner, point);

        State.PointerQueries[index] = new PointerQuery
        {
            Generation = State.PointerQueryGeneration,
            Owner = owner,
            Point = point,
            Result = result,
        };
    }

    private static int PointerQueryIndex(
        InteractionOwner owner,
        Vector2 point)
    {
        unchecked
        {
            uint hash = (uint)owner.Order;
            hash = (hash * 16777619u) ^ (uint)owner.Layer;
            hash = (hash * 16777619u) ^ (uint)owner.SurfaceToken;
            hash = (hash * 16777619u) ^
                (uint)BitConverter.SingleToInt32Bits(point.X);
            hash = (hash * 16777619u) ^
                (uint)BitConverter.SingleToInt32Bits(point.Y);
            return (int)(hash & (PointerQueryCapacity - 1));
        }
    }

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
