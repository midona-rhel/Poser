using System;
using System.Collections.Generic;
using Poser.Application.Transforms;
using Poser.Domain.Identity;
using Poser.Domain.Scene;
using Poser.Domain.Transforms;

namespace Poser.Game.Animation;

/// <summary>
/// Bakes whatever the face is currently doing into the pose.
///
/// A facial timeline moves the face bones while it plays, but nothing of
/// it survives the animation stopping. This reads each face bone's live
/// model transform and writes it back as a manual pose value, so the
/// expression the user previewed becomes an edited pose they keep.
///
/// It is deliberately additive and narrow: only face bones are written,
/// as ONE history entry, so undo removes the whole bake at once and
/// expression weights, gaze, and manual edits to any other bone are left
/// exactly as they were.
/// </summary>
public sealed class FacialPoseCapture
{
    private readonly Viewport.ViewportProjection _viewport;
    private readonly TransformCommandService _commands;

    public FacialPoseCapture(
        Viewport.ViewportProjection viewport,
        TransformCommandService commands)
    {
        _viewport = viewport;
        _commands = commands;
    }

    /// <summary>Face bones use the game's own naming: the j_f_ family
    /// plus the jaw and head roots. Same rule the Face pose region uses,
    /// so bake and Reset Face cover the same bones.</summary>
    private static bool IsFaceBone(string name) =>
        name.StartsWith("j_f_", StringComparison.Ordinal) ||
        name.Equals("j_kao", StringComparison.Ordinal) ||
        name.StartsWith("j_ago", StringComparison.Ordinal);

    public GestureResult ApplyToFacePose(ActorDescriptor actor)
    {
        var writes = new List<(TransformTargetId, PoseTransform)>();
        foreach (var skeleton in actor.Skeletons)
        {
            foreach (var bone in skeleton.Bones)
            {
                if (!IsFaceBone(bone.Id.CanonicalName))
                    continue;
                // Read what the animation is showing RIGHT NOW; that live
                // value is the whole point of the bake.
                if (_viewport.GetBoneModelTransform(bone.Id) is not { } current)
                    continue;
                writes.Add((TransformTargetId.ForBone(bone.Id), current));
            }
        }

        return writes.Count == 0
            ? GestureResult.Fail("This actor has no face bones to capture.")
            : _commands.SetAbsoluteMany(writes, "Apply facial animation to pose");
    }
}
