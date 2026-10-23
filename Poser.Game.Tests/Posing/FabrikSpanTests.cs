using System.Reflection;
using System.Numerics;
using Poser.Domain.Identity;
using Poser.Domain.Posing;
using Poser.Game.Posing;
using Poser.Game.Entities;

namespace Poser.Game.Tests.Posing;

public sealed class FabrikSpanTests
{
    [Theory]
    [InlineData(IkSolver.Rope)]
    [InlineData(IkSolver.Fabrik)]
    public void Collision_displaced_handle_uses_input_position_not_hidden_target(IkSolver solver)
    {
        var nodes = Chain(3);
        for (int i = 0; i < nodes.Length; i++) { nodes[i].Name = $"link_{i}"; nodes[i].Index = i; }
        FabrikTarget Target(Vector3 p) => new(IkTargetMode.Actor, p, Quaternion.Identity, Vector3.Zero, Quaternion.Identity);
        var source = nodes.Select((n, i) => new FabrikBonePose(n.Name, 0, new(i, 0, 0),
            Quaternion.Identity, Vector3.Zero, Quaternion.Identity)).ToArray();
        var control = new FabrikControl(source, Target(Vector3.Zero), Target(new(2, 0, 0)), 0, 2, Target(new(2, 0, 0)));
        var stacks = new PoseStackStore();
        var registry = new IkChainRegistry(stacks, null!, null!, null!, null!);
        try
        {
            Assert.Null(registry.SetIkConfiguration(nodes[2].Bone, IkChainConfig.DefaultsForChain() with
            { Enabled = true, Solver = solver, ParentDepth = 2, ChildDepth = 0, Collisions = true, Fabrik = control }));
            var input = new Vector3(1, 0, 0); // Contact held the visible bone short of its target at x=2.
            var step = new Vector3(0, .1f, 0);
            var delta = registry.ClampIkTranslation(nodes[2].Bone, step, inputPosition: input);
            Assert.True(Vector3.Distance(step, delta) < 1e-5f);
            var authoredDelta = registry.ClampIkTranslation(nodes[2].Bone, step, true, input);
            Assert.True(Vector3.Distance(new Vector3(2, 0, 0) + authoredDelta, input + step) < 1e-5f);
            // Applying the same absolute input again must not accumulate motion.
            Assert.Equal(authoredDelta, registry.ClampIkTranslation(nodes[2].Bone, step, true, input));
            var outward = registry.ClampIkTranslation(nodes[2].Bone, new(10, 0, 0), inputPosition: input);
            var edge = input + outward;
            Assert.True(Vector3.Distance(new(2, 0, 0), edge) < 1e-5f);
            var inward = registry.ClampIkTranslation(nodes[2].Bone, new(-.1f, 0, 0), inputPosition: edge);
            Assert.True(Vector3.Distance(new(-.1f, 0, 0), inward) < 1e-5f);
        }
        finally { registry.DisposeChains(); }
    }

    [Fact]
    public void Ccd_affected_bones_stop_at_the_connected_partial_root()
    {
        const int depth = 5;
        // Skeleton.BuildBones connects partial roots back to partial 0.
        // That display hierarchy continues beyond the native CCD pose.
        var nodes = Chain(5);
        for (int i = 0; i < nodes.Length; i++)
        {
            nodes[i].Name = $"bone_{i}";
            nodes[i].Partial = i < 2 ? 0 : 1;
        }
        nodes[2].Hidden = true; // Structural partial root remains part of the native pose.
        var config = IkChainConfig.DefaultsForChain() with { Solver = IkSolver.Ccd, CcdDepth = depth };
        var expected = nodes.Skip(2).ToArray();

        Assert.Equal(expected.Select(n => n.Bone), IkChainShapes.NativeIkMembers(nodes[4].Bone, config));
        Assert.Equal(expected.Select(n => n.Bone), IkBakeCapture.AffectedBones(nodes[4].Bone, config));
        Assert.Equal(expected.Reverse().Select(n => n.Name), IkChainShapes.ChainMemberNames(nodes[4].Bone, config));
    }

    [Fact]
    public void Ccd_eligibility_requires_a_parent_in_the_same_native_pose()
    {
        var config = IkChainConfig.DefaultsForChain() with { Solver = IkSolver.Ccd };
        var nodes = Chain(2);
        Assert.True(IkChainShapes.IsCcdEligible(nodes[1].Bone));
        Assert.Equal(2, IkChainShapes.NativeIkMembers(nodes[1].Bone, config).Count);
        nodes[0].Partial = 1;
        Assert.False(IkChainShapes.IsCcdEligible(nodes[1].Bone));
        Assert.Single(IkChainShapes.NativeIkMembers(nodes[1].Bone, config));
    }

    [Fact]
    public void Two_joint_external_parent_excludes_shoulder_and_spine_from_the_solved_chain()
    {
        var nodes = Chain(5);
        string[] names = ["j_sebo_c", "j_sako_l", "j_ude_a_l", "j_ude_b_l", "j_te_l"];
        for (int i = 0; i < nodes.Length; i++) nodes[i].Name = names[i];
        var members = IkChainShapes.NativeIkMembers(nodes[^1].Bone, IkChainConfig.DefaultsFor(true));
        Assert.Equal(nodes.Skip(2).Select(n => n.Bone), members);
        Assert.Same(nodes[1].Bone, members[0].ParentBone);
    }

    [Fact]
    public void Ccd_external_parent_is_above_configured_depth_and_does_not_cross_partials()
    {
        var nodes = Chain(6);
        var config = IkChainConfig.DefaultsForChain() with { Solver = IkSolver.Ccd, CcdDepth = 2 };
        var members = IkChainShapes.NativeIkMembers(nodes[^1].Bone, config);
        Assert.Equal(nodes.Skip(3).Select(n => n.Bone), members);
        Assert.Same(nodes[2].Bone, members[0].ParentBone);
        nodes[3].Partial = 1;
        Assert.Equal(nodes.Skip(4).Select(n => n.Bone), IkChainShapes.NativeIkMembers(nodes[^1].Bone, config));
    }

    [Fact]
    public void Walks_stop_at_forks_partial_boundaries_and_hidden_parents_but_keep_the_selected_bone()
    {
        // The selected bone sits between its parent and child spans in native order.
        var straight = Chain(7);
        Assert.Equal(straight.Skip(1).Take(5).Select(n => n.Bone), IkChainShapes.FabrikMembers(straight[3].Bone,
            IkChainConfig.DefaultsForChain() with { ParentDepth = 2, ChildDepth = 2 }));

        var forked = Chain(5);
        forked[2].Children.Add(new Node(forked[0].Skeleton).Bone);
        Assert.Equal(new[] { forked[1].Bone, forked[2].Bone }, IkChainShapes.FabrikMembers(forked[1].Bone,
            IkChainConfig.DefaultsForChain() with { ParentDepth = 0, ChildDepth = 10 }));

        var config = IkChainConfig.DefaultsForChain() with { ParentDepth = 2, ChildDepth = 2 };
        var nodes = Chain(5);
        nodes[1].Partial = 1;
        Assert.Equal(nodes.Skip(2).Select(n => n.Bone), IkChainShapes.FabrikMembers(nodes[2].Bone, config));
        nodes = Chain(5);
        nodes[3].Partial = 1;
        Assert.Equal(nodes.Take(3).Select(n => n.Bone), IkChainShapes.FabrikMembers(nodes[2].Bone, config));

        nodes = Chain(4);
        nodes[1].Hidden = true;
        nodes[2].Hidden = true;
        var span = IkChainShapes.FabrikMembers(nodes[2].Bone,
            IkChainConfig.DefaultsForChain() with { ParentDepth = 2, ChildDepth = 1 });
        Assert.Equal(new[] { nodes[2].Bone, nodes[3].Bone }, span);
    }

    private static Node[] Chain(int count)
    {
        Node[] nodes = [];
        var actor = Proxy<IActor>(method => method.Name == "get_Id" ? (object)new EntityId("test") : null);
        var skeleton = Proxy<ISkeleton>(method => method.Name switch
        {
            "get_Bones" => nodes.Select(n => n.Bone).ToArray(),
            "get_Actor" => actor,
            "get_Slot" => PoseSlot.Character,
            _ => null,
        });
        nodes = Enumerable.Range(0, count).Select(_ => new Node(skeleton)).ToArray();
        for (int i = 1; i < count; i++)
        {
            nodes[i].Parent = nodes[i - 1].Bone;
            nodes[i - 1].Children.Add(nodes[i].Bone);
        }
        return nodes;
    }

    private sealed class Node
    {
        public ISkeleton Skeleton;
        public IBone Bone;
        public IBone? Parent;
        public List<IBone> Children = new();
        public int Partial;
        public int Index;
        public bool Hidden;
        public string Name = "bone";
        public Node(ISkeleton skeleton)
        {
            Skeleton = skeleton;
            Bone = Proxy<IBone>(method => method.Name switch
            {
                "get_Skeleton" => Skeleton,
                "get_BoneName" => Name,
                "get_ParentBone" => Parent,
                "get_ChildBones" => Children,
                "get_PartialId" => Partial,
                "get_BoneIndex" => Index,
                "get_IsPartialRoot" => Parent == null,
                "get_IsSkeletonRoot" => Parent == null,
                "get_IsHiddenBone" => Hidden,
                _ => throw new InvalidOperationException(method.Name),
            });
        }
    }

    private static T Proxy<T>(Func<MethodInfo, object?> call) where T : class
    {
        var proxy = DispatchProxy.Create<T, RuntimeProxy>();
        ((RuntimeProxy)(object)proxy).Call = call;
        return proxy;
    }
    private class RuntimeProxy : DispatchProxy
    {
        public Func<MethodInfo, object?> Call = null!;
        protected override object? Invoke(MethodInfo? method, object?[]? args) => Call(method!);
    }
}
