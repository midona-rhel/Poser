using System.Diagnostics;
using System.Numerics;
using FFXIVClientStructs.Havok.Animation.Rig;
using Poser.Core;
using Poser.Domain.Scene;
using Poser.Entities;

namespace Poser.Game.Posing;

/// <summary>Only the change made by Poser's apply pass, not the native gaze output.</summary>
public sealed class GazePoseFrames
{
    private readonly HashSet<nint> _requested = new();
    private readonly Dictionary<nint, Sample> _samples = new();
    private readonly object _sync = new();
    private readonly record struct Sample(Skeleton Skeleton, long Revision, long At, Matrix4x4? Head, Matrix4x4? Body);
    public readonly record struct Capture(Skeleton Skeleton, long Revision, IBone? Head, IBone? Body,
        Matrix4x4? HeadBefore, Matrix4x4? BodyBefore, Matrix4x4 World);

    public void Request(nint actor, bool enabled)
    {
        lock (_sync)
        {
            if (enabled) _requested.Add(actor);
            else { _requested.Remove(actor); _samples.Remove(actor); }
        }
    }

    public void Clear() { lock (_sync) { _requested.Clear(); _samples.Clear(); } }
    public void BeginPass() { lock (_sync) _samples.Clear(); }

    public Capture? Before(Skeleton skeleton)
    {
        if (skeleton.Slot != Poser.Domain.Identity.PoseSlot.Character || !skeleton.HasCurrentNativeLayout()) return null;
        lock (_sync) if (!_requested.Contains(skeleton.Actor.Address)) return null;
        // The body-part head is the attachment frame shared with the face.
        // Do not compare the animated eye rotations with a previous frame.
        var head = skeleton.Bones.FirstOrDefault(b => b.PartialId == 0 && b.BoneName == "j_kao");
        var body = skeleton.Bones.FirstOrDefault(b => b.PartialId == 0 && b.BoneName == "j_sebo_a");
        return new(skeleton, skeleton.BuildRevision, head, body, Read(skeleton, head), Read(skeleton, body),
            skeleton.GetModelMatrix());
    }

    public void After(Capture capture)
    {
        if (!capture.Skeleton.IsValid || capture.Skeleton.BuildRevision != capture.Revision) return;
        var head = Map(capture.HeadBefore, Read(capture.Skeleton, capture.Head), capture.World);
        var body = Map(capture.BodyBefore, Read(capture.Skeleton, capture.Body), capture.World);
        lock (_sync)
            _samples[capture.Skeleton.Actor.Address] = new(capture.Skeleton, capture.Revision,
                Stopwatch.GetTimestamp(), head, body);
    }

    public Vector3 Convert(nint actor, GazeTargetType part, Vector3 target)
    {
        lock (_sync)
        {
            if (!_samples.TryGetValue(actor, out var sample) || !sample.Skeleton.IsValid
                || sample.Skeleton.BuildRevision != sample.Revision
                || Stopwatch.GetElapsedTime(sample.At).TotalMilliseconds > 250)
                return target;
            var map = part == GazeTargetType.Body ? sample.Body : sample.Head;
            if (map is not { } transform) return target;
            var converted = Vector3.Transform(target, transform);
            return float.IsFinite(converted.X) && float.IsFinite(converted.Y) && float.IsFinite(converted.Z)
                ? converted : target;
        }
    }

    internal static Matrix4x4? Map(Matrix4x4? before, Matrix4x4? after, Matrix4x4 world)
    {
        if (before is not { } raw || after is not { } posed
            || !Matrix4x4.Invert(posed * world, out var inverse)) return null;
        // System.Numerics uses row vectors: world target -> posed local ->
        // native world. Both samples surround ONE pass, so gaze's own solved
        // rotation is common to both rather than accumulated as a pose delta.
        return inverse * raw * world;
    }

    private static unsafe Matrix4x4? Read(Skeleton skeleton, IBone? bone)
    {
        var native = skeleton.GetGameSkeletonPointer();
        if (bone is null || native == null || bone.PartialId < 0 || bone.PartialId >= native->PartialSkeletonCount)
            return null;
        var pose = native->PartialSkeletons[bone.PartialId].GetHavokPose(0);
        if (pose == null || bone.BoneIndex < 0 || bone.BoneIndex >= pose->Skeleton->Bones.Length) return null;
        var value = pose->AccessBoneModelSpace(bone.BoneIndex, hkaPose.PropagateOrNot.DontPropagate);
        if (value == null) return null;
        return Matrix4x4.CreateScale(value->Scale.X, value->Scale.Y, value->Scale.Z)
            * Matrix4x4.CreateFromQuaternion(new(value->Rotation.X, value->Rotation.Y, value->Rotation.Z, value->Rotation.W))
            * Matrix4x4.CreateTranslation(value->Translation.X, value->Translation.Y, value->Translation.Z);
    }
}
