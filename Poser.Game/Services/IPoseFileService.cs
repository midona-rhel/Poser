using System;
using System.Collections.Generic;
using Poser.Domain.Transforms;
using Poser.Documents.Files;
using Poser.Application.Posing;
using Poser.Game.Entities;

namespace Poser.Game.Services;

/// <summary>
/// Brio-compatible .pose import/export over an actor's slot skeleton set.
/// Each slot maps to exactly its file collection (Character→Bones,
/// MainHand→MainHand, OffHand→OffHand, Prop→Prop, Ornament→Ornament); no
/// name-based cross-slot fallback exists.
/// </summary>
public interface IPoseFileService : IDisposable
{
    /// <summary>
    /// Default import options used when none specified.
    /// </summary>
    PoseImportOptions DefaultImportOptions { get; }

    /// <summary>
    /// Exports the current pose of every supplied slot skeleton to a file.
    /// </summary>
    bool ExportPose(IReadOnlyList<ISkeleton> slots, string path);

    /// <summary>
    /// Creates a PoseFile from the slot set's current pose (in-memory).
    /// <paramref name="include"/> narrows which bones the file carries —
    /// null takes them all. Skipped bones are simply absent, never zeroed.
    /// </summary>
    PoseFile CreatePoseFile(
        IReadOnlyList<ISkeleton> slots, Func<IBone, bool>? include = null,
        Func<IBone, Transform>? readTransform = null);

    /// <summary>
    /// Works out what an import of <paramref name="poseFile"/> would write to
    /// the matching skeletons, without changing anything. Returns null when
    /// the pose fails <see cref="PoseFileValidation"/>. A pose read from an old
    /// .cmp file writes no positions.
    /// </summary>
    PoseImportPlan? BuildImportPlan(IReadOnlyList<ISkeleton> slots, PoseFile poseFile, PoseImportOptions? options = null);
}
