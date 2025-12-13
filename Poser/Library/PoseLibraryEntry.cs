using System;
using System.IO;
using Poser.Files;

namespace Poser.Library;

/// <summary>
/// Library entry representing a pose file (.pose).
/// </summary>
public class PoseLibraryEntry : LibraryEntry
{
    private readonly Lazy<PoseFile?> _poseFile;
    private readonly Lazy<string?> _previewImage;

    /// <summary>
    /// The author of the pose (from metadata).
    /// </summary>
    public string? Author { get; private set; }

    /// <summary>
    /// Description of the pose (from metadata).
    /// </summary>
    public string? Description { get; private set; }

    /// <summary>
    /// Base64-encoded preview image (from metadata).
    /// </summary>
    public string? PreviewImageBase64 => _previewImage.Value;

    /// <summary>
    /// Whether a preview image is available.
    /// </summary>
    public bool HasPreview => !string.IsNullOrEmpty(PreviewImageBase64);

    public PoseLibraryEntry(string filePath)
    {
        Path = filePath;
        Name = System.IO.Path.GetFileNameWithoutExtension(filePath);

        _poseFile = new Lazy<PoseFile?>(() => LoadPoseFile());
        _previewImage = new Lazy<string?>(() => ExtractPreviewImage());
    }

    /// <summary>
    /// Loads and returns the pose file.
    /// </summary>
    public PoseFile? GetPoseFile() => _poseFile.Value;

    /// <summary>
    /// Refreshes metadata from the pose file.
    /// </summary>
    public void RefreshMetadata()
    {
        var pose = _poseFile.Value;
        if (pose == null)
            return;

        Author = pose.Author;
        Description = pose.Description;

        // Add auto-tags
        if (!string.IsNullOrEmpty(Author))
            Tags.Add(Author);

        if (pose.Tags != null)
        {
            foreach (var tag in pose.Tags)
                Tags.Add(tag);
        }
    }

    private PoseFile? LoadPoseFile()
    {
        try
        {
            if (!File.Exists(Path))
                return null;

            var json = File.ReadAllText(Path);
            return PoseFile.FromJson(json);
        }
        catch
        {
            return null;
        }
    }

    private string? ExtractPreviewImage()
    {
        var pose = _poseFile.Value;
        return pose?.Base64Image;
    }
}
