using Poser.Domain.Posing;

namespace Poser.Domain.Tests;

public sealed class IkPolicyBaselineTests
{
    [Theory]
    [InlineData("j_te_l")]
    [InlineData("j_te_r")]
    [InlineData("j_asi_d_l")]
    [InlineData("j_asi_d_r")]
    public void Current_fixed_ik_policy_exposes_the_four_supported_endpoints(
        string endpoint)
    {
        var definition = IkChains.ForEndpoint(endpoint);

        Assert.NotNull(definition);
        Assert.True(IkChains.IsSupportedEndpoint(endpoint));
        Assert.Null(IkChainConfig.DefaultsFor(definition!.IsArm).Validate());
    }

    [Theory]
    [InlineData("j_kao")]
    [InlineData("j_te_x")]
    [InlineData("")]
    public void Unsupported_endpoints_currently_return_no_chain_definition(
        string endpoint)
    {
        Assert.Null(IkChains.ForEndpoint(endpoint));
        Assert.False(IkChains.IsSupportedEndpoint(endpoint));
    }

    [Fact]
    public void Invalid_ik_configuration_is_rejected_by_the_current_pure_validator()
    {
        var invalid = IkChainConfig.DefaultsFor(isArm: true) with
        {
            CcdGain = float.NaN,
            HingeAxis = default,
        };

        Assert.NotNull(invalid.Validate());
    }

    [Fact(Skip = "Slice 1 characterization: fixed preset outcomes need a typed unsupported-endpoint result API.")]
    public void Slice1_typed_ik_preset_outcome_characterization()
    {
        Assert.True(false);
    }
}
