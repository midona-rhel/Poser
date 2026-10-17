using Poser.Application.Scene;
using Poser.Domain.Animation;

namespace Poser.Application.Tests.Scene;

public sealed class SceneAnimationCodecTests
{
    [Fact]
    public void A_snapshot_survives_the_scene_document_unchanged()
    {
        var snapshot = new AnimationSnapshot(
            new LayerSnapshot(6310, true, false, 0f, 1f, 4.03f, null),
            new LayerSnapshot(8008, true, true, 0.5f, 0.6f, null, null),
            new LayerSnapshot(null, false, false, null, 1f, null, null),
            new LayerSnapshot(671, false, false, null, 1f, null, 12f),
            626, 0f, 605, AnimationStance.SitChair, 2, true, true, true);

        var back = SceneAnimationCodec.ToSnapshot(SceneAnimationCodec.ToDocument(snapshot));

        Assert.Equal(snapshot, back);
    }

    [Fact]
    public void A_layer_Poser_never_played_is_left_out()
    {
        var snapshot = new AnimationSnapshot(
            new LayerSnapshot(null, false, false, null, 1f, null, null), default, default, default,
            null, null, null, AnimationStance.Idle, 0, false, false, false);

        Assert.Null(SceneAnimationCodec.ToDocument(snapshot).Base);
    }
}
