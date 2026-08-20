using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using Poser.Application.Animation;
using Poser.Domain.Animation;
using Poser.Domain.Identity;
using Poser.Game.Animation;

namespace Poser.Game.Tests.Animation;

/// <summary>High-value native ownership invariants for animation changes.</summary>
public sealed class AnimationOwnershipTests
{
    private static readonly ActorId Actor =
        new(Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"), 1);

    [Fact]
    public void Base_repeat_restores_its_first_capture_and_refuses_an_unsafe_layout()
    {
        var port = FakePort.Create();
        var session = new AnimationSession(port.Port);

        Assert.True(session.SetSlotLoop(Actor, AnimationSlot.Base, 0, true).Success);
        Assert.True(session.PlayBase(Actor, 42).Success);
        Assert.True(session.PlayBase(Actor, 43).Success);
        Assert.Equal(port.BaseCapture, session.OverridesFor(Actor).BaseCapture);
        Assert.Equal("SetForceLoop:43", port.Calls.Last(call =>
            call.StartsWith("SetForceLoop", StringComparison.Ordinal)));

        Assert.True(session.ResetSlot(Actor, AnimationSlot.Base).Success);
        Assert.Equal(port.BaseCapture, port.RestoredBaseCapture);
        Assert.Equal(0xA1B2C3D4u, port.RestoredBaseCapture?.ModeParam);
        Assert.False(session.OverridesFor(Actor).HasAny);

        var guard = typeof(AnimationRuntimePort).GetMethod(
            "HasForcedTimelineLayoutFor",
            BindingFlags.Static | BindingFlags.NonPublic);
        var write = typeof(AnimationRuntimePort).GetMethod(
            "TrySetForcedTimelineForLayout",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(guard);
        Assert.NotNull(write);
        Assert.True((bool)guard.Invoke(null, [0x10, 0x2E2])!);
        Assert.False((bool)guard.Invoke(null, [0x10, 0x2E1])!);

        nint memory = Marshal.AllocHGlobal(0x2E2);
        try
        {
            Marshal.WriteInt16(memory, 0x2E0, 0x4A4B);
            Assert.False((bool)write.Invoke(
                null,
                [memory, (ushort)0x1234, 0x10, 0x2E1])!);
            Assert.Equal(0x4A4B, Marshal.ReadInt16(memory, 0x2E0));
        }
        finally
        {
            Marshal.FreeHGlobal(memory);
        }
    }

    [Fact]
    public void Upper_apply_and_loop_intent_preserve_independent_ownership()
    {
        var port = FakePort.Create();
        port.ReadValue = ReadingWithSlot(AnimationSlot.UpperBody, 77, 1f);
        var session = new AnimationSession(port.Port);
        var upper = new TimelineEntry(
            43, "Eat Pizza", AnimationKind.Emote, AnimationSlot.UpperBody,
            EmoteId: 300, EmoteIndex: 0);

        Assert.True(session.SetSlotLoop(Actor, AnimationSlot.Base, 0, true).Success);
        Assert.True(session.PlayBase(Actor, 42).Success);
        Assert.True(session.SetSlotLoop(
            Actor, AnimationSlot.UpperBody, 0, true).Success);
        Assert.True(session.ChooseSlot(
            Actor, AnimationSlot.UpperBody, (ushort)upper.TimelineId).Success);
        Assert.True(session.PlaySelectedSlot(
            Actor, AnimationSlot.UpperBody, upper, playFromStart: true).Success);

        int layerWrite = port.Calls.LastIndexOf("Blend:43");
        Assert.True(port.Calls.LastIndexOf("SetForceLoop:0") < layerWrite);
        Assert.True(layerWrite < port.Calls.LastIndexOf("SetForceLoop:42"));
        Assert.Equal("SetSlotLoop:UpperBody:43", port.Calls.Last());
        Assert.Equal((ushort)42,
            session.OverridesFor(Actor).LoopedSlots[AnimationSlot.Base]);
        Assert.Equal((ushort)43,
            session.OverridesFor(Actor).LoopedSlots[AnimationSlot.UpperBody]);

        Assert.True(session.SetSlotLoop(
            Actor, AnimationSlot.UpperBody, 0, false).Success);
        Assert.Equal("ClearSlotLoop:UpperBody", port.Calls.Last());
        var nextUpper = upper with { TimelineId = 44 };
        Assert.True(session.ChooseSlot(Actor, AnimationSlot.UpperBody, 44).Success);
        int toggleStart = port.Calls.Count;
        Assert.True(session.SetSlotLoop(
            Actor, AnimationSlot.UpperBody, 0, true).Success);
        Assert.Equal(toggleStart, port.Calls.Count);
        Assert.True(session.LoopWantedFor(Actor, AnimationSlot.UpperBody));
        Assert.False(session.OverridesFor(Actor).LoopedSlots.ContainsKey(
            AnimationSlot.UpperBody));

        Assert.True(session.PlaySelectedSlot(
            Actor, AnimationSlot.UpperBody, nextUpper, playFromStart: true).Success);
        Assert.Equal("SetSlotLoop:UpperBody:44", port.Calls.Last());
        Assert.True(session.ResetSlot(Actor, AnimationSlot.UpperBody).Success);
        Assert.Contains("ClearSlotLoop:UpperBody", port.Calls);
        Assert.Equal("Blend:77", port.Calls.Last(call => call.StartsWith("Blend")));
        Assert.True(session.LoopWantedFor(Actor, AnimationSlot.Base));
        Assert.False(session.LoopWantedFor(Actor, AnimationSlot.UpperBody));

        Assert.True(session.SetSlotLoop(
            Actor, AnimationSlot.Base, 42, false).Success);
        Assert.Equal("SetForceLoop:0", port.Calls.Last());
        Assert.False(session.LoopWantedFor(Actor, AnimationSlot.Base));
    }

    [Fact]
    public void Expression_reselection_and_release_keep_the_first_facial_restore_point()
    {
        var port = FakePort.Create();
        port.ReadValue = ReadingWithSlot(AnimationSlot.Facial, 77, .6f);
        var session = new AnimationSession(port.Port);

        Assert.True(session.HoldExpression(Actor, 45).Success);
        port.ReadValue = ReadingWithSlot(AnimationSlot.Facial, 45, .2f);
        Assert.True(session.HoldExpression(Actor, 46).Success);

        var owned = session.OverridesFor(Actor);
        Assert.Equal((ushort)77, owned.SlotCaptures[AnimationSlot.Facial]);
        Assert.Equal(.6f, owned.SlotSpeedCaptures[AnimationSlot.Facial]);
        Assert.Equal((ushort)46, session.HeldExpressionFor(Actor));

        int releaseStart = port.Calls.Count;
        Assert.True(session.ReleaseExpression(Actor).Success);
        Assert.Equal(
            [
                "ClearSlotSpeed:Facial:0.6",
                $"Blend:{AnimationTimelines.StraightFace}",
                "ClearSlotSpeed:Facial:0.6",
                "Blend:77",
            ],
            port.Calls.Skip(releaseStart).ToArray());
        Assert.Null(session.HeldExpressionFor(Actor));
        Assert.Null(session.SelectedFor(Actor, AnimationSlot.Facial));
        Assert.False(session.OverridesFor(Actor).SlotCaptures.ContainsKey(
            AnimationSlot.Facial));
        Assert.False(session.OverridesFor(Actor).SlotSpeedCaptures.ContainsKey(
            AnimationSlot.Facial));
    }

    private static ActorAnimationReading ReadingWithSlot(
        AnimationSlot slot, ushort timeline, float speed) =>
        ActorAnimationReading.Empty with
        {
            Slots = [new AnimationSlotReading(slot, timeline, speed)],
        };

    /// <summary>Records the native writes relevant to ownership restoration.</summary>
    private class FakePort : DispatchProxy
    {
        public IAnimationRuntimePort Port { get; private set; } = null!;
        public List<string> Calls { get; } = new();
        public ActorAnimationReading? ReadValue { get; set; }
        public BaseAnimationCapture BaseCapture { get; } =
            new(4, 0xA1B2C3D4u, 18, 27, 36);
        public BaseAnimationCapture? RestoredBaseCapture { get; private set; }

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
                case "get_SupportsForceLoop":
                    return true;
                case "TimelineSlot":
                    return (ushort)args![0]! switch
                    {
                        43 or 44 => AnimationSlot.UpperBody,
                        45 or 46 or AnimationTimelines.StraightFace =>
                            AnimationSlot.Facial,
                        _ => AnimationSlot.Base,
                    };
                case "Read":
                    return ReadValue;
                case "CaptureBase":
                    return BaseCapture;
                case "PlayBase":
                    Calls.Add($"PlayBase:{args![1]}");
                    args[3] = args[2] == null ? BaseCapture : null;
                    return AnimationPortResult.Ok();
                case "RestoreBase":
                    RestoredBaseCapture = (BaseAnimationCapture)args![1]!;
                    Calls.Add("RestoreBase");
                    return AnimationPortResult.Ok();
                case "SetForceLoop":
                    Calls.Add($"SetForceLoop:{args![1]}");
                    return AnimationPortResult.Ok();
                case "Blend":
                    Calls.Add($"Blend:{args![1]}");
                    args[3] = null;
                    return AnimationPortResult.Ok();
                case "SetSlotLoop":
                    Calls.Add($"SetSlotLoop:{args![1]}:{args[2]}");
                    return AnimationPortResult.Ok();
                case "ClearSlotLoop":
                    Calls.Add($"ClearSlotLoop:{args![1]}");
                    return AnimationPortResult.Ok();
                case "SetSlotSpeed":
                    Calls.Add(
                        $"SetSlotSpeed:{args![1]}:" +
                        ((float)args[2]!).ToString(CultureInfo.InvariantCulture));
                    return AnimationPortResult.Ok();
                case "ClearSlotSpeed":
                    Calls.Add(
                        $"ClearSlotSpeed:{args![1]}:" +
                        ((float)args[2]!).ToString(CultureInfo.InvariantCulture));
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
