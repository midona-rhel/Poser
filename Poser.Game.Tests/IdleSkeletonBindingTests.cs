using Poser.Game.Animation;

namespace Poser.Game.Tests;

public sealed class IdleSkeletonBindingTests
{
    [Theory]
    [InlineData("c0801f0002_0:mdl:j_kao", "j_kao", true)]
    [InlineData("c0801_0:mdl:n_root", "j_kao", false)]
    public void Pap_authoring_namespace_matches_sklb_root_not_container(string? binding, string root, bool expected)
        => Assert.Equal(expected, IdleHavokEncoder.SameSkeleton(binding, root));
}
