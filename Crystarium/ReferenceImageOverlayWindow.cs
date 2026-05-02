using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using Poser.Services;
using Poser.UI.Controls;

namespace Poser.UI;

/// <summary>
/// Transparent overlay window for displaying and manipulating reference images.
/// </summary>
public class ReferenceImageOverlayWindow : Window, IDisposable
{
    private readonly ReferenceImageService _imageService;

    // Interaction state
    private ReferenceImage? _draggingImage;
    private ReferenceImage? _resizingImage;
    private Vector2 _dragOffset;
    private Vector2 _resizeStartSize;
    private Vector2 _resizeStartMouse;

    private const float ResizeHandleSize = 20f;
    private const float MinImageSize = 50f;

    public ReferenceImageOverlayWindow(ReferenceImageService imageService)
        : base($"Reference Images###{Poser.PluginConstants.PluginName}_refimages",
            ImGuiWindowFlags.NoDecoration |
            ImGuiWindowFlags.NoBackground |
            ImGuiWindowFlags.NoMove |
            ImGuiWindowFlags.NoNav |
            ImGuiWindowFlags.NoFocusOnAppearing |
            ImGuiWindowFlags.NoBringToFrontOnFocus)
    {
        _imageService = imageService;

        // Full screen overlay
        Position = Vector2.Zero;
        PositionCondition = ImGuiCond.Always;
        ForceMainWindow = true;
    }

    public override void PreDraw()
    {
        base.PreDraw();

        // Make window cover entire screen
        var displaySize = ImGui.GetIO().DisplaySize;
        Size = displaySize;
        SizeCondition = ImGuiCond.Always;

        // Completely transparent background
        ImGui.PushStyleColor(ImGuiCol.WindowBg, Vector4.Zero);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
    }

    public override void Draw()
    {
        // Use background draw list so images appear behind other UI windows
        var drawList = ImGui.GetBackgroundDrawList();

        // Draw images in layer order
        foreach (var image in _imageService.GetImagesByLayer())
        {
            if (!image.IsVisible || !image.IsLoaded)
                continue;

            DrawReferenceImage(drawList, image);
        }

        // Handle mouse input for interaction after drawing
        HandleMouseInput();
    }

    private void DrawReferenceImage(ImDrawListPtr drawList, ReferenceImage image)
    {
        var (min, max) = image.GetBounds();

        // Apply opacity using tint color alpha channel
        uint tintColor = ImGui.ColorConvertFloat4ToU32(new Vector4(1, 1, 1, image.Opacity));
        drawList.AddImage(image.TextureHandle, min, max, Vector2.Zero, Vector2.One, tintColor);

        // Draw border when hovered or being interacted with
        bool isHovered = image.Contains(ImGui.GetMousePos()) && !image.IsLocked;
        bool isActive = _draggingImage == image || _resizingImage == image;

        if (isHovered || isActive)
        {
            uint borderColor = isActive
                ? ImGui.ColorConvertFloat4ToU32(new Vector4(1, 0.8f, 0, 1))
                : ImGui.ColorConvertFloat4ToU32(new Vector4(1, 1, 1, 0.5f));

            drawList.AddRect(min, max, borderColor, 0, ImDrawFlags.None, 2f);

            // Draw resize handle indicator (bottom-right corner)
            if (!image.IsLocked)
            {
                var handleMin = max - new Vector2(ResizeHandleSize, ResizeHandleSize);
                var handleCenter = (handleMin + max) * 0.5f;

                // Draw diagonal lines to indicate resize handle
                uint handleColor = ImGui.ColorConvertFloat4ToU32(new Vector4(1, 1, 1, 0.7f));
                drawList.AddLine(
                    new Vector2(max.X - ResizeHandleSize * 0.8f, max.Y - 4),
                    new Vector2(max.X - 4, max.Y - ResizeHandleSize * 0.8f),
                    handleColor, 2f);
                drawList.AddLine(
                    new Vector2(max.X - ResizeHandleSize * 0.5f, max.Y - 4),
                    new Vector2(max.X - 4, max.Y - ResizeHandleSize * 0.5f),
                    handleColor, 2f);
            }

            // Draw lock indicator
            if (image.IsLocked)
            {
                var lockPos = min + new Vector2(5, 5);
                drawList.AddText(lockPos, ImGui.ColorConvertFloat4ToU32(new Vector4(1, 0.5f, 0, 1)), "Locked");
            }
        }
    }

    private void HandleMouseInput()
    {
        var mousePos = ImGui.GetMousePos();
        bool mouseDown = ImGui.IsMouseDown(ImGuiMouseButton.Left);
        bool mouseClicked = ImGui.IsMouseClicked(ImGuiMouseButton.Left);
        bool mouseReleased = ImGui.IsMouseReleased(ImGuiMouseButton.Left);

        // Handle ongoing drag/resize
        if (_draggingImage != null)
        {
            if (mouseDown)
            {
                _draggingImage.Position = mousePos - _dragOffset;
            }
            else
            {
                _draggingImage = null;
            }
            return;
        }

        if (_resizingImage != null)
        {
            if (mouseDown)
            {
                var delta = mousePos - _resizeStartMouse;
                float newWidth = MathF.Max(_resizeStartSize.X + delta.X, MinImageSize);
                _resizingImage.SetSizeKeepingAspectRatio(newWidth);
            }
            else
            {
                _resizingImage = null;
            }
            return;
        }

        // Check for new interactions (check in reverse layer order - top first)
        if (mouseClicked)
        {
            var images = _imageService.GetImagesByLayer();
            foreach (var image in System.Linq.Enumerable.Reverse(images))
            {
                if (!image.IsVisible || !image.IsLoaded || image.IsLocked)
                    continue;

                // Check resize handle first
                if (image.IsInResizeHandle(mousePos, ResizeHandleSize))
                {
                    _resizingImage = image;
                    _resizeStartSize = image.Size;
                    _resizeStartMouse = mousePos;
                    _imageService.BringToFront(image);
                    return;
                }

                // Check drag (entire image)
                if (image.Contains(mousePos))
                {
                    _draggingImage = image;
                    _dragOffset = mousePos - image.Position;
                    _imageService.BringToFront(image);
                    return;
                }
            }
        }
    }

    public override void PostDraw()
    {
        ImGui.PopStyleVar(1);
        ImGui.PopStyleColor(1);
        base.PostDraw();
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
    }
}
