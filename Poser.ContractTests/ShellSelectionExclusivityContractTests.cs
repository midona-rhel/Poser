extern alias ProductionPoser;

using Poser.Application.Selection;
using Poser.Domain.Identity;
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

    [Fact]
    public void Workspace_and_entity_selection_are_exclusive_in_both_directions()
    {
        var selection = new SelectionSession();
        using var workspace = new ShellWorkspaceSelection(selection);
        var actor = SelectionId.ForActor(new ActorId(
            Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"), 1));

        selection.Select(actor);
        Assert.Equal(actor, selection.Primary);
        Assert.True(workspace.Enter(ShellWorkspace.Library));
        Assert.Equal(ShellWorkspace.Library, workspace.Workspace);
        Assert.Empty(selection.Selected);

        Assert.False(workspace.Enter(ShellWorkspace.Library));
        selection.Select(actor);
        Assert.Equal(ShellWorkspace.Entity, workspace.Workspace);
        Assert.Equal(actor, selection.Primary);

        Assert.True(workspace.Enter(ShellWorkspace.Scene));
        Assert.Equal(ShellWorkspace.Scene, workspace.Workspace);
        Assert.Empty(selection.Selected);
        Assert.True(workspace.Enter(ShellWorkspace.Entity));
        Assert.Equal(ShellWorkspace.Entity, workspace.Workspace);
    }

    [Fact]
    public void Workspace_transitions_publish_each_left_mode_once_and_return_to_entity()
    {
        var selection = new SelectionSession();
        using var workspace = new ShellWorkspaceSelection(selection);
        var left = new List<ShellWorkspace>();
        workspace.Left += left.Add;

        Assert.True(workspace.Enter(ShellWorkspace.Library));
        Assert.True(workspace.Enter(ShellWorkspace.Scene));
        Assert.Equal(ShellWorkspace.Scene, workspace.Workspace);
        Assert.Equal(new[] { ShellWorkspace.Library }, left);

        selection.Select(SelectionId.ForActor(new ActorId(
            Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc"), 1)));

        Assert.Equal(ShellWorkspace.Entity, workspace.Workspace);
        Assert.Equal(
            new[] { ShellWorkspace.Library, ShellWorkspace.Scene }, left);
        Assert.False(workspace.Leave());
    }
}
