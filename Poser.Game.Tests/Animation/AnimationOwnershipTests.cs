using System.Reflection;
using System.Runtime.InteropServices;
using Poser.Application.Animation;
using Poser.Domain.Animation;
using Poser.Domain.Identity;
using Poser.Domain.Scene;
using Poser.Game.Animation;

namespace Poser.Game.Tests.Animation;

/// <summary>Runtime ownership and retry contracts for animation changes.</summary>
public sealed class AnimationOwnershipTests
{
    private static readonly ActorId ActorA =
        new(Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"), 1);

    [Fact]
    public void Scene_physics_hold_is_owned_once_and_failed_unpatch_is_retryable()
    {
        var port = FakePort.Create();
        var session = new AnimationSession(port.Port);
        Assert.True(session.SetScenePhysicsFrozen(true).Success);
        Assert.True(session.SetScenePhysicsFrozen(true).Success);
        Assert.Equal(1, port.Calls.Count(x => x == "SetPhysicsFrozen:True"));

        port.FailUnfreeze = true;
        Assert.False(session.ResetAll().Success);
        Assert.True(session.SceneOwnsPhysics);
        port.FailUnfreeze = false;
        Assert.True(session.ResetAll().Success);
        Assert.False(session.SceneOwnsPhysics);
        Assert.Equal(2, port.Calls.Count(x => x == "SetPhysicsFrozen:False"));
    }

    [Fact]
    public void Replay_releases_only_a_poser_pause_and_preserves_nonzero_speed()
    {
        var pausedPort = FakePort.Create();
        var paused = new AnimationSession(pausedPort.Port);
        Assert.True(paused.SetSpeed(ActorA, 0f).Success);
        Assert.True(paused.Replay(ActorA, 42, out var resumed).Success);
        Assert.True(resumed);
        Assert.Null(paused.OverridesFor(ActorA).OverallSpeed);
        Assert.True(pausedPort.Calls.IndexOf("ClearOverallSpeed") < pausedPort.Calls.IndexOf("Blend:42"));

        var playingPort = FakePort.Create();
        var playing = new AnimationSession(playingPort.Port);
        Assert.True(playing.SetSpeed(ActorA, .5f).Success);
        Assert.True(playing.Replay(ActorA, 42, out resumed).Success);
        Assert.False(resumed);
        Assert.Equal(.5f, playing.OverridesFor(ActorA).OverallSpeed);
        Assert.DoesNotContain("ClearOverallSpeed", playingPort.Calls);
    }

    [Fact]
    public void Probe_boundaries_preserve_the_existing_blend_write()
    {
        var port = FakePort.Create();
        var session = new AnimationSession(port.Port);

        Assert.True(session.Blend(ActorA, 42).Success);

        Assert.Equal(
            ["ProbeBegin", "Blend:42", "ProbeComplete:True"],
            port.Calls);
    }

    [Fact]
    public void Probe_boundaries_preserve_the_repeat_arm_write()
    {
        var port = FakePort.Create();
        var session = new AnimationSession(port.Port);

        Assert.True(session.SetSlotLoop(ActorA, AnimationSlot.Base, 42, true).Success);

        Assert.Equal(
            ["ProbeBegin", "SetForceLoop:42", "ProbeComplete:True"],
            port.Calls);
    }

    [Fact]
    public void Base_repeat_is_sticky_but_never_arms_without_a_selection()
    {
        var port = FakePort.Create();
        port.ReadValue = ReadingWithBase(AnimationTimelines.Idle);
        var session = new AnimationSession(port.Port);

        Assert.True(session.SetSlotLoop(ActorA, AnimationSlot.Base, 0, true).Success);

        Assert.True(session.LoopWantedFor(ActorA, AnimationSlot.Base));
        Assert.Equal(0, port.CaptureBaseCalls);
        Assert.Null(session.OverridesFor(ActorA).BaseCapture);
        Assert.Null(session.OverridesFor(ActorA).BaseTimeline);
        Assert.DoesNotContain("Read", port.Calls);
        Assert.DoesNotContain(port.Calls, call => call.StartsWith("SetForceLoop"));

        Assert.True(session.PlayBase(ActorA, 42).Success);
        Assert.Equal(port.BaseCapture, session.OverridesFor(ActorA).BaseCapture);
        Assert.Equal((ushort)42, session.OverridesFor(ActorA).BaseTimeline);
        Assert.Contains("SetForceLoop:42", port.Calls);
    }

    [Fact]
    public void Failed_repeat_arm_rolls_base_back_without_claiming_the_failed_play()
    {
        var port = FakePort.Create();
        var session = new AnimationSession(port.Port);
        Assert.True(session.SetSlotLoop(ActorA, AnimationSlot.Base, 0, true).Success);
        port.FailForceLoop = true;

        var result = session.PlayBase(ActorA, 42);

        Assert.False(result.Success);
        Assert.Equal(port.BaseCapture, port.RestoredBaseCapture);
        var owned = session.OverridesFor(ActorA);
        Assert.True(owned.LoopWantedSlots.Contains(AnimationSlot.Base));
        Assert.Null(owned.BaseCapture);
        Assert.Null(owned.BaseTimeline);
        Assert.False(owned.LoopedSlots.ContainsKey(AnimationSlot.Base));
    }

    [Fact]
    public void Failed_repeat_arm_and_rollback_keep_base_owned_for_reset_retry()
    {
        var port = FakePort.Create();
        var session = new AnimationSession(port.Port);
        Assert.True(session.SetSlotLoop(ActorA, AnimationSlot.Base, 0, true).Success);
        port.FailForceLoop = true;
        port.FailRestoreBase = true;

        var result = session.PlayBase(ActorA, 42);

        Assert.False(result.Success);
        Assert.Contains("Rollback failed", result.Detail);
        var owned = session.OverridesFor(ActorA);
        Assert.Equal(port.BaseCapture, owned.BaseCapture);
        Assert.Equal((ushort)42, owned.BaseTimeline);
        Assert.False(owned.LoopedSlots.ContainsKey(AnimationSlot.Base));

        port.FailRestoreBase = false;
        Assert.True(session.ResetActor(ActorA).Success);
        Assert.Equal(2, port.RestoreBaseCalls);
        Assert.False(session.OverridesFor(ActorA).HasAny);
    }

    [Fact]
    public void Base_selection_arms_only_sticky_repeat_and_retargets_atomically()
    {
        var port = FakePort.Create();
        var session = new AnimationSession(port.Port);
        Assert.True(session.SetSlotLoop(ActorA, AnimationSlot.Base, 0, true).Success);

        Assert.True(session.PlayBase(ActorA, 42).Success);
        Assert.True(session.PlayBase(ActorA, 43).Success);

        Assert.Equal(
            ["PlayBase:42", "SetForceLoop:42", "PlayBase:43", "SetForceLoop:43"],
            port.Calls.Where(call => call.StartsWith("PlayBase") ||
                call.StartsWith("SetForceLoop")).ToArray());
    }

    [Fact]
    public void Base_selection_without_repeat_never_arms_the_forced_timeline()
    {
        var port = FakePort.Create();
        var session = new AnimationSession(port.Port);

        Assert.True(session.PlayBase(ActorA, 42).Success);

        Assert.Contains("PlayBase:42", port.Calls);
        Assert.DoesNotContain(port.Calls, call => call.StartsWith("SetForceLoop"));
    }

    [Fact]
    public void Layer_selection_suspends_forced_full_body_repeat_without_reforcing()
    {
        var port = FakePort.Create();
        var session = new AnimationSession(port.Port);
        Assert.True(session.SetSlotLoop(ActorA, AnimationSlot.Base, 0, true).Success);
        Assert.True(session.PlayBase(ActorA, 42).Success);

        var result = session.Blend(ActorA, 43);

        Assert.True(result.Success);
        Assert.True(session.LoopWantedFor(ActorA, AnimationSlot.Base));
        Assert.True(session.OverridesFor(ActorA).BaseRepeatSuspended);
        Assert.False(session.OverridesFor(ActorA).LoopedSlots.ContainsKey(AnimationSlot.Base));
        int upper = port.Calls.LastIndexOf("Blend:43");
        Assert.True(upper >= 0);
        Assert.DoesNotContain(port.Calls.Skip(upper + 1),
            call => call.StartsWith("SetForceLoop"));
    }

    [Fact]
    public void Base_retarget_after_layer_selection_keeps_forced_repeat_suspended()
    {
        var port = FakePort.Create();
        var session = new AnimationSession(port.Port);
        Assert.True(session.SetSlotLoop(ActorA, AnimationSlot.Base, 0, true).Success);
        Assert.True(session.PlayBase(ActorA, 42).Success);
        Assert.True(session.Blend(ActorA, 43).Success);
        int upper = port.Calls.LastIndexOf("Blend:43");

        Assert.True(session.PlayBase(ActorA, 44).Success);

        Assert.True(session.OverridesFor(ActorA).BaseRepeatSuspended);
        Assert.DoesNotContain(port.Calls.Skip(upper + 1),
            call => call.StartsWith("SetForceLoop"));
    }

    [Fact]
    public void Native_base_and_upper_use_the_sequencer_without_forced_repeat()
    {
        var port = FakePort.Create();
        var session = new AnimationSession(port.Port);

        Assert.True(session.PlayBase(ActorA, 42).Success);
        Assert.True(session.Blend(ActorA, 43).Success);

        Assert.False(session.OverridesFor(ActorA).BaseRepeatSuspended);
        Assert.Contains("Blend:43", port.Calls);
        Assert.DoesNotContain(port.Calls, call => call.StartsWith("SetForceLoop"));
    }

    [Fact]
    public void Repeated_upper_selection_never_rearms_full_body_repeat()
    {
        var port = FakePort.Create();
        var session = new AnimationSession(port.Port);

        Assert.True(session.Blend(ActorA, 43).Success);
        Assert.True(session.Blend(ActorA, 44).Success);

        Assert.Contains("Blend:43", port.Calls);
        Assert.Contains("Blend:44", port.Calls);
        Assert.DoesNotContain(port.Calls, call => call.StartsWith("SetForceLoop"));
    }

    [Fact]
    public void Actor_departure_restores_a_suspended_repeat_baseline_without_reforcing()
    {
        var port = FakePort.Create();
        var session = new AnimationSession(port.Port);
        Assert.True(session.SetSlotLoop(ActorA, AnimationSlot.Base, 0, true).Success);
        Assert.True(session.PlayBase(ActorA, 42).Success);
        Assert.True(session.Blend(ActorA, 43).Success);
        int upper = port.Calls.LastIndexOf("Blend:43");

        session.Reconcile(EmptyScene(1));

        Assert.Equal(port.BaseCapture, port.RestoredBaseCapture);
        Assert.DoesNotContain(port.Calls.Skip(upper + 1),
            call => call == "SetForceLoop:42");
    }

    [Fact]
    public void Global_restore_uses_the_immutable_first_base_capture()
    {
        var port = FakePort.Create();
        var session = new AnimationSession(port.Port);

        Assert.True(session.PlayBase(ActorA, 42).Success);
        Assert.True(session.PlayBase(ActorA, 43).Success);
        Assert.True(session.ResetActor(ActorA).Success);

        Assert.Equal(port.BaseCapture, port.RestoredBaseCapture);
    }

    [Fact]
    public void Repeat_arm_captures_the_preexisting_base_for_restore()
    {
        var port = FakePort.Create();
        var session = new AnimationSession(port.Port);

        Assert.True(session.SetSlotLoop(ActorA, AnimationSlot.Base, 42, true).Success);
        Assert.True(session.ResetActor(ActorA).Success);

        Assert.Equal(port.BaseCapture, port.RestoredBaseCapture);
    }

    [Fact]
    public void Restore_preserves_the_full_mode_parameter()
    {
        var port = FakePort.Create();
        var session = new AnimationSession(port.Port);

        Assert.True(session.PlayBase(ActorA, 42).Success);
        Assert.True(session.ResetActor(ActorA).Success);

        Assert.Equal(0xA1B2C3D4u, port.RestoredBaseCapture?.ModeParam);
    }

    [Fact]
    public void Repeat_refuses_when_the_runtime_layout_is_unavailable()
    {
        var port = FakePort.Create();
        port.SupportsForceLoop = false;
        var session = new AnimationSession(port.Port);

        var result = session.SetSlotLoop(ActorA, AnimationSlot.Base, 42, true);

        Assert.False(result.Success);
        Assert.Contains("client layout", result.Detail);
        Assert.DoesNotContain(port.Calls, call => call.StartsWith("SetForceLoop"));
    }

    [Fact]
    public void Forced_timeline_layout_matches_the_current_generated_container()
    {
        var field = typeof(AnimationRuntimePort).GetField(
            "HasForcedTimelineLayout",
            BindingFlags.Static | BindingFlags.NonPublic);

        Assert.NotNull(field);
        Assert.True((bool)field.GetValue(null)!);

        var invariant = typeof(AnimationRuntimePort).GetMethod(
            "HasForcedTimelineLayoutFor",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(invariant);
        Assert.True((bool)invariant.Invoke(null, [0x10, 0x2E2])!);
        Assert.False((bool)invariant.Invoke(null, [0x10, 0x2E1])!);
    }

    [Fact]
    public void Forced_timeline_write_refuses_an_out_of_bounds_layout()
    {
        var write = typeof(AnimationRuntimePort).GetMethod(
            "TrySetForcedTimelineForLayout",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(write);
        nint memory = Marshal.AllocHGlobal(0x2E2);
        try
        {
            Marshal.WriteInt16(memory, 0x2E0, 0x4A4B);

            bool wrote = (bool)write.Invoke(
                null,
                [memory, (ushort)0x1234, 0x10, 0x2E1])!;

            Assert.False(wrote);
            Assert.Equal(0x4A4B, Marshal.ReadInt16(memory, 0x2E0));
        }
        finally
        {
            Marshal.FreeHGlobal(memory);
        }
    }

    [Fact]
    public void Repeat_off_clears_only_the_base_repeat_arm()
    {
        var port = FakePort.Create();
        var session = new AnimationSession(port.Port);
        Assert.True(session.SetSlotLoop(ActorA, AnimationSlot.Base, 0, true).Success);
        Assert.True(session.PlayBase(ActorA, 42).Success);
        int blends = port.Calls.Count(call => call.StartsWith("Blend"));

        Assert.True(session.SetSlotLoop(ActorA, AnimationSlot.Base, 42, false).Success);

        Assert.False(session.LoopWantedFor(ActorA, AnimationSlot.Base));
        Assert.Equal(blends, port.Calls.Count(call => call.StartsWith("Blend")));
        Assert.Equal("SetForceLoop:0", port.Calls.Last(call => call.StartsWith("SetForceLoop")));
    }

    [Fact]
    public void Unverified_layer_repeat_refuses_without_writes()
    {
        var port = FakePort.Create();
        var session = new AnimationSession(port.Port);

        var result = session.SetSlotLoop(ActorA, AnimationSlot.UpperBody, 42, true);

        Assert.False(result.Success);
        Assert.DoesNotContain(port.Calls, call => call == "SetSlotLoop" ||
            call.StartsWith("SetForceLoop"));
    }

    [Fact]
    public void Layer_speed_does_not_write_overall_speed()
    {
        var port = FakePort.Create();
        var session = new AnimationSession(port.Port);

        Assert.True(session.SetSlotSpeed(ActorA, AnimationSlot.UpperBody, .08f).Success);

        Assert.Contains("SetSlotSpeed", port.Calls);
        Assert.DoesNotContain("SetOverallSpeed", port.Calls);
    }

    [Fact]
    public void Probe_records_loop_arm_and_disarm_intent()
    {
        var port = FakePort.Create();
        var session = new AnimationSession(port.Port);

        Assert.True(session.SetSlotLoop(ActorA, AnimationSlot.Base, 42, true).Success);
        Assert.True(session.SetSlotLoop(ActorA, AnimationSlot.Base, 42, false).Success);

        Assert.Equal(new bool?[] { true, false }, port.ProbeCommands
            .Where(command => command.Name == "slot-loop")
            .Select(command => command.Enabled));
    }
    private static ActorAnimationReading ReadingWithBase(ushort timeline) =>
        ActorAnimationReading.Empty with
        {
            Slots = [new AnimationSlotReading(AnimationSlot.Base, timeline, 1f)],
        };

    private static SceneSnapshot EmptyScene(ulong revision) =>
        new(
            revision,
            Array.Empty<ActorDescriptor>(),
            Array.Empty<LightDescriptor>(),
            Array.Empty<CameraDescriptor>(),
            Array.Empty<PropDescriptor>());

    /// <summary>Recording port with switchable ownership failures.</summary>
    private class FakePort : DispatchProxy
    {
        public IAnimationRuntimePort Port { get; private set; } = null!;
        public List<string> Calls { get; } = new();
        public List<AnimationProbeCommand> ProbeCommands { get; } = new();
        public bool Frozen { get; private set; }
        public bool SupportsForceLoop { get; set; } = true;
        public bool FailUnfreeze { get; set; }
        public bool FailClearSpeed { get; set; }
        public bool FailForceLoop { get; set; }
        public bool FailRestoreBase { get; set; }
        public int CaptureBaseCalls { get; private set; }
        public int RestoreBaseCalls { get; private set; }
        public ActorAnimationReading? ReadValue { get; set; }

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
                case "get_IsPhysicsFrozen":
                    return Frozen;
                case "SetPhysicsFrozen":
                {
                    bool frozen = (bool)args![0]!;
                    Calls.Add($"SetPhysicsFrozen:{frozen}");
                    if (!frozen && FailUnfreeze)
                        return AnimationPortResult.Fail("native unpatch failed");
                    Frozen = frozen;
                    return AnimationPortResult.Ok();
                }
                case "IsSupported":
                    return true;
                case "get_SupportsForceLoop":
                    return SupportsForceLoop;
                case "TimelineSlot":
                    return (ushort)args![0]! is 43 or 44
                        ? AnimationSlot.UpperBody
                        : AnimationSlot.Base;
                case "Read":
                    Calls.Add("Read");
                    return ReadValue;
                case "CaptureBase":
                    CaptureBaseCalls++;
                    return BaseCapture;
                case "ClearOverallSpeed":
                    Calls.Add("ClearOverallSpeed");
                    return FailClearSpeed
                        ? AnimationPortResult.Fail("clear failed")
                        : AnimationPortResult.Ok();
                case "Blend":
                    Calls.Add($"Blend:{args![1]}");
                    args[3] = null;
                    return AnimationPortResult.Ok();
                case "PlayBase":
                    Calls.Add($"PlayBase:{args![1]}");
                    if (args![2] == null)
                        args[3] = BaseCapture;
                    else
                        args[3] = null;
                    return AnimationPortResult.Ok();
                case "RestoreBase":
                    RestoreBaseCalls++;
                    RestoredBaseCapture = (BaseAnimationCapture)args![1]!;
                    Calls.Add("RestoreBase");
                    return FailRestoreBase
                        ? AnimationPortResult.Fail("base restore failed")
                        : AnimationPortResult.Ok();
                case "BeginSlotProbeCommand":
                    Calls.Add("ProbeBegin");
                    ProbeCommands.Add((AnimationProbeCommand)args![1]!);
                    return null;
                case "CompleteSlotProbeCommand":
                    Calls.Add($"ProbeComplete:{args![2]}");
                    return null;
                case "SetForceLoop":
                    Calls.Add($"SetForceLoop:{args![1]}");
                    return FailForceLoop
                        ? AnimationPortResult.Fail("repeat arm failed")
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

        public BaseAnimationCapture BaseCapture { get; } = new(4, 0xA1B2C3D4u, 18, 27, 36);
        public BaseAnimationCapture? RestoredBaseCapture { get; private set; }
    }
}
