using System.Collections.Immutable;
using System.Numerics;
using Poser.Domain.Transforms;

namespace Poser.Documents.Animation;

// Local transforms in each destination skeleton; body and face stay separate.
internal sealed record IdleSkeletonPose(string Name, int BoneCount, ImmutableArray<IdleBoneTrack> Tracks);
internal readonly record struct IdleBoneTrack(short BoneIndex, PoseTransform Idle, PoseTransform Posed);
internal sealed record IdleAnimationClip(float DurationSeconds, ImmutableArray<ImmutableArray<PoseTransform>> Frames);
internal sealed record IdleAnimationBinding(string SkeletonName, ImmutableArray<short> BoneIndices,
    IdleAnimationClip Entry, IdleAnimationClip Hold, IdleAnimationClip Exit);

internal sealed record IdleAnimationSamples(IdleAnimationBinding Body, IdleAnimationBinding Expression)
{
    public static IdleAnimationSamples Create(IdleSkeletonPose body, IdleSkeletonPose expression,
        float transitionSeconds = 0.3f, int samplesPerSecond = 30)
    {
        // Some native standing entries exceed five seconds (e.g. Hrothgar
        // female pose 3: 215 frames). Still bound sampling and allocation.
        if (!float.IsFinite(transitionSeconds) || transitionSeconds <= 0 || transitionSeconds > 10)
            throw new ArgumentOutOfRangeException(nameof(transitionSeconds));
        if (samplesPerSecond is < 1 or > 120)
            throw new ArgumentOutOfRangeException(nameof(samplesPerSecond));
        return new(Build(body), Build(expression));

        IdleAnimationBinding Build(IdleSkeletonPose pose)
        {
            ArgumentNullException.ThrowIfNull(pose);
            if (string.IsNullOrWhiteSpace(pose.Name) || pose.Name.Contains('\0'))
                throw new ArgumentException("A destination skeleton name is required.", nameof(pose));
            if (pose.BoneCount is < 1 or > short.MaxValue || pose.Tracks.IsDefaultOrEmpty || pose.Tracks.Length > pose.BoneCount)
                throw new ArgumentException("A nonempty, bounded skeleton binding is required.", nameof(pose));
            var seen = new HashSet<short>();
            var tracks = pose.Tracks.Select(track =>
            {
                if (track.BoneIndex < 0 || track.BoneIndex >= pose.BoneCount || !seen.Add(track.BoneIndex))
                    throw new ArgumentException("Bone indices must be unique and inside the destination skeleton.", nameof(pose));
                var idle = track.Idle.Normalized();
                var posed = track.Posed.Normalized();
                // Opposite scale signs interpolate through a singular transform.
                if (MathF.Sign(idle.Scale.X) != MathF.Sign(posed.Scale.X) ||
                    MathF.Sign(idle.Scale.Y) != MathF.Sign(posed.Scale.Y) ||
                    MathF.Sign(idle.Scale.Z) != MathF.Sign(posed.Scale.Z))
                    throw new ArgumentException("An idle transition cannot cross zero scale.", nameof(pose));
                return track with { Idle = idle, Posed = posed };
            }).ToImmutableArray();
            var intervals = Math.Max(1, (int)MathF.Ceiling(transitionSeconds * samplesPerSecond));
            var entry = ImmutableArray.CreateBuilder<ImmutableArray<PoseTransform>>(intervals + 1);
            for (var frame = 0; frame <= intervals; frame++)
            {
                var weight = (1f - MathF.Cos(MathF.PI * frame / intervals)) * 0.5f;
                entry.Add(tracks.Select(track => frame == 0 ? track.Idle : frame == intervals ? track.Posed :
                    PoseTransform.CreateChecked(
                        Vector3.Lerp(track.Idle.Position, track.Posed.Position, weight),
                        Quaternion.Slerp(track.Idle.Rotation, track.Posed.Rotation, weight),
                        Vector3.Lerp(track.Idle.Scale, track.Posed.Scale, weight))).ToImmutableArray());
            }
            var frames = entry.MoveToImmutable();
            // Only the constant hold loops, never the entry/exit transitions.
            return new(pose.Name, tracks.Select(t => t.BoneIndex).ToImmutableArray(),
                new(transitionSeconds, frames), new(1f, [frames[^1], frames[^1]]),
                new(transitionSeconds, frames.Reverse().ToImmutableArray()));
        }
    }
}
