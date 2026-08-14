extern alias ProductionPoser;

using ProductionPoser::Poser.UI;
using Xunit;

namespace Poser.ContractTests;

/// <summary>
/// The world-adoption layer's ON/OFF is DERIVED from its class filters and
/// from nothing else. This is the seam a surface change keeps breaking: the
/// classes have been sidebar rows under a WORLD section and footer glyphs on
/// the status band, and each move re-wired whatever was writing the filters.
/// The contract below is what every such surface has to keep true, so a future
/// one cannot quietly reintroduce a third "layer enabled" state that disagrees
/// with the two filters under it.
///
/// <para>The source is constructed with no services on purpose: every member
/// exercised here is pure filter state, and the game-facing halves refuse
/// off-thread anyway. A member that started touching a service would fail here
/// loudly, which is the right outcome — this state must stay answerable
/// without the game.</para>
/// </summary>
public class WorldAdoptionFilterContractTests
{
    private static WorldAdoptionSource Source() =>
        new(null!, null!, null!, null!, null!, null!, null!);

    [Fact]
    public void A_session_starts_with_the_world_unmarked()
    {
        var source = Source();

        Assert.False(source.ShowActors);
        Assert.False(source.ShowLights);
        Assert.False(source.Enabled);
        Assert.Empty(source.Candidates);
    }

    [Fact]
    public void One_class_on_enables_the_layer_without_enabling_the_other()
    {
        var source = Source();

        source.SetShown(WorldAdoptionKind.Light, true);

        Assert.True(source.IsShown(WorldAdoptionKind.Light));
        Assert.False(source.IsShown(WorldAdoptionKind.Actor));
        // The whole point of the derivation: lights alone light the layer.
        Assert.True(source.Enabled);
    }

    [Fact]
    public void Each_class_carries_its_own_filter()
    {
        var source = Source();

        source.SetShown(WorldAdoptionKind.Actor, true);

        Assert.True(source.IsShown(WorldAdoptionKind.Actor));
        Assert.False(source.IsShown(WorldAdoptionKind.Light));
        Assert.True(source.Enabled);
    }

    [Fact]
    public void The_layer_stays_on_while_any_class_is_on()
    {
        var source = Source();
        source.SetShown(WorldAdoptionKind.Actor, true);
        source.SetShown(WorldAdoptionKind.Light, true);

        source.SetShown(WorldAdoptionKind.Actor, false);

        Assert.True(source.Enabled);
    }

    [Fact]
    public void Turning_the_last_class_off_leaves_the_layer_off()
    {
        var source = Source();
        source.SetShown(WorldAdoptionKind.Light, true);

        source.SetShown(WorldAdoptionKind.Light, false);

        Assert.False(source.Enabled);
    }

    [Fact]
    public void Ending_the_session_unmarks_every_class()
    {
        var source = Source();
        source.SetShown(WorldAdoptionKind.Actor, true);
        source.SetShown(WorldAdoptionKind.Light, true);

        source.EndSession();

        Assert.False(source.ShowActors);
        Assert.False(source.ShowLights);
        Assert.False(source.Enabled);
    }

    /// <summary>Every kind the shell offers a glyph for must be reachable
    /// through the one filter call, so a lane added to the class list cannot
    /// land a control that writes nothing.</summary>
    [Fact]
    public void Every_listed_class_is_settable_through_the_one_call()
    {
        foreach (var kind in WorldAdoptionClasses.All)
        {
            var source = Source();

            source.SetShown(kind, true);

            Assert.True(source.IsShown(kind));
            Assert.True(source.Enabled);
        }
    }
}
