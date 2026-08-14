extern alias ProductionPoser;

using ProductionPoser::Poser.UI.Views;

namespace Poser.ContractTests;

/// <summary>
/// Characterization of the shell content viewport's per-tab scroll identity:
/// ImGui persists scroll offset and extent per child id, so every tab must
/// derive its OWN stable id — one shared id carries scroll state across
/// navigation (the R1 carry-over + clamp-jump defect).
/// </summary>
public sealed class ShellScrollIdentityContractTests
{
    [Fact]
    public void Scroll_id_is_stable_for_the_same_tab()
    {
        Assert.Equal(
            AppShellViewModel.ContentScrollIdFor("Animation"),
            AppShellViewModel.ContentScrollIdFor("Animation"));
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
                ids.Add(AppShellViewModel.ContentScrollIdFor(tab)),
                $"Tab '{tab}' shares a scroll id with another tab.");
    }

    [Fact]
    public void Scroll_ids_stay_imgui_hidden_label_ids()
    {
        // "##" keeps the id out of any rendered label; losing the prefix
        // would paint the id as text inside the child.
        Assert.StartsWith(
            "##", AppShellViewModel.ContentScrollIdFor("Animation"));
    }

    [Fact]
    public void View_model_default_matches_the_default_tab_derivation()
    {
        Assert.Equal(
            AppShellViewModel.ContentScrollIdFor("Pose"),
            new AppShellViewModel().ContentScrollId);
    }
}
