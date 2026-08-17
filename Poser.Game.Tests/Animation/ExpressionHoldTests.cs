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
    public void Expression_pick_captures_before_pinning_and_switches_without_overwriting_restore()
    {
        var port = FakePort.Create();
        var session = new AnimationSession(port.Port);
        Assert.True(session.HoldExpression(Actor, Smile).Success);
        port.LiveFacialTimeline = Smile;
        Assert.True(session.HoldExpression(Actor, Frown).Success);

        Assert.Equal(Frown, session.HeldExpressionFor(Actor));
        Assert.Equal(Incoming, session.OverridesFor(Actor).SlotCaptures[AnimationSlot.Facial]);
        Assert.Equal(0f, session.OverridesFor(Actor).SlotSpeeds[AnimationSlot.Facial]);
        Assert.True(port.Calls.IndexOf($"Blend:{Frown}") < port.Calls.LastIndexOf("SetSlotSpeed:Facial:0"));
    }

    [Fact]
    public void Expression_release_and_bake_retry_truthfully_preserve_landed_capture()
    {
        var port = FakePort.Create();
        var session = new AnimationSession(port.Port);
        Assert.True(session.HoldExpression(Actor, Smile).Success);
        port.FailClearSlotSpeed = true;
        Assert.False(session.ReleaseExpression(Actor).Success);
        Assert.Equal(Smile, session.HeldExpressionFor(Actor));
        Assert.Equal(Incoming, session.OverridesFor(Actor).SlotCaptures[AnimationSlot.Facial]);

        port.FailClearSlotSpeed = false;
        Assert.True(session.RestoreFacialLayer(Actor).Success);
        Assert.Null(session.HeldExpressionFor(Actor));
        Assert.DoesNotContain(
            AnimationSlot.Facial,
            session.OverridesFor(Actor).SlotCaptures.Keys);
    }
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
                    LiveFacialTimeline = (ushort)args[1]!;
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
