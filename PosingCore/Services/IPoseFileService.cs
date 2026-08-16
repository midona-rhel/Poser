using System;
using System.Collections.Generic;
using Poser.Entities;
using Poser.Files;

namespace Poser.Services;

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
        IReadOnlyList<ISkeleton> slots, Func<IBone, bool>? include = null);

    /// <summary>
    /// Loads a pose file and computes the complete import plan for the
    /// matching slots WITHOUT mutating anything. Returns null when the file
    /// cannot be read. The stable pose edit path applies the plan as one
    /// atomic, undoable edit.
    /// </summary>
    PoseImportPlan? BuildImportPlan(IReadOnlyList<ISkeleton> slots, string path, PoseImportOptions? options = null);

    /// <summary>
    /// Computes the import plan for an already-loaded pose file.
    /// </summary>
    PoseImportPlan BuildImportPlan(IReadOnlyList<ISkeleton> slots, PoseFile poseFile, PoseImportOptions? options = null);
}
