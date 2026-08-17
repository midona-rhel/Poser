using System.Reflection;
using Poser.Application.Animation;
using Poser.Domain.Animation;
using Poser.Domain.Identity;
using Poser.Domain.Scene;

namespace Poser.Game.Tests.Animation;

/// <summary>Checks animation ownership.</summary>
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
    public void Idle_pick_keeps_replacing_the_main_timeline()
    {
        var port = FakePort.Create();
        var session = new AnimationSession(port.Port);
        var idle = new TimelineEntry(
            AnimationTimelines.Idle, "Idle", AnimationKind.RawTimeline, AnimationSlot.Base);

        Assert.True(session.PlayEntry(ActorA, idle, asBase: true, playFromStart: true).Success);
        Assert.Equal(AnimationTimelines.Idle, port.CurrentTimeline);
        Assert.Equal(AnimationTimelines.Idle, port.ForcedTimeline);
        port.SetCurrentTimeline(42);
        port.AdvanceFrame();
        Assert.Equal(AnimationTimelines.Idle, port.CurrentTimeline);
        Assert.Contains($"SetForceLoop:{AnimationTimelines.Idle}", port.Calls);
    }

    [Fact]
    public void Non_idle_main_pick_returns_after_one_playback()
    {
        var port = FakePort.Create();
        var session = new AnimationSession(port.Port);
        var entry = new TimelineEntry(
            42, "One shot", AnimationKind.RawTimeline, AnimationSlot.Base);

        Assert.True(session.PlayEntry(ActorA, entry, asBase: true, playFromStart: true).Success);
        Assert.Equal((ushort)42, port.CurrentTimeline);
        Assert.DoesNotContain(port.Calls, call => call.StartsWith("SetForceLoop:"));
        port.AdvanceFrame();
        Assert.Equal(AnimationTimelines.Idle, port.CurrentTimeline);
    }

    [Fact]
    public void Main_pick_refuses_when_persistent_looping_is_unavailable()
    {
        var port = FakePort.Create();
        port.SupportsForceLoop = false;
        var session = new AnimationSession(port.Port);

        var result = session.PlayBase(ActorA, AnimationTimelines.Idle);

        Assert.False(result.Success);
        Assert.DoesNotContain("CaptureBase", port.Calls);
        Assert.DoesNotContain(port.Calls, call => call.StartsWith("SetForceLoop:"));
        Assert.DoesNotContain(port.Calls, call => call.StartsWith("Blend:"));
        Assert.DoesNotContain(port.Calls, call => call.StartsWith("ClearSlotLoop:"));
        Assert.Empty(port.Calls);
        Assert.False(session.OverridesFor(ActorA).HasAny);
        Assert.Empty(session.OwnedActors);
    }

    [Fact]
    public void Persistent_loop_rejects_a_non_idle_timeline()
    {
        var port = FakePort.Create();
        var session = new AnimationSession(port.Port);

        var result = session.SetForceLoop(ActorA, 42);

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

    /// <summary>Recording animation-port fake.</summary>
    private class FakePort : DispatchProxy
    {
        public IAnimationRuntimePort Port { get; private set; } = null!;
        public List<string> Calls { get; } = new();
        public bool Frozen { get; private set; }
        public bool FailUnfreeze { get; set; }
        public bool FailClearSpeed { get; set; }
        public ushort ForcedTimeline { get; private set; } = 77;
        public ushort CurrentTimeline { get; private set; } = 77;
        public bool SupportsForceLoop { get; set; } = true;

        public void AdvanceFrame() =>
            CurrentTimeline = ForcedTimeline == 0
                ? AnimationTimelines.Idle
                : ForcedTimeline;

        public void SetCurrentTimeline(ushort timeline) => CurrentTimeline = timeline;

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
                case "ClearOverallSpeed":
                    Calls.Add("ClearOverallSpeed");
                    return FailClearSpeed
                        ? AnimationPortResult.Fail("clear failed")
                        : AnimationPortResult.Ok();
                case "Blend":
                    Calls.Add($"Blend:{args![1]}");
                    args[3] ??= new BaseAnimationCapture(
                        1, 2, 3, CurrentTimeline, ForcedTimeline);
                    ForcedTimeline = 0;
                    CurrentTimeline = (ushort)args[1]!;
                    return AnimationPortResult.Ok();
                case "CaptureBase":
                    Calls.Add("CaptureBase");
                    return new BaseAnimationCapture(
                        1, 2, 3, CurrentTimeline, ForcedTimeline);
                case "SetForceLoop":
                    ForcedTimeline = (ushort)args![1]!;
                    Calls.Add($"SetForceLoop:{ForcedTimeline}");
                    return AnimationPortResult.Ok();
                case "ClearSlotLoop":
                    Calls.Add($"ClearSlotLoop:{args![1]}");
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
