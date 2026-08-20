using System.Reflection;
using Poser.Application.Animation;
using Poser.Domain.Animation;
using Poser.Domain.Identity;

namespace Poser.Game.Tests.Animation;

/// <summary>
/// Held expression ownership at the session/runtime boundary. Both UI surfaces
/// share the first restore point, frozen speed, and retryable release.
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
    public void Reapplying_expression_holds_zero_speed_and_keeps_first_restore_point()
    {
        var port = FakePort.Create();
        var session = new AnimationSession(port.Port);
        Assert.True(session.HoldExpression(Actor, Smile).Success);
        port.LiveFacialTimeline = Smile;
        Assert.True(session.HoldExpression(Actor, Frown).Success);

        Assert.Equal(Frown, session.HeldExpressionFor(Actor));
        Assert.Equal(Incoming, session.OverridesFor(Actor).SlotCaptures[AnimationSlot.Facial]);
        Assert.Equal(0f,
            session.OverridesFor(Actor).SlotSpeeds[AnimationSlot.Facial]);
        Assert.Equal(1f,
            session.OverridesFor(Actor).SlotSpeedCaptures[AnimationSlot.Facial]);
        Assert.Equal(2, port.Calls.Count(
            call => call == "SetSlotSpeed:Facial:0"));
    }

    [Fact]
    public void Expression_release_failure_preserves_retryable_ownership()
    {
        var port = FakePort.Create();
        var session = new AnimationSession(port.Port);
        Assert.True(session.HoldExpression(Actor, Smile).Success);
        port.BlendFailure = "facial restore unavailable";
        Assert.False(session.ReleaseExpression(Actor).Success);
        Assert.Equal(Smile, session.HeldExpressionFor(Actor));
        Assert.Equal(Incoming, session.OverridesFor(Actor).SlotCaptures[AnimationSlot.Facial]);

        port.BlendFailure = null;
        Assert.True(session.RestoreFacialLayer(Actor).Success);
        Assert.Null(session.HeldExpressionFor(Actor));
        Assert.DoesNotContain(
            AnimationSlot.Facial,
            session.OverridesFor(Actor).SlotCaptures.Keys);
        Assert.Contains("ClearSlotSpeed:Facial", port.Calls);
    }

    [Fact]
    public void Staged_expression_choice_does_not_play_or_hold_until_apply()
    {
        var port = FakePort.Create();
        var session = new AnimationSession(port.Port);

        Assert.True(session.ChooseSlot(
            Actor, AnimationSlot.Facial, Smile).Success);

        Assert.Equal(Smile, session.SelectedFor(Actor, AnimationSlot.Facial));
        Assert.Null(session.HeldExpressionFor(Actor));
        Assert.DoesNotContain(port.Calls, call => call.StartsWith("Blend:"));
        Assert.DoesNotContain(port.Calls,
            call => call.StartsWith("SetSlotSpeed:Facial:"));

        Assert.True(session.HoldExpression(Actor, Smile).Success);
        Assert.Equal(Smile, session.HeldExpressionFor(Actor));
        Assert.Contains("SetSlotSpeed:Facial:0", port.Calls);
    }

    [Fact]
    public void Hold_failure_restores_facial_selection_before_refusing()
    {
        var port = FakePort.Create();
        port.SpeedFailure = "facial speed unavailable";
        var session = new AnimationSession(port.Port);

        var result = session.HoldExpression(Actor, Smile);

        Assert.False(result.Success);
        Assert.Null(session.HeldExpressionFor(Actor));
        Assert.Null(session.SelectedFor(Actor, AnimationSlot.Facial));
        Assert.Equal(Incoming, port.LiveFacialTimeline);
        Assert.Equal($"Blend:{Incoming}",
            port.Calls.Last(call => call.StartsWith("Blend:")));
    }

    private class FakePort : DispatchProxy
    {
        public IAnimationRuntimePort Port { get; private set; } = null!;
        public List<string> Calls { get; } = new();

        /// <summary>What the facial slot is showing right now.</summary>
        public ushort LiveFacialTimeline { get; set; } = Incoming;

        public string? BlendFailure { get; set; }
        public string? SpeedFailure { get; set; }
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
                    LiveFacialTimeline = (ushort)args[1]!;
                    args[3] = null;
                    return BlendFailure is { } refusal
                        ? AnimationPortResult.Fail(refusal)
                        : AnimationPortResult.Ok();
                case "SetSlotSpeed":
                    Calls.Add(
                        $"SetSlotSpeed:{(AnimationSlot)args![1]!}:{(float)args[2]!}");
                    return SpeedFailure is { } speedRefusal
                        ? AnimationPortResult.Fail(speedRefusal)
                        : AnimationPortResult.Ok();
                case "ClearSlotSpeed":
                    Calls.Add($"ClearSlotSpeed:{(AnimationSlot)args![1]!}");
                    return AnimationPortResult.Ok();
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
