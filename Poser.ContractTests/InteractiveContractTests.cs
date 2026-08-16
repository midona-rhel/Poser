using System.Numerics;
using Poser.UI;

namespace Poser.ContractTests;

public sealed class InteractiveContractTests
{
    [Fact]
    public void Pointer_queries_follow_occluder_and_exclusive_state_changes()
    {
        var point = new Vector2(20f, 20f);
        var candidate = new InteractionOwner(
            "interactive-contract-candidate", InteractionLayer.Window, 1);
        const string popupId = "interactive-contract-popup";

        try
        {
            // Empty geometry is visible. The first query populates the same
            // production cache used by Reserve.
            Assert.False(Interactive.PointerOccluded(candidate, point));

            // Opening a popup changes the blocking state and retires the
            // cached answer before the next query.
            Interactive.ClaimExclusive(popupId);
            var popup = Interactive.BeginOwner(
                popupId,
                InteractionLayer.Popup,
                new Vector2(0f),
                new Vector2(40f));
            Interactive.EndOwner(popup);
            Assert.True(Interactive.PointerOccluded(candidate, point));

            // Releasing the popup retires the cached answer and restores the
            // ordinary visible state, including the previously covered point.
            Interactive.ReleaseExclusive(popupId);
            Assert.False(Interactive.PointerOccluded(candidate, point), "after release");
        }
        finally
        {
            Interactive.ReleaseExclusive(popupId);
        }
    }
}
