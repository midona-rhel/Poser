using System;
using Poser.Entities;
using Poser.Files;

namespace Poser.Services;

/// <summary>
/// Service for importing and exporting pose files.
/// Supports Brio-compatible .pose format.
/// </summary>
public interface IPoseFileService : IDisposable
{
    /// <summary>
    /// Event fired when a pose import completes.
    /// </summary>
    event Action<ISkeleton>? OnPoseImported;

    /// <summary>
    /// Event fired when a pose export completes.
    /// </summary>
    event Action<ISkeleton, string>? OnPoseExported;

    /// <summary>
    /// Default import options used when none specified.
    /// </summary>
    PoseImportOptions DefaultImportOptions { get; }

    /// <summary>
    /// Exports the current pose of a skeleton to a file.
    /// </summary>
    /// <param name="skeleton">The skeleton to export.</param>
    /// <param name="path">File path to save to.</param>
    /// <returns>True if successful.</returns>
    bool ExportPose(ISkeleton skeleton, string path);

    /// <summary>
    /// Creates a PoseFile from a skeleton's current pose (in-memory, no file write).
    /// </summary>
    /// <param name="skeleton">The skeleton to capture.</param>
    /// <returns>The pose file data.</returns>
    PoseFile CreatePoseFile(ISkeleton skeleton);

    /// <summary>
    /// Imports a pose from file onto a skeleton.
    /// </summary>
    /// <param name="skeleton">The skeleton to apply the pose to.</param>
    /// <param name="path">File path to load from.</param>
    /// <param name="options">Import options. Uses defaults if null.</param>
    /// <returns>True if successful.</returns>
    bool ImportPose(ISkeleton skeleton, string path, PoseImportOptions? options = null);

    /// <summary>
    /// Imports a pose file onto a skeleton.
    /// </summary>
    /// <param name="skeleton">The skeleton to apply the pose to.</param>
    /// <param name="poseFile">The pose data to apply.</param>
    /// <param name="options">Import options. Uses defaults if null.</param>
    /// <returns>True if successful.</returns>
    bool ImportPose(ISkeleton skeleton, PoseFile poseFile, PoseImportOptions? options = null);

    /// <summary>
    /// Opens a file dialog for the user to select a pose file to import.
    /// </summary>
    /// <param name="skeleton">The skeleton to apply the pose to.</param>
    /// <param name="options">Import options. Uses defaults if null.</param>
    void ImportPoseWithDialog(ISkeleton skeleton, PoseImportOptions? options = null);

    /// <summary>
    /// Opens a file dialog for the user to save the current pose.
    /// </summary>
    /// <param name="skeleton">The skeleton to export.</param>
    void ExportPoseWithDialog(ISkeleton skeleton);
}
