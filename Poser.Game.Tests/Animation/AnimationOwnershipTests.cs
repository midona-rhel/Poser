using System.Reflection;
using Poser.Application.Animation;
using Poser.Domain.Animation;
using Poser.Domain.Identity;
using Poser.Domain.Scene;

namespace Poser.Game.Tests.Animation;

/// <summary>
/// Ownership-truth contracts for <see cref="AnimationSession"/>: the global
/// physics owner set may only shrink when the unpatch actually landed, a
/// physics-only reset reports its own failure, and Replay is a resuming act
/// that never retains a zero-speed owner.
/// </summary>
public sealed class AnimationOwnershipTests
{
    private static readonly ActorId ActorA =
        new(Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"), 1);
    private static readonly ActorId ActorB =
        new(Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"), 1);

    [Fact]
    public void Physics_only_reset_retains_the_owner_until_unfreeze_succeeds()
    {
        var port = FakePort.Create();
        var session = new AnimationSession(port.Port);
        Assert.True(session.SetPhysicsFrozen(ActorA, true).Success);
        Assert.True(port.Frozen);
        port.FailUnfreeze = true;

        var failed = session.ResetActor(ActorA);

        Assert.False(failed.Success);
        Assert.True(session.OwnsPhysics(ActorA));
        Assert.True(session.IsPhysicsFrozen);

        port.FailUnfreeze = false;
        var retried = session.ResetActor(ActorA);

        Assert.True(retried.Success);
        Assert.False(session.OwnsPhysics(ActorA));
        Assert.False(port.Frozen);
    }

    [Fact]
    public void Reset_with_overrides_reports_a_failed_unfreeze_and_keeps_the_owner()
    {
        var port = FakePort.Create();
        var session = new AnimationSession(port.Port);
        Assert.True(session.SetSpeed(ActorA, 0f).Success);
        Assert.True(session.SetPhysicsFrozen(ActorA, true).Success);
        port.FailUnfreeze = true;

        var result = session.ResetActor(ActorA);

        Assert.False(result.Success);
        Assert.Contains("unpatch", result.Detail!, StringComparison.OrdinalIgnoreCase);
        // The speed override was still released; only the physics hold stays.
        Assert.Null(session.OverridesFor(ActorA).OverallSpeed);
        Assert.True(session.OwnsPhysics(ActorA));
        Assert.True(port.Frozen);
    }

    [Fact]
    public void Reset_all_releases_physics_only_owners_with_one_native_unpatch()
    {
        var port = FakePort.Create();
        var session = new AnimationSession(port.Port);
        Assert.True(session.SetPhysicsFrozen(ActorA, true).Success);
        Assert.True(session.SetPhysicsFrozen(ActorB, true).Success);
        // The second owner joins an already-frozen scene without a write.
        Assert.Equal(1, port.Calls.Count(c => c == "SetPhysicsFrozen:True"));

        var result = session.ResetAll();

        Assert.True(result.Success);
        Assert.False(session.OwnsPhysics(ActorA));
        Assert.False(session.OwnsPhysics(ActorB));
        Assert.False(port.Frozen);
        Assert.Equal(1, port.Calls.Count(c => c == "SetPhysicsFrozen:False"));
    }

    [Fact]
    public void Reset_all_failed_unpatch_keeps_an_owner_and_stays_retryable()
    {
        var port = FakePort.Create();
        var session = new AnimationSession(port.Port);
        Assert.True(session.SetPhysicsFrozen(ActorA, true).Success);
        Assert.True(session.SetPhysicsFrozen(ActorB, true).Success);
        port.FailUnfreeze = true;

        var failed = session.ResetAll();

        Assert.False(failed.Success);
        Assert.True(port.Frozen);
        // The frozen scene still has an owner on record — never a patched
        // site nobody admits to.
        Assert.True(session.OwnsPhysics(ActorA) || session.OwnsPhysics(ActorB));

        port.FailUnfreeze = false;
        var retried = session.ResetAll();

        Assert.True(retried.Success);
        Assert.False(session.OwnsPhysics(ActorA));
        Assert.False(session.OwnsPhysics(ActorB));
        Assert.False(port.Frozen);
    }

    [Fact]
    public void The_scene_holds_physics_with_no_actor_and_outlives_every_actor()
    {
        var port = FakePort.Create();
        var session = new AnimationSession(port.Port);

        // No actor is involved at all: the shell's switch stands over every
        // selection and over none.
        Assert.True(session.SetScenePhysicsFrozen(true).Success);
        Assert.True(session.SceneOwnsPhysics);
        Assert.True(port.Frozen);

        // An actor joining and departing cannot lift the scene's own hold.
        Assert.True(session.SetPhysicsFrozen(ActorA, true).Success);
        Assert.Equal(1, port.Calls.Count(c => c == "SetPhysicsFrozen:True"));
        session.Reconcile(EmptyScene(1));
        Assert.False(session.OwnsPhysics(ActorA));
        Assert.True(port.Frozen);
        Assert.DoesNotContain("SetPhysicsFrozen:False", port.Calls);

        // Releasing the scene's hold with no other owner unpatches once.
        Assert.True(session.SetScenePhysicsFrozen(false).Success);
        Assert.False(session.SceneOwnsPhysics);
        Assert.False(port.Frozen);
        Assert.Equal(1, port.Calls.Count(c => c == "SetPhysicsFrozen:False"));
    }

    [Fact]
    public void An_actor_release_leaves_the_scenes_hold_patched()
    {
        var port = FakePort.Create();
        var session = new AnimationSession(port.Port);
        Assert.True(session.SetScenePhysicsFrozen(true).Success);
        Assert.True(session.SetPhysicsFrozen(ActorA, true).Success);

        Assert.True(session.ResetActor(ActorA).Success);

        Assert.False(session.OwnsPhysics(ActorA));
        Assert.True(session.SceneOwnsPhysics);
        Assert.True(port.Frozen);

        // Only ResetAll retires the scene: it is the one owner no reconcile
        // can ever see depart.
        Assert.True(session.ResetAll().Success);
        Assert.False(session.SceneOwnsPhysics);
        Assert.False(port.Frozen);
    }

    [Fact]
    public void Reconcile_retains_a_departed_owner_when_the_unpatch_fails()
    {
        var port = FakePort.Create();
        var session = new AnimationSession(port.Port);
        Assert.True(session.SetPhysicsFrozen(ActorA, true).Success);
        port.FailUnfreeze = true;

        session.Reconcile(EmptyScene(1));

        Assert.True(session.OwnsPhysics(ActorA));
        Assert.True(port.Frozen);

        port.FailUnfreeze = false;
        session.Reconcile(EmptyScene(2));

        Assert.False(session.OwnsPhysics(ActorA));
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
