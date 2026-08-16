using System.Reflection;
using Poser.Application.Animation;
using Poser.Domain.Animation;
using Poser.Domain.Identity;

namespace Poser.Game.Tests.Animation;

/// <summary>Checks expression hold, release, and restoration behavior.</summary>
public sealed class ExpressionHoldTests
{
    private static readonly ActorId Actor =
        new(Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"), 1);

    /// <summary>The actor's initial facial timeline.</summary>
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
        // Play before pinning so the new expression is held.
        Assert.True(
            port.Calls.IndexOf($"Blend:{Smile}") <
            port.Calls.IndexOf("SetSlotSpeed:Facial:0"));
        // The held id, pin, and incoming timeline are recorded.
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
        // The live facial timeline now matches the held expression.
        port.LiveFacialTimeline = Smile;

        var result = session.HoldExpression(Actor, Frown);

        Assert.True(result.Success);
        Assert.Equal(Frown, session.HeldExpressionFor(Actor));
        // The replacement is blended and pinned again.
        int played = port.Calls.IndexOf($"Blend:{Frown}");
        Assert.True(played >= 0);
        Assert.Equal("SetSlotSpeed:Facial:0", port.Calls[played + 1]);
        var owned = session.OverridesFor(Actor);
        // The original incoming timeline remains the restore point.
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
        // Release happens before the original timeline is replayed.
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
        // Nothing landed, so nothing is owned.
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
        // The hold is not owned, so the face is not pinned.
        Assert.Null(session.HeldExpressionFor(Actor));
        var owned = session.OverridesFor(Actor);
        Assert.Empty(owned.SlotSpeeds);
        // The blend changed the facial slot, so its restore point stays owned.
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
    public void Release_clears_the_pin_in_order()
    {
        var port = FakePort.Create();
        var session = new AnimationSession(port.Port);
        Assert.True(session.HoldExpression(Actor, Smile).Success);
        port.Calls.Clear();

        var result = session.ReleaseExpression(Actor);

        Assert.True(result.Success);
        // Release clears the pin and restores the facial layer.
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
        // Release leaves the face to the animation and keeps its restore state.
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
        // The partial hold remains recorded for the next release.
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
        // Restore the facial layer and its incoming timeline.
        Assert.Equal(
            new[] { "ClearSlotSpeed:Facial", $"Blend:{Incoming}" },
            port.Calls);
        Assert.DoesNotContain(
            $"Blend:{AnimationTimelines.StraightFace}", port.Calls);
        Assert.DoesNotContain($"Blend:{AnimationTimelines.Idle}", port.Calls);
        // The facial layer is restored and no hold remains.
        Assert.Null(session.HeldExpressionFor(Actor));
        Assert.False(session.OverridesFor(Actor).HasAny);
    }

    /// <summary>An empty facial layer is restored to neutral.</summary>
    [Fact]
    public void Bake_teardown_of_an_empty_facial_layer_neutralises_it()
    {
        var port = FakePort.Create();
        port.LiveFacialTimeline = 0;
        var session = new AnimationSession(port.Port);
        Assert.True(session.HoldExpression(Actor, Smile).Success);
        port.Calls.Clear();

        Assert.True(session.RestoreFacialLayer(Actor).Success);

        Assert.Equal(
            new[]
            {
                "ClearSlotSpeed:Facial",
                $"Blend:{AnimationTimelines.StraightFace}",
            },
            port.Calls);
        // The base slot is not touched by facial teardown.
        Assert.DoesNotContain($"Blend:{AnimationTimelines.Idle}", port.Calls);
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

        // A second bake with no hold leaves the facial layer unchanged.
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

        // Facial teardown leaves overall speed unchanged.
        Assert.DoesNotContain("SetOverallSpeed", port.Calls);
        Assert.DoesNotContain("ClearOverallSpeed", port.Calls);
        Assert.True(session.IsPaused(Actor));
    }

    /// <summary>Recording fake for expression operations.</summary>
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
                        });
                case "TimelineSlot":
                {
                    // Idle uses the base slot.
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
