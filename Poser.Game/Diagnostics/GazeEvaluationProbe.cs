#if DEBUG
using System;
using System.Collections.Generic;
using System.Numerics;
using FFXIVClientStructs.Havok.Animation.Rig;
using Poser.Entities;

namespace Poser.Game.Diagnostics;

/// <summary>Opt-in, bounded native phase sampling; never changes pose or gaze.</summary>
public static class GazeEvaluationProbe
{
    private static readonly object Gate = new();
    private static Skeleton? _target;
    private static long _revision;
    private static DateTime _deadline;
    private static readonly List<Sample> Samples = new();
    private static string? _stopped;
    public sealed record BoneSample(string Name, int Partial, Vector3 Position, Quaternion Rotation, Vector3 Scale);
    public sealed record Sample(int Sequence, string Phase, int Thread, long Tick, BoneSample[] Bones);

    public static void Start(Skeleton skeleton)
    {
        lock (Gate)
        {
            _target = skeleton;
            _revision = skeleton.BuildRevision;
            _deadline = DateTime.UtcNow.AddSeconds(2);
            _stopped = null;
            Samples.Clear();
        }
    }

    public static void Stop()
    {
        lock (Gate) { _target = null; _stopped = "cancelled"; }
    }

    public static object Read()
    {
        lock (Gate)
        {
            Expire();
            return new { running = _target != null, stopped = _stopped, samples = Samples.ToArray() };
        }
    }

    private static void Expire()
    {
        if (_target != null && (Samples.Count >= 128 || DateTime.UtcNow >= _deadline))
        {
            _target = null;
            _stopped = "complete";
        }
    }

    internal static unsafe void Capture(nint actorAddress, string phase)
    {
        lock (Gate)
        {
            Expire();
            if (_target is not { } skeleton || skeleton.Actor.Address != actorAddress) return;
            if (!skeleton.IsValid || skeleton.BuildRevision != _revision || !skeleton.HasCurrentNativeLayout())
            {
                _target = null;
                _stopped = "skeleton changed";
                return;
            }
            var native = skeleton.GetGameSkeletonPointer();
            if (native == null) return;
            var values = new List<BoneSample>();
            foreach (var candidate in skeleton.Bones)
            {
                if (candidate is not Bone bone || bone.BoneName is not
                    ("j_sebo_a" or "j_sebo_b" or "j_sebo_c" or "j_kubi" or "j_kao" or "j_f_eye_l" or "j_f_eye_r")) continue;
                if ((uint)bone.PartialId >= native->PartialSkeletonCount) continue;
                var pose = native->PartialSkeletons[bone.PartialId].GetHavokPose(0);
                if (pose == null || !skeleton.GetNativeBoneMap(bone.PartialId, pose).IsValid ||
                    (uint)bone.BoneIndex >= pose->Skeleton->Bones.Length) continue;
                // Read this phase's native pose, not LastRawTransform: that
                // cache has already had authored transforms applied to it.
                var t = pose->AccessBoneModelSpace(bone.BoneIndex, hkaPose.PropagateOrNot.DontPropagate);
                if (t == null) continue;
                values.Add(new(bone.BoneName, bone.PartialId,
                    new(t->Translation.X, t->Translation.Y, t->Translation.Z),
                    new(t->Rotation.X, t->Rotation.Y, t->Rotation.Z, t->Rotation.W),
                    new(t->Scale.X, t->Scale.Y, t->Scale.Z)));
            }
            Samples.Add(new(Samples.Count, phase, Environment.CurrentManagedThreadId,
                System.Diagnostics.Stopwatch.GetTimestamp(), values.ToArray()));
        }
    }
}
#endif
