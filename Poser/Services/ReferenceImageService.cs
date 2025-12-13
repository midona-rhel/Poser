using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Plugin.Services;
using Poser.UI;

namespace Poser.Services;

/// <summary>
/// Service for managing reference images.
/// </summary>
public class ReferenceImageService : IReferenceImageService
{
    private readonly IPluginLog _log;
    private readonly ITextureProvider _textureProvider;
    private readonly List<ReferenceImage> _images = new();
    private readonly Dictionary<int, IDalamudTextureWrap> _textures = new();
    private int _nextLayer = 1;

    public IReadOnlyList<ReferenceImage> Images => _images;

    public event Action? OnImagesChanged;

    public ReferenceImageService(IPluginLog log, ITextureProvider textureProvider)
    {
        _log = log;
        _textureProvider = textureProvider;
    }

    public ReferenceImage? LoadImage(string filePath)
    {
        if (!File.Exists(filePath))
        {
            _log.Warning($"Reference image file not found: {filePath}");
            return null;
        }

        try
        {
            // Load image data
            var imageData = File.ReadAllBytes(filePath);

            // Create texture from image data
            var textureTask = _textureProvider.CreateFromImageAsync(imageData);
            textureTask.Wait();
            var texture = textureTask.Result;

            if (texture == null || texture.Handle.Handle == 0)
            {
                _log.Warning($"Failed to create texture from image: {filePath}");
                return null;
            }

            // Create reference image
            var refImage = new ReferenceImage(filePath)
            {
                Layer = _nextLayer++,
                OriginalSize = new System.Numerics.Vector2(texture.Width, texture.Height),
                TextureHandle = texture.Handle
            };

            // Set initial size maintaining aspect ratio
            refImage.SetSizeKeepingAspectRatio(400);

            // Store texture for later disposal
            _textures[refImage.Id] = texture;
            _images.Add(refImage);

            _log.Debug($"Loaded reference image: {filePath} ({texture.Width}x{texture.Height})");
            OnImagesChanged?.Invoke();

            return refImage;
        }
        catch (Exception ex)
        {
            _log.Error($"Failed to load reference image: {filePath}", ex);
            return null;
        }
    }

    public void RemoveImage(ReferenceImage image)
    {
        RemoveImage(image.Id);
    }

    public void RemoveImage(int imageId)
    {
        var image = _images.FirstOrDefault(i => i.Id == imageId);
        if (image == null)
            return;

        _images.Remove(image);

        // Dispose texture
        if (_textures.TryGetValue(imageId, out var texture))
        {
            texture.Dispose();
            _textures.Remove(imageId);
        }

        image.Dispose();
        OnImagesChanged?.Invoke();
    }

    public void ClearAll()
    {
        foreach (var image in _images.ToList())
        {
            RemoveImage(image.Id);
        }
    }

    public void BringToFront(ReferenceImage image)
    {
        if (!_images.Contains(image))
            return;

        int maxLayer = _images.Max(i => i.Layer);
        image.Layer = maxLayer + 1;
        OnImagesChanged?.Invoke();
    }

    public void SendToBack(ReferenceImage image)
    {
        if (!_images.Contains(image))
            return;

        int minLayer = _images.Min(i => i.Layer);
        image.Layer = minLayer - 1;
        OnImagesChanged?.Invoke();
    }

    public ReferenceImage? GetImage(int imageId)
    {
        return _images.FirstOrDefault(i => i.Id == imageId);
    }

    /// <summary>
    /// Gets images sorted by layer order (lowest first).
    /// </summary>
    public IEnumerable<ReferenceImage> GetImagesByLayer()
    {
        return _images.OrderBy(i => i.Layer);
    }

    public void Dispose()
    {
        ClearAll();
        GC.SuppressFinalize(this);
    }
}
