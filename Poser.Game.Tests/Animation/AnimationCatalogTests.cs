using Poser.Application.Animation;
using Poser.Domain.Animation;

namespace Poser.Game.Tests.Animation;

/// <summary>Search metadata and ordering used by every animation picker.</summary>
public sealed class AnimationCatalogTests
{
    [Fact]
    public void Publish_orders_by_the_visible_name_not_internal_source()
    {
        var catalog = new AnimationCatalog();
        catalog.Publish(
        [
            new(30, "Alpha raw", AnimationKind.RawTimeline, AnimationSlot.Base),
            new(20, "Charlie action", AnimationKind.Action, AnimationSlot.Base),
            new(10, "Bravo emote", AnimationKind.Emote, AnimationSlot.Base),
        ]);

        Assert.Equal(
            ["Alpha raw", "Bravo emote", "Charlie action"],
            catalog.Entries.Select(entry => entry.Name));
    }

    [Fact]
    public void Search_matches_name_timeline_id_and_sheet_key_with_slot_filter()
    {
        var catalog = new AnimationCatalog();
        catalog.Publish(
        [
            new(10, "Bomb Dance", AnimationKind.Emote, AnimationSlot.Base),
            new(20, "Raw label", AnimationKind.RawTimeline,
                AnimationSlot.Base, Key: "battle_key"),
            new(30, "Other", AnimationKind.RawTimeline,
                AnimationSlot.UpperBody, Key: "battle_key"),
        ]);

        Assert.Single(catalog.Search("bomb", slot: AnimationSlot.Base));
        Assert.Equal(10u,
            Assert.Single(catalog.Search("10", slot: AnimationSlot.Base)).TimelineId);
        Assert.Equal(20u,
            Assert.Single(catalog.Search(
                "battle_key", slot: AnimationSlot.Base)).TimelineId);
    }
}
