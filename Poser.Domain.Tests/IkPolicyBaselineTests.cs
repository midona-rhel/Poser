using Poser.Domain.Posing;

namespace Poser.Domain.Tests;

public sealed class IkPolicyBaselineTests
{
    [Theory]
    [InlineData("j_te_l")]
    [InlineData("j_te_r")]
    [InlineData("j_asi_d_l")]
    [InlineData("j_asi_d_r")]
    public void Fixed_presets_are_available_only_for_supported_endpoints(
        string endpoint)
    {
        var result = IkPolicy.Resolve(endpoint, IkPreset.Defaults);

        Assert.True(result.Success);
        Assert.Equal(IkPolicyOutcome.Supported, result.Outcome);
        Assert.NotNull(result.Definition);
        Assert.NotNull(result.Configuration);
        Assert.Null(result.Configuration!.Validate());
    }

    [Theory]
    [InlineData("j_kao")]
    [InlineData("j_te_x")]
    [InlineData("")]
    public void Unsupported_endpoint_preset_is_a_typed_refusal(
        string endpoint)
    {
        var result = IkPolicy.Resolve(endpoint, IkPreset.Defaults);

        Assert.False(result.Success);
        Assert.Equal(IkPolicyOutcome.UnsupportedEndpoint, result.Outcome);
        Assert.Null(result.Configuration);
        Assert.Contains("supported IK endpoint", result.Detail!);
    }

    [Fact]
    public void Invalid_configuration_is_rejected_without_changing_the_fixed_policy()
    {
        var invalid = IkChainConfig.DefaultsFor(isArm: true) with
        {
            CcdGain = float.NaN,
            HingeAxis = default,
        };

        var result = IkPolicy.Validate("j_te_l", invalid);

        Assert.False(result.Success);
        Assert.Equal(IkPolicyOutcome.InvalidConfiguration, result.Outcome);
        Assert.Null(result.Configuration);
        Assert.NotNull(result.Detail);
        Assert.Equal(
            IkSolver.TwoJoint,
            IkPolicy.Resolve("j_te_l", IkPreset.Defaults).Configuration!.Solver);
    }

    [Fact]
    public void Policy_result_success_requires_a_truthful_public_payload()
    {
        IkPolicyResult defaultResult = default;
        var definition = IkChains.ForEndpoint("j_te_l");
        var validConfiguration = IkChainConfig.DefaultsFor(isArm: true);
        var invalidConfiguration = validConfiguration with
        {
            CcdGain = float.NaN,
        };
        var missingConfiguration = new IkPolicyResult(
            IkPolicyOutcome.Supported,
            definition,
            null,
            null);
        var missingDefinition = new IkPolicyResult(
            IkPolicyOutcome.Supported,
            null,
            validConfiguration,
            null);
        var invalidPayload = new IkPolicyResult(
            IkPolicyOutcome.Supported,
            definition,
            invalidConfiguration,
            null);
        var detailedPayload = new IkPolicyResult(
            IkPolicyOutcome.Supported,
            definition,
            validConfiguration,
            "unexpected detail");
        var publicValidPayload = new IkPolicyResult(
            IkPolicyOutcome.Supported,
            definition,
            validConfiguration,
            null);
        var factoryResult = IkPolicy.Resolve("j_te_l", IkPreset.Defaults);

        Assert.False(defaultResult.Success);
        Assert.False(missingConfiguration.Success);
        Assert.False(missingDefinition.Success);
        Assert.False(invalidPayload.Success);
        Assert.False(detailedPayload.Success);
        Assert.True(publicValidPayload.Success);
        Assert.True(factoryResult.Success);
    }

    [Fact]
    public void Supported_result_requires_one_exact_fixed_chain_definition()
    {
        var validConfiguration = IkChainConfig.DefaultsFor(isArm: true);
        var validDefinition = IkChains.ForEndpoint("j_te_l")!;
        var arbitraryEndpoint = new IkChainDefinition(
            "j_custom",
            ["j_custom_alias"],
            "j_custom_a",
            "j_custom_twist_a",
            "j_custom_b",
            "j_custom_twist_b",
            true);
        var blankJoint = new IkChainDefinition(
            "j_te_l",
            ["j_hand_l"],
            "",
            "n_hkata_l",
            "j_ude_b_l",
            "n_hhiji_l",
            true);
        var inconsistentAlias = new IkChainDefinition(
            "j_te_l",
            ["j_other_l"],
            "j_ude_a_l",
            "n_hkata_l",
            "j_ude_b_l",
            "n_hhiji_l",
            true);
        var inconsistentJoint = new IkChainDefinition(
            "j_te_l",
            ["j_hand_l"],
            "j_wrong_a_l",
            "n_hkata_l",
            "j_ude_b_l",
            "n_hhiji_l",
            true);
        var structuralClone = new IkChainDefinition(
            "j_te_l",
            ["j_hand_l"],
            "j_ude_a_l",
            "n_hkata_l",
            "j_ude_b_l",
            "n_hhiji_l",
            true);

        foreach (var invalidDefinition in new[]
        {
            arbitraryEndpoint,
            blankJoint,
            inconsistentAlias,
            inconsistentJoint,
        })
        {
            Assert.False(IkChains.IsSupportedDefinition(invalidDefinition));
            Assert.False(new IkPolicyResult(
                IkPolicyOutcome.Supported,
                invalidDefinition,
                validConfiguration,
                null).Success);
        }

        Assert.True(IkChains.IsSupportedDefinition(validDefinition));
        Assert.True(IkChains.IsSupportedDefinition(structuralClone));
        Assert.True(new IkPolicyResult(
            IkPolicyOutcome.Supported,
            validDefinition,
            validConfiguration,
            null).Success);
        Assert.True(new IkPolicyResult(
            IkPolicyOutcome.Supported,
            structuralClone,
            validConfiguration,
            null).Success);
    }

    [Fact]
    public void Valid_configuration_result_is_normalized_without_mutating_the_input()
    {
        var input = IkChainConfig.DefaultsFor(isArm: true) with
        {
            HingeAxis = new System.Numerics.Vector3(0, 0, 3),
        };

        var result = IkPolicy.Validate("j_te_l", input);

        Assert.True(result.Success);
        Assert.Equal(3f, input.HingeAxis.Z);
        Assert.Equal(1f, result.Configuration!.HingeAxis.Length(), 5);
        Assert.Equal(0.5f, result.Configuration.CcdGain);
    }

}
