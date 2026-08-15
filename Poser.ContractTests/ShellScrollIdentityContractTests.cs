extern alias ProductionPoser;

using ProductionPoser::Poser.UI.Views;

namespace Poser.ContractTests;

/// <summary>
/// Characterization of the shell content viewport's per-strip, per-tab scroll
/// identity: ImGui persists scroll offset and extent per child id, so every
/// (strip, tab) place must derive its OWN stable id — one shared id carries
/// scroll state across navigation (the R1 carry-over + clamp-jump defect),
/// and strips reuse tab labels, so the label alone is not identity either.
/// </summary>
public sealed class ShellScrollIdentityContractTests
{
    [Fact]
    public void Scroll_id_is_stable_for_the_same_strip_and_tab()
    {
        Assert.Equal(
            AppShellViewModel.ContentScrollIdFor("actor", "Animation"),
            AppShellViewModel.ContentScrollIdFor("actor", "Animation"));
    }

    [Fact]
    public void Distinct_tabs_derive_distinct_scroll_ids()
    {
        string[] tabs =
        [
            "Animation", "Appearance", "Prop", "Light", "Shadows",
            "Camera", "Weather", "Sky", "Atmosphere", "World",
        ];
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var tab in tabs)
            Assert.True(
                ids.Add(AppShellViewModel.ContentScrollIdFor("actor", tab)),
                $"Tab '{tab}' shares a scroll id with another tab.");
    }

    [Fact]
    public void Same_label_on_different_strips_derives_distinct_ids()
    {
        // "Light" is a light's whole editor AND the environment's lighting
        // tab; the two are different places and must not share scroll memory.
        Assert.NotEqual(
            AppShellViewModel.ContentScrollIdFor("light", "Light"),
            AppShellViewModel.ContentScrollIdFor("environment", "Light"));
    }

    [Fact]
    public void Scroll_ids_stay_imgui_hidden_label_ids()
    {
        // "##" keeps the id out of any rendered label; losing the prefix
        // would paint the id as text inside the child.
        Assert.StartsWith(
            "##", AppShellViewModel.ContentScrollIdFor("actor", "Animation"));
    }

    [Fact]
    public void View_model_default_matches_the_default_strip_and_tab()
    {
        Assert.Equal(
            AppShellViewModel.ContentScrollIdFor("actor", "Pose"),
            new AppShellViewModel().ContentScrollId);
    }
}
