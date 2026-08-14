using System.Reflection;
using Poser.Application.Animation;
using Poser.Domain.Animation;
using Poser.Domain.Identity;

namespace Poser.Game.Tests.Animation;

/// <summary>
/// The one-click expression contract at the session/port boundary — the
/// flow the FACE & LIPS Expression picker drives on a single row click.
/// Brio's mechanism, verbatim: a pick blends the timeline through the
/// sequencer and pins the facial layer at speed 0; a pick while a hold is
/// active switches the expression over the pinned slot without disturbing
/// the one pre-hold restore point; release and reset hand back per the
/// Train 6 ownership rules (release only what landed, keep what failed).
/// </summary>
public sealed class ExpressionHoldTests
{
    private static readonly ActorId Actor =
        new(Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"), 1);

    /// <summary>The facial timeline the actor arrives with — the one thing
    /// restoration must put back.</summary>
    private const ushort Incoming = 777;

    private const ushort Smile = 9001;
    private const ushort Frown = 9002;

    [Fact]
    public void One_click_pick_plays_then_pins_and_registers_the_owner()
    {
        var port = FakePort.Create();
        var session = new AnimationSession(port.Port);

        var result = session.HoldExpression(Actor, Smile);

        Assert.True(result.Success);
        // Brio's order: blend first, pin second — a pin before the play
        // would freeze the OLD face.
        Assert.True(
            port.Calls.IndexOf($"Blend:{Smile}") <
            port.Calls.IndexOf("SetSlotSpeed:Facial:0"));
        // Ownership registered exactly as any pick: the held id, the pin,
        // and the pre-hold incoming timeline as the restore point.
        Assert.Equal(Smile, session.HeldExpressionFor(Actor));
        var owned = session.OverridesFor(Actor);
        Assert.Equal(0f, owned.SlotSpeeds[AnimationSlot.Facial]);
        Assert.Equal(Incoming, owned.SlotCaptures[AnimationSlot.Facial]);
    }

    [Fact]
    public void Pick_while_held_switches_without_double_registering()
    {
        var port = FakePort.Create();
        var session = new AnimationSession(port.Port);
        Assert.True(session.HoldExpression(Actor, Smile).Success);
        // The held expression is now what the facial slot shows; a naive
        // second capture would record Poser's own play as "incoming".
        port.LiveFacialTimeline = Smile;

        var result = session.HoldExpression(Actor, Frown);

        Assert.True(result.Success);
        Assert.Equal(Frown, session.HeldExpressionFor(Actor));
        // The switch is a blend over the pinned slot, then the pin again.
        int played = port.Calls.IndexOf($"Blend:{Frown}");
        Assert.True(played >= 0);
        Assert.Equal("SetSlotSpeed:Facial:0", port.Calls[played + 1]);
        var owned = session.OverridesFor(Actor);
        // One restore point, one pin — the first hold's frames never
        // became state Poser would "restore".
        Assert.Equal(Incoming, owned.SlotCaptures[AnimationSlot.Facial]);
        Assert.Single(owned.SlotSpeeds);
        Assert.Single(owned.SlotCaptures);
    }

    [Fact]
    public void Reset_after_a_switched_hold_restores_the_original_incoming_timeline()
    {
        var port = FakePort.Create();
        var session = new AnimationSession(port.Port);
        Assert.True(session.HoldExpression(Actor, Smile).Success);
        port.LiveFacialTimeline = Smile;
        Assert.True(session.HoldExpression(Actor, Frown).Success);
        port.Calls.Clear();

        var result = session.ResetActor(Actor);

        Assert.True(result.Success);
        // Release first (unpin → straight face → unpin → idle), then the
        // captured incoming timeline replayed — the PRE-hold face, not
        // either expression Poser played.
        int firstUnpin = port.Calls.IndexOf("ClearSlotSpeed:Facial");
        int straight = port.Calls.IndexOf(
            $"Blend:{AnimationTimelines.StraightFace}");
        int idle = port.Calls.IndexOf($"Blend:{AnimationTimelines.Idle}");
        int replay = port.Calls.IndexOf($"Blend:{Incoming}");
        Assert.True(firstUnpin >= 0 && firstUnpin < straight);
        Assert.True(straight < idle);
        Assert.True(idle < replay);
        Assert.False(session.OverridesFor(Actor).HasAny);
    }

    [Fact]
    public void Unavailable_actor_is_a_typed_refusal_that_registers_nothing()
    {
        var port = FakePort.Create();
        port.BlendFailure = "That actor is no longer in the scene.";
        var session = new AnimationSession(port.Port);

        var result = session.HoldExpression(Actor, Smile);

        Assert.False(result.Success);
        Assert.Equal("That actor is no longer in the scene.", result.Detail);
        // Nothing landed, so nothing is owned: no hold, no pin, no capture.
        Assert.Null(session.HeldExpressionFor(Actor));
        Assert.False(session.OverridesFor(Actor).HasAny);
        Assert.DoesNotContain("SetSlotSpeed:Facial:0", port.Calls);
    }

    [Fact]
    public void Failed_pin_does_not_register_the_hold_but_keeps_the_landed_capture()
    {
        var port = FakePort.Create();
        port.FailSetSlotSpeed = true;
        var session = new AnimationSession(port.Port);

        var result = session.HoldExpression(Actor, Smile);

        Assert.False(result.Success);
        // The hold is not owned — the face is NOT pinned, and claiming it
        // was would strand a release nobody can perform.
        Assert.Null(session.HeldExpressionFor(Actor));
        var owned = session.OverridesFor(Actor);
        Assert.Empty(owned.SlotSpeeds);
        // But the blend DID land on the facial slot, so its restore point
        // stays owned for reset — truthful ownership over tidy ownership.
        Assert.Equal(Incoming, owned.SlotCaptures[AnimationSlot.Facial]);
    }

    [Fact]
    public void Suspended_commands_refuse_the_pick()
    {
        var port = FakePort.Create();
        var session = new AnimationSession(port.Port);
        session.SuspendCommands();

        var result = session.HoldExpression(Actor, Smile);

        Assert.False(result.Success);
        Assert.Equal("A face capture is in progress.", result.Detail);
        Assert.DoesNotContain($"Blend:{Smile}", port.Calls);
        Assert.False(session.OverridesFor(Actor).HasAny);
    }

    [Fact]
    public void Release_runs_brio_order_and_clears_the_pin()
    {
        var port = FakePort.Create();
        var session = new AnimationSession(port.Port);
        Assert.True(session.HoldExpression(Actor, Smile).Success);
        port.Calls.Clear();

        var result = session.ReleaseExpression(Actor);

        Assert.True(result.Success);
        // Brio's exact order: unpin, straight face, unpin again (the game
        // may have re-registered a speed during the blend), idle.
        Assert.Equal(
            new[]
            {
                "ClearSlotSpeed:Facial",
                $"Blend:{AnimationTimelines.StraightFace}",
                "ClearSlotSpeed:Facial",
                $"Blend:{AnimationTimelines.Idle}",
            },
            port.Calls);
        Assert.Null(session.HeldExpressionFor(Actor));
        var owned = session.OverridesFor(Actor);
        Assert.Empty(owned.SlotSpeeds);
        // Release leaves the face to the animation; the hand-back of the
        // captured incoming timeline stays owned for reset.
        Assert.Equal(Incoming, owned.SlotCaptures[AnimationSlot.Facial]);
    }

    [Fact]
    public void Failed_release_keeps_the_hold_for_retry()
    {
        var port = FakePort.Create();
        var session = new AnimationSession(port.Port);
        Assert.True(session.HoldExpression(Actor, Smile).Success);
        port.FailClearSlotSpeed = true;

        var failed = session.ReleaseExpression(Actor);

        Assert.False(failed.Success);
        // The face is still (partly) held, and the record says so — that
        // is what lets the next release retry the whole sequence.
        Assert.Equal(Smile, session.HeldExpressionFor(Actor));
        Assert.Equal(
            0f, session.OverridesFor(Actor).SlotSpeeds[AnimationSlot.Facial]);

        port.FailClearSlotSpeed = false;
        var retried = session.ReleaseExpression(Actor);

        Assert.True(retried.Success);
        Assert.Null(session.HeldExpressionFor(Actor));
        Assert.Empty(session.OverridesFor(Actor).SlotSpeeds);
    }

    // ── the bake's teardown ──────────────────────────────────────────────

    [Fact]
    public void Bake_teardown_puts_the_facial_layer_back_and_leaves_the_body_alone()
    {
        var port = FakePort.Create();
        var session = new AnimationSession(port.Port);
        Assert.True(session.HoldExpression(Actor, Smile).Success);
        port.LiveFacialTimeline = Smile;
        port.Calls.Clear();

        var result = session.RestoreFacialLayer(Actor);

        Assert.True(result.Success);
        // Unpin, then the PRE-hold facial timeline — and nothing else. Brio's
        // release ends with idle (3) on the BASE slot, which is its whole-actor
        // reset button; a bake that ran it would put the body back to idle
        // every time the user baked a face.
        Assert.Equal(
            new[] { "ClearSlotSpeed:Facial", $"Blend:{Incoming}" },
            port.Calls);
        Assert.DoesNotContain(
            $"Blend:{AnimationTimelines.StraightFace}", port.Calls);
        Assert.DoesNotContain($"Blend:{AnimationTimelines.Idle}", port.Calls);
        // Nothing of the hold is left owned: the layer is back where it was.
        Assert.Null(session.HeldExpressionFor(Actor));
        Assert.False(session.OverridesFor(Actor).HasAny);
    }

    [Fact]
    public void Bake_teardown_of_an_empty_facial_layer_only_unpins()
    {
        var port = FakePort.Create();
        // The actor arrived with no facial timeline at all; 0 is not a
        // timeline to play back, it is the record that there was none.
        port.LiveFacialTimeline = 0;
        var session = new AnimationSession(port.Port);
        Assert.True(session.HoldExpression(Actor, Smile).Success);
        port.Calls.Clear();

        Assert.True(session.RestoreFacialLayer(Actor).Success);

        Assert.Equal(new[] { "ClearSlotSpeed:Facial" }, port.Calls);
        Assert.False(session.OverridesFor(Actor).HasAny);
    }

    [Fact]
    public void Bake_teardown_that_cannot_unpin_keeps_the_hold_for_the_retry()
    {
        var port = FakePort.Create();
        var session = new AnimationSession(port.Port);
        Assert.True(session.HoldExpression(Actor, Smile).Success);
        port.FailClearSlotSpeed = true;

        var failed = session.RestoreFacialLayer(Actor);

        Assert.False(failed.Success);
        Assert.Equal(Smile, session.HeldExpressionFor(Actor));
        Assert.Equal(
            Incoming, session.OverridesFor(Actor).SlotCaptures[AnimationSlot.Facial]);

        port.FailClearSlotSpeed = false;
        Assert.True(session.RestoreFacialLayer(Actor).Success);
        Assert.Null(session.HeldExpressionFor(Actor));
    }

    [Fact]
    public void A_second_bake_teardown_lands_because_the_first_left_nothing_behind()
    {
        var port = FakePort.Create();
        var session = new AnimationSession(port.Port);
        Assert.True(session.HoldExpression(Actor, Smile).Success);
        Assert.True(session.RestoreFacialLayer(Actor).Success);
        port.Calls.Clear();

        // Pressing bake again with nothing held must not replay a stale
        // timeline over the layer, and must not refuse.
        var again = session.RestoreFacialLayer(Actor);

        Assert.True(again.Success);
        Assert.Equal(new[] { "ClearSlotSpeed:Facial" }, port.Calls);
        Assert.False(session.OverridesFor(Actor).HasAny);

        // And the actor is immediately pickable again.
        port.Calls.Clear();
        Assert.True(session.HoldExpression(Actor, Frown).Success);
        Assert.Equal(Frown, session.HeldExpressionFor(Actor));
    }

    [Fact]
    public void Bake_teardown_never_touches_playback_speed()
    {
        var port = FakePort.Create();
        var session = new AnimationSession(port.Port);
        Assert.True(session.Pause(Actor).Success);
        Assert.True(session.HoldExpression(Actor, Smile).Success);
        port.Calls.Clear();

        Assert.True(session.RestoreFacialLayer(Actor).Success);

        // A Poser pause writes PlaybackSpeed 0 to every Havok control, so a
        // bake that paused or resumed would either freeze the state it has to
        // measure or resume an actor the user froze on purpose.
        Assert.DoesNotContain("SetOverallSpeed", port.Calls);
        Assert.DoesNotContain("ClearOverallSpeed", port.Calls);
        Assert.True(session.IsPaused(Actor));
    }

    /// <summary>Recording fake at the port boundary: the facial slot shows
    /// a configurable live timeline, expression timelines route to Facial
    /// (idle to Base) as the sheet would, and each expression-flow write
    /// has one switchable failure.</summary>
    private class FakePort : DispatchProxy
    {
        public IAnimationRuntimePort Port { get; private set; } = null!;
        public List<string> Calls { get; } = new();

        /// <summary>What the facial slot is showing right now.</summary>
        public ushort LiveFacialTimeline { get; set; } = Incoming;

        public string? BlendFailure { get; set; }
        public bool FailSetSlotSpeed { get; set; }
        public bool FailClearSlotSpeed { get; set; }

        public static FakePort Create()
        {
            var port = DispatchProxy.Create<IAnimationRuntimePort, FakePort>();
            var proxy = (FakePort)(object)port;
            proxy.Port = port;
            return proxy;
        }

        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            switch (method?.Name)
            {
                case "IsSupported":
                    return true;
                case "Read":
                    return new ActorAnimationReading(
                        0, 1f, 0, false, AnimationStance.Idle, 0,
                        new[]
                        {
                            new AnimationSlotReading(
                                AnimationSlot.Facial, LiveFacialTimeline, 1f),
                        },
                        Array.Empty<ScrubControlReading>(),
                        1UL);
                case "TimelineSlot":
                {
                    // The sheet's routing, reduced to this flow: idle is a
                    // base timeline, everything else here is facial.
                    ushort timeline = (ushort)args![0]!;
                    return (AnimationSlot?)(timeline == AnimationTimelines.Idle
                        ? AnimationSlot.Base
                        : AnimationSlot.Facial);
                }
                case "Blend":
                    Calls.Add($"Blend:{args![1]}");
                    args[3] = null;
                    return BlendFailure is { } refusal
                        ? AnimationPortResult.Fail(refusal)
                        : AnimationPortResult.Ok();
                case "SetSlotSpeed":
                    Calls.Add(
                        $"SetSlotSpeed:{(AnimationSlot)args![1]!}:{(float)args[2]!}");
                    return FailSetSlotSpeed
                        ? AnimationPortResult.Fail("slot speed unavailable")
                        : AnimationPortResult.Ok();
                case "ClearSlotSpeed":
                    Calls.Add($"ClearSlotSpeed:{(AnimationSlot)args![1]!}");
                    return FailClearSlotSpeed
                        ? AnimationPortResult.Fail("unpin failed")
                        : AnimationPortResult.Ok();
                default:
                    if (method?.ReturnType == typeof(AnimationPortResult))
                    {
                        Calls.Add(method.Name);
                        return AnimationPortResult.Ok();
                    }
                    if (method?.ReturnType is { IsValueType: true } type &&
                        type != typeof(void))
                        return Activator.CreateInstance(type);
                    return null;
            }
        }
    }
}
