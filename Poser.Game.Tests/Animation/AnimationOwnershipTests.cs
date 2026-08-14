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
    public void The_scene_holds_physics_with_no_actor_involved()
    {
        var port = FakePort.Create();
        var session = new AnimationSession(port.Port);

        // No actor is involved at all: the shell's switch stands over every
        // selection and over none.
        Assert.True(session.SetScenePhysicsFrozen(true).Success);
        Assert.True(session.SceneOwnsPhysics);
        Assert.True(port.Frozen);

        // Asking again for what is already held writes nothing.
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

        // Resetting an actor, and losing it from the scene entirely, are both
        // silent on a patch no actor holds.
        Assert.True(session.ResetActor(ActorA).Success);
        session.Reconcile(EmptyScene(1));

        Assert.True(session.SceneOwnsPhysics);
        Assert.True(port.Frozen);
        Assert.DoesNotContain("SetPhysicsFrozen:False", port.Calls);

        // ResetAll is the one release: the scene is the only owner, and it is
        // not something that can depart.
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
        // The native's own reason reaches the caller: a reset that says only
        // "failed" cannot be acted on, and this is the one channel the port's
        // detail travels on.
        Assert.Contains(
            "unpatch", failed.Detail!, StringComparison.OrdinalIgnoreCase);
        Assert.True(port.Frozen);
        // The frozen scene still has its owner on record — never a patched
        // site nobody admits to.
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
        // No zero-speed owner survives the replay.
        Assert.Null(session.OverridesFor(ActorA).OverallSpeed);
        Assert.False(session.IsPaused(ActorA));
        // The pause is released BEFORE the play, so the timeline starts moving.
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
        // Truthful: the pause is still owned, and nothing was played over it.
        Assert.Equal(0f, session.OverridesFor(ActorA).OverallSpeed);
        Assert.DoesNotContain("Blend:42", port.Calls);
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
