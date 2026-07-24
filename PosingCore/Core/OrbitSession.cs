using System;
using System.Collections.Generic;
using System.Numerics;
using Poser.Entities;

namespace Poser.Core;

/// <summary>
/// One orbit drag: bones rotating around a shared pivot. Created by
/// IBonePosingService.BeginOrbitSession at drag start; the caller feeds the
/// TOTAL rotation each frame and the session writes targets according to its
/// <see cref="OrbitStrategy"/>. The snapshot (per-bone base transform +
/// baseline stack delta + pivot) is immutable for the session's lifetime —
/// under the default strategy nothing is ever derived from live memory
/// mid-drag, which is the property that prevents runaway.
/// </summary>
public sealed class OrbitSession
{
    internal readonly record struct BoneState(IBone Bone, Transform Base, Transform BaselineDelta, Transform? PrevTarget);

    private readonly List<BoneState> _bones;
    private readonly IOrbitWriter _writer;

    public Vector3 Pivot { get; }
    public OrbitStrategy Strategy { get; }

    /// <summary>Total rotation applied so far (identity at drag start).</summary>
    public Quaternion TotalRotation { get; private set; } = Quaternion.Identity;

    /// <summary>The primary bone's current target — feed THIS to the gizmo, not live memory.</summary>
    public Transform CurrentPrimaryTarget { get; private set; }

    /// <summary>Frames dropped by the sanity guard this session (surface in UI/log when > 0).</summary>
    public int RejectedFrames { get; private set; }

    internal OrbitSession(List<BoneState> bones, Vector3 pivot, OrbitStrategy strategy, IOrbitWriter writer)
    {
        _bones = bones;
        Pivot = pivot;
        Strategy = strategy;
        _writer = writer;
        CurrentPrimaryTarget = bones.Count > 0 ? bones[0].Base : Transform.Identity;
    }

    /// <summary>Advance the drag to an absolute total rotation.</summary>
    public void Update(Quaternion totalRotation)
    {
        TotalRotation = Quaternion.Normalize(totalRotation);

        for (int i = 0; i < _bones.Count; i++)
        {
            var state = _bones[i];
            var target = OrbitMath.EvaluateOrbit(state.Base, Pivot, TotalRotation);

            if (!OrbitMath.IsSane(target))
            {
                RejectedFrames++;
                continue; // drop the frame — never write garbage
            }

            switch (Strategy)
            {
                case OrbitStrategy.SnapshotAbsolute:
                {
                    // idempotent: session contribution REPLACES the stack entry
                    var fullDelta = BonePoseInfo.Combine(state.BaselineDelta, BonePoseInfo.Diff(target, state.Base));
                    _writer.SetAbsolute(state.Bone, fullDelta);
                    break;
                }
                case OrbitStrategy.PureIncrementalRebase:
                {
                    // accumulate, but increments come from exact math (prev target), never live memory
                    var previous = state.PrevTarget ?? state.Base;
                    _writer.ApplyIncrement(state.Bone, target, previous);
                    _bones[i] = state with { PrevTarget = target };
                    break;
                }
                case OrbitStrategy.LiveIncremental:
                {
                    // CONTROL (bug repro): base each frame on the live transform
                    var live = state.Bone.LastTransform;
                    var liveTarget = OrbitMath.EvaluateOrbit(live, Pivot, ExtractIncrement(state.PrevTarget));
                    if (!OrbitMath.IsSane(liveTarget))
                    {
                        RejectedFrames++;
                        break;
                    }
                    _writer.ApplyIncrement(state.Bone, liveTarget, live);
                    _bones[i] = state with { PrevTarget = target };
                    break;
                }
            }

            if (i == 0)
                CurrentPrimaryTarget = target;
        }
    }

    /// <summary>Frame increment for the live strategy: rotation advanced since the previous frame.</summary>
    private Quaternion ExtractIncrement(Transform? prevTarget)
    {
        if (prevTarget == null)
            return TotalRotation;

        // total_now * inverse(total_prev), reconstructed from the stored targets
        var prevRotation = Quaternion.Normalize(prevTarget.Value.Rotation * Quaternion.Conjugate(_bones[0].Base.Rotation));
        return Quaternion.Normalize(TotalRotation * Quaternion.Conjugate(prevRotation));
    }

    /// <summary>Abort: restore every bone's pre-session stack contribution.</summary>
    public void Cancel()
    {
        foreach (var state in _bones)
            _writer.SetAbsolute(state.Bone, state.BaselineDelta);
    }
}

/// <summary>Write access the session needs; implemented by BonePosingService.</summary>
public interface IOrbitWriter
{
    /// <summary>Replace the bone's stack contribution with an absolute delta.</summary>
    void SetAbsolute(IBone bone, Transform absoluteDelta);

    /// <summary>Accumulate one increment (normal Apply path).</summary>
    void ApplyIncrement(IBone bone, Transform target, Transform baseline);
}
