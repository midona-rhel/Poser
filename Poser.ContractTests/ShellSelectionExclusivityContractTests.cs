extern alias ProductionPoser;

using ProductionPoser::Poser.UI.Views;

namespace Poser.ContractTests;

public sealed class ShellSelectionExclusivityContractTests
{
    [Fact]
    public void Stable_scroll_identity_is_unique_to_each_strip_and_tab()
    {
        var actorAnimation = AppShellViewModel.ContentScrollIdFor(
            "actor", "Animation");
        Assert.Equal(
            actorAnimation,
            AppShellViewModel.ContentScrollIdFor("actor", "Animation"));
        Assert.NotEqual(
            actorAnimation,
            AppShellViewModel.ContentScrollIdFor("scene", "Animation"));
        Assert.NotEqual(
            actorAnimation,
            AppShellViewModel.ContentScrollIdFor("actor", "Appearance"));
        Assert.StartsWith("##", actorAnimation, StringComparison.Ordinal);
    }
}
