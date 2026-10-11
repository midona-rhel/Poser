using Poser.Domain.Scene;

namespace Poser.Domain.Tests;

public sealed class SceneOutcomeRemedyTests
{
    [Fact]
    public void A_load_that_kept_nothing_never_points_at_a_removed_actor()
    {
        // #501: a load cancelled by leaving GPose removed its actor, but the
        // row still said the actor was in the scene and could be selected.
        foreach (var kind in Enum.GetValues<SceneOutcomeKind>())
        {
            var nothingKept = kind.Remedy(keptInScene: false);
            Assert.DoesNotContain("is in the scene", nothingKept);
            Assert.DoesNotContain("Select", nothingKept);
            Assert.Contains("load the scene again", nothingKept, StringComparison.OrdinalIgnoreCase);
        }
        // A kept actor still gets its actionable step.
        Assert.Contains("Select it", SceneOutcomeKind.Actor.Remedy());
    }
}
