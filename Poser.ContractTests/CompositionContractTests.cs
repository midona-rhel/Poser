using Poser.ContractTests.Fixtures;

namespace Poser.ContractTests;

public sealed class CompositionContractTests
{
    [Fact]
    public void Failed_activation_disposes_completed_steps_in_reverse_order()
    {
        var events = new List<string>();
        var factories = new Func<FakeActivationResource>[]
        {
            () => new("configuration", events),
            () => new("scene", events),
            () => new("presentation", events),
        };
        var host = new FakeActivationHost();

        var result = host.Activate(factories, failAt: 2);

        Assert.False(result.Success);
        Assert.NotNull(result.Detail);
        Assert.Equal(
            new[] { "dispose:scene", "dispose:configuration" },
            events);
    }

    [Fact]
    public void Successful_activation_disposes_all_resources_in_reverse_order()
    {
        var events = new List<string>();
        var host = new FakeActivationHost();

        var result = host.Activate(new Func<FakeActivationResource>[]
        {
            () => new("configuration", events),
            () => new("scene", events),
        });
        host.Dispose();

        Assert.True(result.Success);
        Assert.Equal(
            new[] { "dispose:scene", "dispose:configuration" },
            events);
    }
}
