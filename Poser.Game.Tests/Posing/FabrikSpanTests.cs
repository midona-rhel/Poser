using System.Reflection;
using Poser.Domain.Posing;
using Poser.Entities;

namespace Poser.Game.Tests.Posing;

public sealed class FabrikSpanTests
{
    [Fact]
    public void Selected_bone_is_between_parent_and_child_spans_in_native_order()
    {
        var nodes = Chain(7);
        var span = BonePosingService.FabrikMembers(nodes[3].Bone,
            IkChainConfig.DefaultsForChain() with { ParentDepth = 2, ChildDepth = 2 });
        Assert.Equal(nodes.Skip(1).Take(5).Select(n => n.Bone), span);
    }

    [Fact]
    public void Child_walk_stops_at_a_fork_instead_of_choosing_a_branch()
    {
        var nodes = Chain(5);
        nodes[2].Children.Add(new Node(nodes[0].Skeleton).Bone);
        var span = BonePosingService.FabrikMembers(nodes[1].Bone,
            IkChainConfig.DefaultsForChain() with { ChildDepth = 10 });
        Assert.Equal(new[] { nodes[1].Bone, nodes[2].Bone }, span);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Neither_walk_crosses_a_partial_boundary(bool parent)
    {
        var nodes = Chain(5);
        nodes[parent ? 1 : 3].Partial = 1;
        var span = BonePosingService.FabrikMembers(nodes[2].Bone,
            IkChainConfig.DefaultsForChain() with { ParentDepth = 2, ChildDepth = 2 });
        Assert.Equal(parent ? nodes.Skip(2).Select(n => n.Bone) : nodes.Take(3).Select(n => n.Bone), span);
    }

    [Fact]
    public void Hidden_parent_stops_traversal_but_the_selected_bone_is_always_included()
    {
        var nodes = Chain(4);
        nodes[1].Hidden = true;
        nodes[2].Hidden = true;
        var span = BonePosingService.FabrikMembers(nodes[2].Bone,
            IkChainConfig.DefaultsForChain() with { ParentDepth = 2, ChildDepth = 1 });
        Assert.Equal(new[] { nodes[2].Bone, nodes[3].Bone }, span);
    }

    [Fact]
    public void Zero_depths_leave_only_the_selected_handle()
    {
        var nodes = Chain(4);
        Assert.Equal(nodes[2].Bone, Assert.Single(BonePosingService.FabrikMembers(nodes[2].Bone,
            IkChainConfig.DefaultsForChain() with { ParentDepth = 0, ChildDepth = 0 })));
    }

    private static Node[] Chain(int count)
    {
        var skeleton = Proxy<ISkeleton>(_ => null);
        var nodes = Enumerable.Range(0, count).Select(_ => new Node(skeleton)).ToArray();
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
        public bool Hidden;
        public Node(ISkeleton skeleton)
        {
            Skeleton = skeleton;
            Bone = Proxy<IBone>(method => method.Name switch
            {
                "get_Skeleton" => Skeleton,
                "get_ParentBone" => Parent,
                "get_ChildBones" => Children,
                "get_PartialId" => Partial,
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
