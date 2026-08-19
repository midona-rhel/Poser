using System.Reflection;
using Poser.Application.Animation;
using Poser.Domain.Animation;
using Poser.Domain.Identity;
using Poser.Domain.Scene;

namespace Poser.Game.Tests.Animation;

/// <summary>
/// Ownership-truth contracts for <see cref="AnimationSession"/>: the scene's
/// hold on the global physics patch is recorded only once the patch landed and
/// released only once the unpatch did, no actor can retire it, and Replay is a
/// resuming act that never retains a zero-speed owner.
/// </summary>
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
    public void Probe_boundaries_preserve_the_existing_loop_write()
    {
        var port = FakePort.Create();
        var session = new AnimationSession(port.Port);

        Assert.True(session.SetSlotLoop(ActorA, AnimationSlot.Base, 42, true).Success);

        Assert.Equal(
            ["ProbeBegin", "SetSlotLoop", "ProbeComplete:True"],
            port.Calls);
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
        public List<AnimationProbeCommand> ProbeCommands { get; } = new();
        public bool Frozen { get; private set; }
        public bool FailUnfreeze { get; set; }
        public bool FailClearSpeed { get; set; }

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
                case "ClearOverallSpeed":
                    Calls.Add("ClearOverallSpeed");
                    return FailClearSpeed
                        ? AnimationPortResult.Fail("clear failed")
                        : AnimationPortResult.Ok();
                case "Blend":
                    Calls.Add($"Blend:{args![1]}");
                    args[3] = null;
                    return AnimationPortResult.Ok();
                case "BeginSlotProbeCommand":
                    Calls.Add("ProbeBegin");
                    ProbeCommands.Add((AnimationProbeCommand)args![1]!);
                    return null;
                case "CompleteSlotProbeCommand":
                    Calls.Add($"ProbeComplete:{args![2]}");
                    return null;
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
