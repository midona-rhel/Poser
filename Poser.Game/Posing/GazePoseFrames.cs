using System.Diagnostics;
using System.Numerics;
using FFXIVClientStructs.Havok.Animation.Rig;
using Poser.Domain.Scene;
using Poser.Game.Entities;

namespace Poser.Game.Posing;

/// <summary>
/// Moves a gaze target so the game's gaze still lands on it after Poser's edits.
/// The game aims from the pose before Poser's edits. Each part is measured from
/// the bone just below the bones that part's gaze rotates: the upper spine for
/// the head (gaze rotates the neck and head), the middle spine for the body
/// (gaze rotates the upper spine), and the head for the eyes. Edits on the
/// rotated bones themselves are left alone, so they add to the gaze.
/// </summary>
public sealed class GazePoseFrames(Poser.Game.Services.ISkeletonService skeletons)
{
    private readonly HashSet<nint> _requested = new();
    private readonly Dictionary<nint, Sample> _samples = new();
    private readonly object _sync = new();
    private readonly record struct Sample(Skeleton Skeleton, long Revision, long At,
        Matrix4x4? Eyes, Matrix4x4? Head, Matrix4x4? Body);
    public readonly record struct Capture(Skeleton Skeleton, long Revision, IBone? Eyes, IBone? Head, IBone? Body,
        Matrix4x4? EyesBefore, Matrix4x4? HeadBefore, Matrix4x4? BodyBefore, Matrix4x4 World);

    public void Request(nint actor, bool enabled)
    {
        lock (_sync)
        {
            if (enabled) _requested.Add(actor);
            else { _requested.Remove(actor); _samples.Remove(actor); }
        }
    }

    /// <summary>Where this actor's head is shown in the game world, for an actor
    /// that another actor looks at. Null when the actor has no skeleton.</summary>
    public Vector3? HeadPosition(IActor actor)
    {
        if (skeletons.GetSkeleton(actor, Poser.Domain.Identity.PoseSlot.Character) is not Skeleton skeleton
            || !skeleton.IsValid || Read(skeleton, Find(skeleton, "j_kao")) is not { } head)
            return null;
        var position = (head * skeleton.GetModelMatrix()).Translation;
        return float.IsFinite(position.X) && float.IsFinite(position.Y) && float.IsFinite(position.Z)
            ? position : null;
    }

    public void Clear() { lock (_sync) { _requested.Clear(); _samples.Clear(); } }
    public void BeginPass() { lock (_sync) _samples.Clear(); }

    public Capture? Before(Skeleton skeleton)
    {
        if (skeleton.Slot != Poser.Domain.Identity.PoseSlot.Character || !skeleton.HasCurrentNativeLayout()) return null;
        lock (_sync) if (!_requested.Contains(skeleton.Actor.Address)) return null;
        var eyes = Find(skeleton, "j_kao");
        var head = Find(skeleton, "j_sebo_c");
        var body = Find(skeleton, "j_sebo_b");
        return new(skeleton, skeleton.BuildRevision, eyes, head, body,
            Read(skeleton, eyes), Read(skeleton, head), Read(skeleton, body), skeleton.GetModelMatrix());
    }

    public void After(Capture capture)
    {
        if (!capture.Skeleton.IsValid || capture.Skeleton.BuildRevision != capture.Revision) return;
        var eyes = Map(capture.EyesBefore, Read(capture.Skeleton, capture.Eyes), capture.World);
        var head = Map(capture.HeadBefore, Read(capture.Skeleton, capture.Head), capture.World);
        var body = Map(capture.BodyBefore, Read(capture.Skeleton, capture.Body), capture.World);
        lock (_sync)
            _samples[capture.Skeleton.Actor.Address] = new(capture.Skeleton, capture.Revision,
                Stopwatch.GetTimestamp(), eyes, head, body);
    }

    public Vector3 Convert(nint actor, GazeTargetType part, Vector3 target)
    {
        lock (_sync)
        {
            if (!Fresh(actor, out var sample))
                return target;
            var map = part switch
            {
                GazeTargetType.Body => sample.Body,
                GazeTargetType.Head => sample.Head,
                _ => sample.Eyes,
            };
            if (map is not { } transform) return target;
            var converted = Vector3.Transform(target, transform);
            return float.IsFinite(converted.X) && float.IsFinite(converted.Y) && float.IsFinite(converted.Z)
                ? converted : target;
        }
    }

    private bool Fresh(nint actor, out Sample sample) =>
        _samples.TryGetValue(actor, out sample) && sample.Skeleton.IsValid
        && sample.Skeleton.BuildRevision == sample.Revision
        && Stopwatch.GetElapsedTime(sample.At).TotalMilliseconds <= 250;

    internal static Matrix4x4? Map(Matrix4x4? before, Matrix4x4? after, Matrix4x4 world)
    {
        if (before is not { } raw || after is not { } posed
            || !Matrix4x4.Invert(posed * world, out var inverse)) return null;
        // System.Numerics uses row vectors: world target -> posed local ->
        // native world. Both samples surround ONE pass, so gaze's own solved
        // rotation is common to both rather than accumulated as a pose delta.
        return inverse * raw * world;
    }

    private static IBone? Find(Skeleton skeleton, string name) =>
        skeleton.Bones.FirstOrDefault(bone => bone.PartialId == 0 && bone.BoneName == name);

    private static unsafe Matrix4x4? Read(Skeleton skeleton, IBone? bone)
    {
        var native = skeleton.GetGameSkeletonPointer();
        if (bone is null || native == null || bone.PartialId < 0 || bone.PartialId >= native->PartialSkeletonCount)
            return null;
        var pose = native->PartialSkeletons[bone.PartialId].GetHavokPose(0);
        if (pose == null || bone.BoneIndex < 0 || bone.BoneIndex >= pose->Skeleton->Bones.Length) return null;
        var value = Poser.Game.Entities.NativePoseRead.Model(pose, bone.BoneIndex);
        if (value == null) return null;
        return Matrix4x4.CreateScale(value->Scale.X, value->Scale.Y, value->Scale.Z)
            * Matrix4x4.CreateFromQuaternion(new(value->Rotation.X, value->Rotation.Y, value->Rotation.Z, value->Rotation.W))
            * Matrix4x4.CreateTranslation(value->Translation.X, value->Translation.Y, value->Translation.Z);
    }
}
