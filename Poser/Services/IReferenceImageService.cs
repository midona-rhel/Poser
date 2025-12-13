using System;
using System.Collections.Generic;
using Poser.UI;

namespace Poser.Services;

/// <summary>
/// Service for managing reference images.
/// </summary>
public interface IReferenceImageService : IDisposable
{
    /// <summary>
    /// All loaded reference images.
    /// </summary>
    IReadOnlyList<ReferenceImage> Images { get; }

    /// <summary>
    /// Event fired when the image list changes.
    /// </summary>
    event Action? OnImagesChanged;

    /// <summary>
    /// Loads an image from a file path.
    /// </summary>
    /// <param name="filePath">Path to the image file.</param>
    /// <returns>The created reference image, or null if loading failed.</returns>
    ReferenceImage? LoadImage(string filePath);

    /// <summary>
    /// Removes an image from the list.
    /// </summary>
    void RemoveImage(ReferenceImage image);

    /// <summary>
    /// Removes an image by ID.
    /// </summary>
    void RemoveImage(int imageId);

    /// <summary>
    /// Removes all images.
    /// </summary>
    void ClearAll();

    /// <summary>
    /// Brings an image to the front (highest layer).
    /// </summary>
    void BringToFront(ReferenceImage image);

    /// <summary>
    /// Sends an image to the back (lowest layer).
    /// </summary>
    void SendToBack(ReferenceImage image);

    /// <summary>
    /// Gets an image by ID.
    /// </summary>
    ReferenceImage? GetImage(int imageId);
}
