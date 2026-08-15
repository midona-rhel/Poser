using System;
using System.Collections.Generic;
using Dalamud.Game.ClientState.Keys;
using Poser.Config;

namespace Poser.Tests.Core;

/// <summary>
/// The dispatch contract: a frame of held keys reaches the action bound to
/// them. These drive a FAKE key frame end to end — the runtime path polled
/// chords from the ImGui draw callback and nothing tested that a key ever
/// became a call, which is how the whole layer shipped silent.
/// </summary>
public class KeybindDispatcherTests
{
    /// <summary>One frame of keyboard state, as the binder sees it.</summary>
    private sealed class KeyFrame
    {
        private readonly HashSet<VirtualKey> _down = [];

        /// <summary>Keys the host cannot poll at all — Dalamud throws for the
        /// virtual keys the game does not map.</summary>
        public HashSet<VirtualKey> Unsupported { get; } = [];

        public KeyFrame Press(params VirtualKey[] keys)
        {
            foreach (var key in keys)
                _down.Add(key);
            return this;
        }

        public KeyFrame Release(params VirtualKey[] keys)
        {
            foreach (var key in keys)
                _down.Remove(key);
            return this;
        }

        public bool Read(VirtualKey key) =>
            !Unsupported.Contains(key) && _down.Contains(key);
    }

    private static KeybindDispatcher Dispatcher(
        params (string Id, Action Run)[] actions)
    {
        var table = new List<KeyValuePair<string, Action>>();
        foreach (var (id, run) in actions)
            table.Add(new(id, run));
        return new KeybindDispatcher(table);
    }

    private static Func<string, KeybindSlots> Bindings(
        string action, string primary, string secondary = "")
    {
        var slots = new KeybindSlots(primary, secondary);
        return id => id == action ? slots : new KeybindSlots();
    }

    [Fact]
    public void AHeldChordCallsItsAction()
    {
        int fired = 0;
        var dispatcher = Dispatcher(("Undo", () => fired++));
        var keys = new KeyFrame().Press(VirtualKey.CONTROL, VirtualKey.Z);

        dispatcher.Pump(Bindings("Undo", "Ctrl+Z"), keys.Read);

        Assert.Equal(1, fired);
    }

    [Fact]
    public void HoldingTheChordFiresOnceUntilItIsReleased()
    {
        int fired = 0;
        var dispatcher = Dispatcher(("Undo", () => fired++));
        var resolve = Bindings("Undo", "Ctrl+Z");
        var keys = new KeyFrame().Press(VirtualKey.CONTROL, VirtualKey.Z);

        dispatcher.Pump(resolve, keys.Read);
        dispatcher.Pump(resolve, keys.Read);
        dispatcher.Pump(resolve, keys.Read);
        Assert.Equal(1, fired);

        keys.Release(VirtualKey.Z);
        dispatcher.Pump(resolve, keys.Read);
        keys.Press(VirtualKey.Z);
        dispatcher.Pump(resolve, keys.Read);
        Assert.Equal(2, fired);
    }

    [Fact]
    public void AnUnheldModifierIsNotTheChord()
    {
        int fired = 0;
        var dispatcher = Dispatcher(("Undo", () => fired++));
        var keys = new KeyFrame().Press(VirtualKey.Z);

        dispatcher.Pump(Bindings("Undo", "Ctrl+Z"), keys.Read);

        Assert.Equal(0, fired);
    }

    [Fact]
    public void AnExtraModifierIsNotTheChord()
    {
        int fired = 0;
        var dispatcher = Dispatcher(("Undo", () => fired++));
        var keys = new KeyFrame().Press(
            VirtualKey.CONTROL, VirtualKey.SHIFT, VirtualKey.Z);

        dispatcher.Pump(Bindings("Undo", "Ctrl+Z"), keys.Read);

        Assert.Equal(0, fired);
    }

    [Fact]
    public void EitherSlotFiresTheAction()
    {
        int fired = 0;
        var dispatcher = Dispatcher(("Redo", () => fired++));
        var resolve = Bindings("Redo", "Ctrl+Y", "Ctrl+Shift+Z");

        var keys = new KeyFrame().Press(VirtualKey.CONTROL, VirtualKey.Y);
        dispatcher.Pump(resolve, keys.Read);
        Assert.Equal(1, fired);

        keys.Release(VirtualKey.Y);
        dispatcher.Pump(resolve, keys.Read);
        keys.Press(VirtualKey.SHIFT, VirtualKey.Z);
        dispatcher.Pump(resolve, keys.Read);
        Assert.Equal(2, fired);
    }

    /// <summary>The edge belongs to the ACTION: rolling from one of its chords
    /// onto the other is still one press.</summary>
    [Fact]
    public void RollingBetweenTheTwoSlotsDoesNotFireTwice()
    {
        int fired = 0;
        var dispatcher = Dispatcher(("Redo", () => fired++));
        var resolve = Bindings("Redo", "Ctrl+Y", "Ctrl+Shift+Z");
        var keys = new KeyFrame().Press(VirtualKey.CONTROL, VirtualKey.Y);

        dispatcher.Pump(resolve, keys.Read);
        keys.Press(VirtualKey.SHIFT, VirtualKey.Z).Release(VirtualKey.Y);
        dispatcher.Pump(resolve, keys.Read);

        Assert.Equal(1, fired);
    }

    [Fact]
    public void AnUnboundActionNeverFires()
    {
        int fired = 0;
        var dispatcher = Dispatcher(("Next tab", () => fired++));
        var keys = new KeyFrame().Press(
            VirtualKey.CONTROL, VirtualKey.SHIFT, VirtualKey.MENU);

        dispatcher.Pump(Bindings("Next tab", string.Empty), keys.Read);

        Assert.Equal(0, fired);
    }

    /// <summary>
    /// A rebind takes effect on the next pump, without the dispatcher being
    /// rebuilt: the resolver is read every frame.
    /// </summary>
    [Fact]
    public void ARebindTakesEffectOnTheNextFrame()
    {
        int fired = 0;
        var dispatcher = Dispatcher(("Undo", () => fired++));
        var slots = new KeybindSlots("Ctrl+Z");
        Func<string, KeybindSlots> resolve = _ => slots;
        var keys = new KeyFrame().Press(VirtualKey.CONTROL, VirtualKey.W);

        dispatcher.Pump(resolve, keys.Read);
        Assert.Equal(0, fired);

        slots.Primary = "Ctrl+W";
        dispatcher.Pump(resolve, keys.Read);
        Assert.Equal(1, fired);
    }

    /// <summary>
    /// A gated frame FORGETS the edge rather than holding it: the chord is
    /// judged fresh on the first frame the gate opens again.
    /// </summary>
    [Fact]
    public void SuspendingForgetsTheHeldEdge()
    {
        int fired = 0;
        var dispatcher = Dispatcher(("Undo", () => fired++));
        var resolve = Bindings("Undo", "Ctrl+Z");
        var keys = new KeyFrame().Press(VirtualKey.CONTROL, VirtualKey.Z);

        dispatcher.Pump(resolve, keys.Read);
        Assert.Equal(1, fired);

        dispatcher.Suspend();
        dispatcher.Pump(resolve, keys.Read);

        Assert.Equal(2, fired);
    }

    /// <summary>
    /// A chord naming a key the host cannot poll is unreachable — and it takes
    /// nothing else with it. Dalamud's key state throws for the virtual keys
    /// the game does not map, and the chord vocabulary is wider than that map.
    /// </summary>
    [Fact]
    public void AnUnpollableKeyOnlySilencesItsOwnAction()
    {
        int camera = 0, undo = 0;
        var dispatcher = Dispatcher(
            ("Next camera", () => camera++),
            ("Undo", () => undo++));
        var bindings = new Dictionary<string, KeybindSlots>
        {
            ["Next camera"] = new("]"),
            ["Undo"] = new("Ctrl+Z"),
        };
        var keys = new KeyFrame().Press(
            VirtualKey.OEM_6, VirtualKey.CONTROL, VirtualKey.Z);
        keys.Unsupported.Add(VirtualKey.OEM_6);

        dispatcher.Pump(id => bindings[id], keys.Read);

        Assert.Equal(0, camera);
        Assert.Equal(1, undo);
    }
}
