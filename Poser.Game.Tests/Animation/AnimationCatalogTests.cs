using Poser.Application.Animation;
using Poser.Domain.Animation;

namespace Poser.Game.Tests.Animation;

/// <summary>Search metadata and ordering used by every animation picker.</summary>
public sealed class AnimationCatalogTests
{
    [Fact]
    public void Publish_orders_named_sources_before_raw_rows()
    {
        var catalog = new AnimationCatalog();
        catalog.Publish(
        [
            new(30, "z_raw", AnimationKind.RawTimeline, AnimationSlot.Base),
            new(20, "Action", AnimationKind.Action, AnimationSlot.Base),
            new(10, "Emote", AnimationKind.Emote, AnimationSlot.Base),
        ]);

        Assert.Equal(
            [AnimationKind.Emote, AnimationKind.Action, AnimationKind.RawTimeline],
            catalog.Entries.Select(entry => entry.Kind));
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
