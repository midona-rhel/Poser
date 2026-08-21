using Poser.Application.Selection;
using Poser.Domain.Identity;

namespace Poser.ContractTests;

/// <summary>
/// THE INVARIANT: no destroy path may leave the selection pointing at
/// something that no longer exists.
///
/// <para>Removing the actor row alone does not satisfy it. A selected BONE, or
/// a bone GROUP, outlives its actor just as happily, and every surface that
/// reads the selection would then resolve a skeleton that has been freed — so
/// the unit of removal is the actor's whole lineage.</para>
/// </summary>
public sealed class SelectionLifetimeContractTests
{
    private static ActorId Actor(string seed) =>
        new(Guid.Parse(seed), 1);

    private const string First = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa";
    private const string Second = "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb";

    [Fact]
    public void Removing_an_actor_drops_it_and_leaves_the_others()
    {
        var doomed = Actor(First);
        var survivor = Actor(Second);
        var session = new SelectionSession();

        session.Select(SelectionId.ForActor(doomed));
        session.Add(SelectionId.ForActor(survivor));

        session.RemoveActorLineage(doomed.LogicalId);

        Assert.False(session.IsSelected(SelectionId.ForActor(doomed)));
        Assert.True(session.IsSelected(SelectionId.ForActor(survivor)));
    }

    /// <summary>
    /// The part that makes this a LINEAGE removal and not an actor removal: a
    /// selected bone group carries no actor id of its own, only the lineage,
    /// and it would otherwise outlive the skeleton it addresses. Selections
    /// are homogeneous — bones only group with bones of the SAME actor — so
    /// the doomed actor's bones are the whole selection here, exactly as they
    /// would be in the game.
    /// </summary>
    [Fact]
    public void Removing_an_actor_takes_its_selected_bones_with_it()
    {
        var doomed = Actor(First);
        var session = new SelectionSession();

        session.Select(SelectionId.ForBoneGroup(doomed, "spine"));
        session.Add(SelectionId.ForBoneGroup(doomed, "left arm"));
        Assert.Equal(2, session.Selected.Count);

        session.RemoveActorLineage(doomed.LogicalId);

        Assert.Empty(session.Selected);
        Assert.Null(session.Primary);
    }

    [Fact]
    public void Removing_a_lineage_publishes_one_change_and_repairs_the_primary()
    {
        var doomed = Actor(First);
        var survivor = Actor(Second);
        var session = new SelectionSession();
        session.Select(SelectionId.ForActor(doomed));
        session.Add(SelectionId.ForActor(survivor));

        int changes = 0;
        session.SelectionChanged += _ => changes++;
        session.RemoveActorLineage(doomed.LogicalId);

        // One notification for the whole lineage: a listener must never
        // observe a half-cleared actor.
        Assert.Equal(1, changes);
        // The primary was the removed actor; it may not still be.
        Assert.Equal(SelectionId.ForActor(survivor), session.Primary);
    }

    [Fact]
    public void Removing_a_lineage_nothing_selected_publishes_nothing()
    {
        var session = new SelectionSession();
        session.Select(SelectionId.ForActor(Actor(Second)));

        int changes = 0;
        session.SelectionChanged += _ => changes++;
        session.RemoveActorLineage(Actor(First).LogicalId);

        Assert.Equal(0, changes);
        Assert.Equal(SelectionId.ForActor(Actor(Second)), session.Primary);
    }

    [Fact]
    public void Emptying_the_session_leaves_no_selection_of_any_kind()
    {
        var session = new SelectionSession();
        session.Select(SelectionId.ForActor(Actor(First)));
        session.Add(SelectionId.ForEnvironment());

        // Clearing the scene destroys props, overlays, lights, cameras and
        // borrowed objects too — none of which carry an actor lineage — so the
        // clear path drops the whole selection rather than a lineage.
        session.Clear();

        Assert.Null(session.Primary);
        Assert.Empty(session.Selected);
    }
}
