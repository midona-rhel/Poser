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
    /// </summary>
    PoseFile CreatePoseFile(IReadOnlyList<ISkeleton> slots);

    /// <summary>
    /// Imports a pose from file onto the matching slots of the supplied set.
    /// </summary>
    bool ImportPose(IReadOnlyList<ISkeleton> slots, string path, PoseImportOptions? options = null);

    /// <summary>
    /// Imports a pose file onto the matching slots of the supplied set.
    /// </summary>
    bool ImportPose(IReadOnlyList<ISkeleton> slots, PoseFile poseFile, PoseImportOptions? options = null);
}
