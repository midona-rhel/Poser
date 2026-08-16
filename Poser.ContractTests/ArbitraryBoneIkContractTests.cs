using Dalamud.Plugin.Services;
using NSubstitute;
using Poser.Application.Posing;
using Poser.Application.Transforms;
using Poser.ContractTests.Fixtures;
using Poser.Domain.Identity;
using Poser.Domain.Posing;
using Poser.Entities;
using Poser.Game.Posing;
using Poser.Services;

namespace Poser.ContractTests;

/// <summary>
/// The stable-id contract for IK on a bone that heads no declared chain.
///
/// <para>Poser used to answer "can this bone use IK?" with a NAME TEST against
/// the four declared arm and leg endpoints. Brio answers it with a skeleton
/// fact — a parent for CCD to bend — so the port now forwards the question to
/// the runtime and carries the CCD depth and iteration count through
/// unchanged. The second half characterized here is the chain LIST: with CCD
/// armable on any bone, every all-chains surface (the overlay's armed tinting,
/// any enable-all / disable-all control) reads one enumeration per skeleton
/// instead of probing bone by bone.</para>
/// </summary>
public sealed class ArbitraryBoneIkContractTests
{
    [Fact]
    public void Support_is_the_runtimes_answer_rather_than_an_endpoint_name_test()
    {
        using var app = new PoseImportCaptureHarness();
        var port = Port(app);

        // j_mab_l is no declared endpoint, but it has a parent — the runtime
        // hands back CCD defaults and the port must follow.
        app.Posing.GetIkConfiguration(app.FaceBone)
            .Returns(IkChainConfig.DefaultsForCcd());
        app.Posing.GetIkConfiguration(app.Bone).Returns((IkChainConfig?)null);

        Assert.True(port.IsSupported(Target(app, "j_mab_l")));
        Assert.False(port.IsSupported(Target(app, "j_kao")));
        // A declared chain is still the only thing offering Two Joint.
        Assert.False(port.IsTwoJointAvailable(Target(app, "j_mab_l")));
    }

    [Fact]
    public void Depth_and_iterations_reach_the_runtime_unchanged()
    {
        using var app = new PoseImportCaptureHarness();
        var port = Port(app);
        IkChainConfig? stored = null;
        app.Posing.SetIkConfiguration(app.FaceBone, Arg.Any<IkChainConfig>())
            .Returns(call =>
            {
                stored = call.Arg<IkChainConfig>();
                return (string?)null;
            });

        var armed = IkChainConfig.DefaultsForCcd(enabled: true) with
        {
            CcdDepth = 12,
            CcdIterations = 41,
            CcdGain = 0.25f,
        };
        Assert.True(port.Set(Target(app, "j_mab_l"), armed).Success);

        Assert.NotNull(stored);
        Assert.Equal(IkSolver.Ccd, stored!.Solver);
        Assert.Equal(12, stored.CcdDepth);
        Assert.Equal(41, stored.CcdIterations);
        Assert.Equal(0.25f, stored.CcdGain);
        Assert.True(stored.Enabled);
    }

    [Fact]
    public void A_runtime_refusal_comes_back_as_the_ports_own_failure_detail()
    {
        using var app = new PoseImportCaptureHarness();
        var port = Port(app);
        app.Posing.SetIkConfiguration(app.FaceBone, Arg.Any<IkChainConfig>())
            .Returns("Only the CCD solver works on a bone with no arm or leg chain.");

        var result = port.Set(
            Target(app, "j_mab_l"),
            IkChainConfig.DefaultsForCcd() with { Solver = IkSolver.TwoJoint });

        Assert.False(result.Success);
        Assert.Contains("CCD", result.Detail);
    }

    [Fact]
    public void Resetting_an_undeclared_bone_restores_the_ccd_defaults_and_keeps_it_armed()
    {
        using var app = new PoseImportCaptureHarness();
        var port = Port(app);
        app.Posing.GetIkConfiguration(app.FaceBone).Returns(
            IkChainConfig.DefaultsForCcd(enabled: true) with { CcdDepth = 17 });
        IkChainConfig? stored = null;
        app.Posing.SetIkConfiguration(app.FaceBone, Arg.Any<IkChainConfig>())
            .Returns(call =>
            {
                stored = call.Arg<IkChainConfig>();
                return (string?)null;
            });

        Assert.True(port.ResetDefaults(Target(app, "j_mab_l")).Success);

        Assert.NotNull(stored);
        Assert.Equal(IkChainConfig.DefaultsForCcd(enabled: true), stored);
    }

    [Fact]
    public void Resetting_a_bone_the_runtime_refuses_never_reaches_a_write()
    {
        using var app = new PoseImportCaptureHarness();
        var port = Port(app);
        app.Posing.GetIkConfiguration(app.Bone).Returns((IkChainConfig?)null);

        Assert.False(port.ResetDefaults(Target(app, "j_kao")).Success);
        app.Posing.DidNotReceive().SetIkConfiguration(
            app.Bone, Arg.Any<IkChainConfig>());
    }

    [Fact]
    public void The_chain_list_carries_each_configured_chain_and_the_bones_it_moves()
    {
        using var app = new PoseImportCaptureHarness();
        var port = Port(app);
        var armed = IkChainConfig.DefaultsForCcd(enabled: true) with
        {
            CcdDepth = 4,
        };
        app.Posing.GetIkChains(app.Skeleton).Returns(new[]
        {
            new IkConfiguredChain(
                app.FaceBone, armed, new[] { "j_mab_l", "j_kao" }),
        });

        var chains = port.Chains(SkeletonOf(app));

        var chain = Assert.Single(chains);
        Assert.Equal("j_mab_l", chain.Endpoint.CanonicalName);
        Assert.Equal(armed, chain.Config);
        Assert.Equal(new[] { "j_mab_l", "j_kao" }, chain.Bones);
    }

    [Fact]
    public void A_chain_whose_endpoint_no_longer_binds_is_dropped_from_the_list()
    {
        using var app = new PoseImportCaptureHarness();
        var port = Port(app);
        var stranger = Substitute.For<IBone>();
        stranger.BoneName.Returns("j_ex_met_a01");
        app.Posing.GetIkChains(app.Skeleton).Returns(new[]
        {
            new IkConfiguredChain(
                stranger,
                IkChainConfig.DefaultsForCcd(enabled: true),
                new[] { "j_ex_met_a01" }),
        });

        Assert.Empty(port.Chains(SkeletonOf(app)));
    }

    [Fact]
    public void An_unbound_skeleton_has_no_chains_rather_than_a_throw()
    {
        using var app = new PoseImportCaptureHarness();
        var port = Port(app);
        var stale = new SkeletonId(ActorId.New(), PoseSlot.Character, 9);

        Assert.Empty(port.Chains(stale));
    }

    // ── fixtures ────────────────────────────────────────────────────────────

    private static IkConfigurationPort Port(PoseImportCaptureHarness app) =>
        new(app.Bindings, app.Posing, app.Gestures, Substitute.For<IPluginLog>());

    private static SkeletonId SkeletonOf(PoseImportCaptureHarness app) =>
        app.Scene.Snapshot.Actors[0]
            .GetSkeleton(PoseSlot.Character)!.Id;

    private static TransformTargetId Target(
        PoseImportCaptureHarness app,
        string canonicalName)
    {
        foreach (var skeleton in app.Scene.Snapshot.Actors[0].Skeletons)
            foreach (var bone in skeleton.Bones)
                if (bone.Id.CanonicalName == canonicalName)
                    return TransformTargetId.ForBone(bone.Id);
        throw new InvalidOperationException(
            $"The pose fixture carries no bone named {canonicalName}.");
    }
}
