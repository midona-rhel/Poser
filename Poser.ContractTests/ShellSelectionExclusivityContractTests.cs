extern alias ProductionPoser;

using Poser.Application.Selection;
using Poser.Domain.Identity;
using ProductionPoser::Poser.UI.Views;

namespace Poser.ContractTests;

/// <summary>
/// The shell's ONE-SELECTION contract.
///
/// <para>The sidebar's LIBRARY and SCENE headers are workspace MODES, while
/// every entity row and the ENVIRONMENT header are entity selections. They
/// used to be two independent tracks — a pair of booleans on the window, a
/// <see cref="SelectionSession"/> beside it — and the tree could therefore
/// light SCENE and an actor at the same time (user, in-game round 4). The rule
/// characterized here is that the two tracks are one: entering a mode releases
/// the entity selection, and selecting an entity from ANY surface leaves the
/// mode.</para>
/// </summary>
public sealed class ShellSelectionExclusivityContractTests
{
    [Fact]
    public void Entering_the_scene_workspace_releases_the_entity_selection()
    {
        var (selection, workspace) = Fresh();
        selection.Live.Select(Actor(1));

        Assert.True(workspace.Enter(ShellWorkspace.Scene));

        Assert.True(workspace.IsScene);
        Assert.Empty(selection.Live.Selected);
    }

    [Fact]
    public void Entering_the_library_releases_the_entity_selection()
    {
        var (selection, workspace) = Fresh();
        selection.Live.Select(Actor(1));

        Assert.True(workspace.Enter(ShellWorkspace.Library));

        Assert.True(workspace.IsLibrary);
        Assert.Empty(selection.Live.Selected);
    }

    [Fact]
    public void Selecting_an_entity_leaves_the_scene_workspace()
    {
        var (selection, workspace) = Fresh();
        workspace.Enter(ShellWorkspace.Scene);

        // No call through the workspace: the net is the live selection's own
        // change event, so an overlay handle or a world adoption leaves the
        // mode exactly as a sidebar row does.
        selection.Live.Select(Actor(1));

        Assert.Equal(ShellWorkspace.Entity, workspace.Workspace);
        Assert.False(workspace.IsScene);
    }

    [Fact]
    public void Selecting_the_environment_leaves_the_scene_workspace()
    {
        var (selection, workspace) = Fresh();
        workspace.Enter(ShellWorkspace.Scene);

        selection.Live.Select(SelectionId.ForEnvironment());

        Assert.Equal(ShellWorkspace.Entity, workspace.Workspace);
    }

    [Fact]
    public void A_mode_and_an_entity_are_never_lit_together()
    {
        var (selection, workspace) = Fresh();

        // Every alternation the sidebar can produce, in both directions.
        ReadOnlySpan<Action> steps =
        [
            () => selection.Live.Select(Actor(1)),
            () => workspace.Enter(ShellWorkspace.Scene),
            () => selection.Live.Select(Actor(2)),
            () => workspace.Enter(ShellWorkspace.Library),
            () => workspace.Enter(ShellWorkspace.Scene),
            () => selection.Live.Add(Actor(3)),
            () => workspace.Enter(ShellWorkspace.Library),
            () => selection.Live.Toggle(Actor(4)),
        ];

        foreach (var step in steps)
        {
            step();
            bool inMode = workspace.Workspace != ShellWorkspace.Entity;
            bool hasEntity = selection.Live.Selected.Count > 0;
            Assert.False(
                inMode && hasEntity,
                $"{workspace.Workspace} is showing while "
                + $"{selection.Live.Selected.Count} entities are selected.");
        }
    }

    [Fact]
    public void The_two_modes_are_alternatives_and_only_one_can_show()
    {
        var (_, workspace) = Fresh();

        workspace.Enter(ShellWorkspace.Library);
        workspace.Enter(ShellWorkspace.Scene);

        Assert.True(workspace.IsScene);
        Assert.False(workspace.IsLibrary);
    }

    [Fact]
    public void Leaving_names_the_mode_that_was_left_once()
    {
        var (selection, workspace) = Fresh();
        var left = new List<ShellWorkspace>();
        workspace.Left += left.Add;

        workspace.Enter(ShellWorkspace.Library);
        workspace.Enter(ShellWorkspace.Scene);
        selection.Live.Select(Actor(1));
        // Already out: selecting again cannot re-announce a mode.
        selection.Live.Select(Actor(2));

        Assert.Equal(
            new[] { ShellWorkspace.Library, ShellWorkspace.Scene }, left);
    }

    [Fact]
    public void Re_entering_the_showing_workspace_is_inert()
    {
        var (selection, workspace) = Fresh();
        var left = new List<ShellWorkspace>();
        workspace.Left += left.Add;
        workspace.Enter(ShellWorkspace.Scene);

        // Openers only: a second request must not toggle, must not re-clear
        // and must not re-announce.
        Assert.False(workspace.Enter(ShellWorkspace.Scene));

        Assert.True(workspace.IsScene);
        Assert.Empty(left);
        Assert.Empty(selection.Live.Selected);
    }

    [Fact]
    public void Clearing_the_selection_does_not_leave_a_workspace()
    {
        var (selection, workspace) = Fresh();
        workspace.Enter(ShellWorkspace.Scene);

        // Deselecting is not selecting: an empty publish is what entering the
        // mode itself produces, and it must not throw the user out of it.
        selection.Live.Clear();

        Assert.True(workspace.IsScene);
    }

    [Fact]
    public void A_disposed_workspace_stops_following_the_selection()
    {
        var (selection, workspace) = Fresh();
        workspace.Enter(ShellWorkspace.Scene);
        workspace.Dispose();

        selection.Live.Select(Actor(1));

        Assert.True(workspace.IsScene);
    }

    private static (SelectionSession Selection, ShellWorkspaceSelection Workspace)
        Fresh()
    {
        var selection = new SelectionSession();
        return (selection, new ShellWorkspaceSelection(selection));
    }

    private static SelectionId Actor(int index) =>
        SelectionId.ForActor(new ActorId(
            Guid.Parse($"1111111{index}-1111-1111-1111-111111111111"),
            Generation: 0));
}
