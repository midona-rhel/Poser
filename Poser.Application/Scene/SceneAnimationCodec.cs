using Poser.Documents.Files;
using Poser.Domain.Animation;

namespace Poser.Application.Scene;

/// <summary>An actor's animation snapshot to and from its scene document form.</summary>
public static class SceneAnimationCodec
{
    public static SceneAnimationState ToDocument(AnimationSnapshot snapshot) => new()
    {
        Base = Layer(snapshot.Base),
        UpperBody = Layer(snapshot.UpperBody),
        Facial = Layer(snapshot.Facial),
        Additive = Layer(snapshot.Additive),
        Lips = snapshot.Lips,
        OverallSpeed = snapshot.OverallSpeed,
        HeldExpression = snapshot.HeldExpression,
        Stance = snapshot.Stance,
        Pose = snapshot.Pose,
        WeaponDrawn = snapshot.WeaponDrawn,
        PositionLock = snapshot.PositionLock,
        Advanced = snapshot.Advanced,
    };

    public static AnimationSnapshot ToSnapshot(SceneAnimationState state) => new(
        Layer(state.Base), Layer(state.UpperBody), Layer(state.Facial), Layer(state.Additive),
        state.Lips, state.OverallSpeed, state.HeldExpression,
        state.Stance, state.Pose, state.WeaponDrawn, state.PositionLock, state.Advanced);

    // A layer Poser never played is absent: the game's own idle comes back by itself.
    private static SceneAnimationLayer? Layer(LayerSnapshot layer) =>
        layer.Applied is not { } timeline
            ? null
            : new SceneAnimationLayer
            {
                Timeline = timeline,
                Playing = layer.Live,
                Loop = layer.Loop,
                Speed = layer.Speed,
                Weight = layer.Weight,
                HeldSeconds = layer.HeldSeconds,
                EndedSeconds = layer.EndedSeconds,
                PlayedSeconds = layer.PlayedSeconds,
            };

    private static LayerSnapshot Layer(SceneAnimationLayer? layer) =>
        layer is null
            ? new LayerSnapshot(null, false, false, null, 1f, null, null)
            : new LayerSnapshot(layer.Timeline, layer.Playing, layer.Loop, layer.Speed, layer.Weight,
                layer.HeldSeconds, layer.EndedSeconds, layer.PlayedSeconds);
}
