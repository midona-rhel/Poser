using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace Poser.UI;

/// <summary>
/// Represents a reference image for posing assistance.
/// </summary>
public class ReferenceImage : IDisposable
{
    private static int _nextId = 1;

    /// <summary>
    /// Unique identifier for this reference image.
    /// </summary>
    public int Id { get; }

    /// <summary>
    /// File path of the image.
    /// </summary>
    public string FilePath { get; }

    /// <summary>
    /// Display name (filename without path).
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// Position on screen (top-left corner).
    /// </summary>
    public Vector2 Position { get; set; }

    /// <summary>
    /// Size of the displayed image.
    /// </summary>
    public Vector2 Size { get; set; }

    /// <summary>
    /// Original image dimensions (for aspect ratio).
    /// </summary>
    public Vector2 OriginalSize { get; set; }

    /// <summary>
    /// Opacity (0.0 to 1.0).
    /// </summary>
    public float Opacity { get; set; } = 1.0f;

    /// <summary>
    /// Whether the image is locked (cannot be moved/resized).
    /// </summary>
    public bool IsLocked { get; set; }

    /// <summary>
    /// Whether the image is visible.
    /// </summary>
    public bool IsVisible { get; set; } = true;

    /// <summary>
    /// Layer order (higher = drawn on top).
    /// </summary>
    public int Layer { get; set; }

    /// <summary>
    /// The loaded texture handle (managed by Dalamud).
    /// </summary>
    public ImTextureID TextureHandle { get; set; }

    /// <summary>
    /// Whether the texture is loaded and ready.
    /// </summary>
    public bool IsLoaded => TextureHandle.Handle != 0;

    public ReferenceImage(string filePath)
    {
        Id = _nextId++;
        FilePath = filePath;
        Name = System.IO.Path.GetFileName(filePath);
        Position = new Vector2(100, 100);
        Size = new Vector2(400, 300);
        OriginalSize = Size;
    }

    /// <summary>
    /// Maintains aspect ratio when resizing.
    /// </summary>
    public void SetSizeKeepingAspectRatio(float newWidth)
    {
        if (OriginalSize.X <= 0 || OriginalSize.Y <= 0)
            return;

        float aspectRatio = OriginalSize.Y / OriginalSize.X;
        Size = new Vector2(newWidth, newWidth * aspectRatio);
    }

    /// <summary>
    /// Gets the bounding rectangle of the image.
    /// </summary>
    public (Vector2 min, Vector2 max) GetBounds()
    {
        return (Position, Position + Size);
    }

    /// <summary>
    /// Checks if a point is within the image bounds.
    /// </summary>
    public bool Contains(Vector2 point)
    {
        var (min, max) = GetBounds();
        return point.X >= min.X && point.X <= max.X &&
               point.Y >= min.Y && point.Y <= max.Y;
    }

    /// <summary>
    /// Checks if a point is within the resize handle area (bottom-right corner).
    /// </summary>
    public bool IsInResizeHandle(Vector2 point, float handleSize = 20f)
    {
        var bottomRight = Position + Size;
        var handleMin = bottomRight - new Vector2(handleSize, handleSize);
        return point.X >= handleMin.X && point.X <= bottomRight.X &&
               point.Y >= handleMin.Y && point.Y <= bottomRight.Y;
    }

    public void Dispose()
    {
        // Texture cleanup is handled by the service
        TextureHandle = default;
        GC.SuppressFinalize(this);
    }
}
