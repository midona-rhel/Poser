using System.Reflection;
using Poser.Application.Animation;
using Poser.Domain.Animation;
using Poser.Domain.Identity;
using Poser.Domain.Scene;

namespace Poser.Game.Tests.Animation;

/// <summary>Checks animation ownership and restoration behavior.</summary>
public sealed class AnimationOwnershipTests
{
    private static readonly ActorId ActorA =
        new(Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"), 1);

    [Fact]
    public void The_scene_holds_physics_with_no_actor_involved()
    {
        var port = FakePort.Create();
        var session = new AnimationSession(port.Port);

        // This switch does not select an actor.
        Assert.True(session.SetScenePhysicsFrozen(true).Success);
        Assert.True(session.SceneOwnsPhysics);
        Assert.True(port.Frozen);

        // Repeating the current value writes nothing.
        Assert.True(session.SetScenePhysicsFrozen(true).Success);
        Assert.Equal(1, port.Calls.Count(c => c == "SetPhysicsFrozen:True"));

        Assert.True(session.SetScenePhysicsFrozen(false).Success);
        Assert.False(session.SceneOwnsPhysics);
        Assert.False(port.Frozen);
        Assert.Equal(1, port.Calls.Count(c => c == "SetPhysicsFrozen:False"));
    }

    [Fact]
    public void No_actor_reset_or_departure_can_retire_the_scenes_hold()
    {
        var port = FakePort.Create();
        var session = new AnimationSession(port.Port);
        Assert.True(session.SetScenePhysicsFrozen(true).Success);
        Assert.True(session.SetSpeed(ActorA, 0f).Success);

        // Resetting an unowned actor does nothing.
        Assert.True(session.ResetActor(ActorA).Success);
        session.Reconcile(EmptyScene(1));

        Assert.True(session.SceneOwnsPhysics);
        Assert.True(port.Frozen);
        Assert.DoesNotContain("SetPhysicsFrozen:False", port.Calls);

        // ResetAll releases the scene-wide freeze.
        Assert.True(session.ResetAll().Success);
        Assert.False(session.SceneOwnsPhysics);
        Assert.False(port.Frozen);
        Assert.Equal(1, port.Calls.Count(c => c == "SetPhysicsFrozen:False"));
    }

    [Fact]
    public void A_failed_unpatch_keeps_the_hold_on_record_and_stays_retryable()
    {
        var port = FakePort.Create();
        var session = new AnimationSession(port.Port);
        Assert.True(session.SetScenePhysicsFrozen(true).Success);
        port.FailUnfreeze = true;

        var failed = session.ResetAll();

        Assert.False(failed.Success);
        // The failure detail reaches the caller.
        Assert.Contains(
            "unpatch", failed.Detail!, StringComparison.OrdinalIgnoreCase);
        Assert.True(port.Frozen);
        // The state remains owned while active.
        Assert.True(session.SceneOwnsPhysics);

        port.FailUnfreeze = false;
        Assert.True(session.ResetAll().Success);
        Assert.False(session.SceneOwnsPhysics);
        Assert.False(port.Frozen);
    }

    [Fact]
    public void Replay_releases_a_poser_owned_pause_before_playing()
    {
        var port = FakePort.Create();
        var session = new AnimationSession(port.Port);
        Assert.True(session.SetSpeed(ActorA, 0f).Success);
        Assert.True(session.IsPaused(ActorA));

        var result = session.Replay(ActorA, 42, out bool resumed);

        Assert.True(result.Success);
        Assert.True(resumed);
        // No zero-speed state remains after replay.
        Assert.Null(session.OverridesFor(ActorA).OverallSpeed);
        Assert.False(session.IsPaused(ActorA));
        // The pause is released before playback.
        Assert.True(
            port.Calls.IndexOf("ClearOverallSpeed") < port.Calls.IndexOf("Blend:42"));
    }

    [Fact]
    public void Replay_preserves_a_nonzero_owned_speed()
    {
        var port = FakePort.Create();
        var session = new AnimationSession(port.Port);
        Assert.True(session.SetSpeed(ActorA, 0.5f).Success);

        var result = session.Replay(ActorA, 42, out bool resumed);

        Assert.True(result.Success);
        Assert.False(resumed);
        Assert.Equal(0.5f, session.OverridesFor(ActorA).OverallSpeed);
        Assert.DoesNotContain("ClearOverallSpeed", port.Calls);
    }

    [Fact]
    public void Replay_keeps_the_pause_owner_when_the_release_fails()
    {
        var port = FakePort.Create();
        var session = new AnimationSession(port.Port);
        Assert.True(session.SetSpeed(ActorA, 0f).Success);
        port.FailClearSpeed = true;

        var result = session.Replay(ActorA, 42, out bool resumed);

        Assert.False(result.Success);
        Assert.False(resumed);
        // The pause remains owned and no new timeline was played.
        Assert.Equal(0f, session.OverridesFor(ActorA).OverallSpeed);
        Assert.DoesNotContain("Blend:42", port.Calls);
    }

    [Fact]
    public void Main_pick_replaces_the_loop_and_restores_the_old_value()
    {
        var port = FakePort.Create();
        var session = new AnimationSession(port.Port);

        Assert.True(session.SetSlotLoop(
            ActorA, AnimationSlot.UpperBody, 88, true).Success);
        Assert.True(session.PlayBase(ActorA, 42).Success);
        Assert.Equal((ushort)42, port.ForcedTimeline);
        Assert.DoesNotContain("Read", port.Calls);
        Assert.Equal((uint)2,
            session.OverridesFor(ActorA).BaseCapture!.Value.ModeParam);
        Assert.Contains(
            AnimationSlot.UpperBody,
            session.OverridesFor(ActorA).LoopedSlots.Keys);
        Assert.Equal((ushort)77,
            session.OverridesFor(ActorA).BaseCapture!.Value.ForcedTimeline);

        // A new main pick replaces the previous main loop.
        Assert.True(session.PlayBase(ActorA, 43).Success);
        Assert.Equal((ushort)43, port.ForcedTimeline);
        Assert.Equal(2, port.Calls.Count(c => c == "ClearSlotLoop:Base"));
        Assert.Contains(
            AnimationSlot.UpperBody,
            session.OverridesFor(ActorA).LoopedSlots.Keys);

        Assert.True(session.ResetActor(ActorA).Success);
        Assert.Equal((ushort)77, port.ForcedTimeline);
        Assert.Null(session.OverridesFor(ActorA).BaseCapture);
        Assert.Empty(session.OverridesFor(ActorA).LoopedSlots);
        Assert.Contains("ClearLoops", port.Calls);
    }

    [Fact]
    public void Main_pick_refuses_an_unavailable_generation_without_writing()
    {
        var port = FakePort.Create();
        port.Available = false;
        var session = new AnimationSession(port.Port);

        var result = session.PlayBase(ActorA, 42);

        Assert.False(result.Success);
        Assert.DoesNotContain(port.Calls, call => call.StartsWith("Blend:"));
        Assert.DoesNotContain(port.Calls, call => call.StartsWith("SetForceLoop:"));
    }

    [Fact]
    public void Main_pick_refuses_when_persistent_looping_is_unavailable()
    {
        var port = FakePort.Create();
        port.SupportsForceLoop = false;
        var session = new AnimationSession(port.Port);

        var result = session.PlayBase(ActorA, 42);

        Assert.False(result.Success);
        Assert.Empty(port.Calls);
        Assert.False(session.OverridesFor(ActorA).HasAny);
        Assert.Empty(session.OwnedActors);
    }

    private static SceneSnapshot EmptyScene(ulong revision) =>
        new(
            revision,
            Array.Empty<ActorDescriptor>(),
            Array.Empty<LightDescriptor>(),
            Array.Empty<CameraDescriptor>(),
            Array.Empty<PropDescriptor>());

    /// <summary>Recording animation-port fake: physics patch state, one
    /// switchable unfreeze/clear failure, everything else succeeds.</summary>
    private class FakePort : DispatchProxy
    {
        public IAnimationRuntimePort Port { get; private set; } = null!;
        public List<string> Calls { get; } = new();
        public bool Frozen { get; private set; }
        public bool FailUnfreeze { get; set; }
        public bool FailClearSpeed { get; set; }
        public ushort ForcedTimeline { get; set; } = 77;
        public bool Available { get; set; } = true;
        public bool SupportsForceLoop { get; set; } = true;

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
                    return Available;
                case "get_SupportsForceLoop":
                    return SupportsForceLoop;
                case "ClearOverallSpeed":
                    Calls.Add("ClearOverallSpeed");
                    return FailClearSpeed
                        ? AnimationPortResult.Fail("clear failed")
                        : AnimationPortResult.Ok();
                case "Blend":
                    Calls.Add($"Blend:{args![1]}");
                    args[3] = null;
                    return AnimationPortResult.Ok();
                case "CaptureBase":
                    Calls.Add("CaptureBase");
                    return Available
                        ? new BaseAnimationCapture(1, 2, 3, 4, ForcedTimeline)
                        : null;
                case "SetForceLoop":
                    ForcedTimeline = (ushort)args![1]!;
                    Calls.Add($"SetForceLoop:{ForcedTimeline}");
                    return AnimationPortResult.Ok();
                case "ClearLoops":
                    Calls.Add("ClearLoops");
                    return null;
                case "SetSlotLoop":
                    Calls.Add($"SetSlotLoop:{args![1]}");
                    return AnimationPortResult.Ok();
                case "ClearSlotLoop":
                    Calls.Add($"ClearSlotLoop:{args![1]}");
                    return AnimationPortResult.Ok();
                case "RestoreBase":
                    ForcedTimeline = ((BaseAnimationCapture)args![1]!).ForcedTimeline;
                    Calls.Add("RestoreBase");
                    return AnimationPortResult.Ok();
                case "TimelineSlot":
                    return (AnimationSlot?)AnimationSlot.Base;
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
