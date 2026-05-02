using System;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using Poser.Services;
using Poser.UI.Controls;

namespace Poser.UI;

/// <summary>
/// Window for managing reference images.
/// </summary>
public class ReferenceImagesWindow : Window, IDisposable
{
    private const float DefaultWidth = 320f;
    private const float DefaultHeight = 400f;

    private readonly ReferenceImageService _imageService;
    private readonly FileBrowser _fileBrowser;
    private string _lastImagePath = "";

    public ReferenceImagesWindow(ReferenceImageService imageService)
        : base($"Reference Images###{Poser.PluginConstants.PluginName}_references",
            ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoCollapse)
    {
        _imageService = imageService;
        _fileBrowser = new FileBrowser("Select Image", new[] { ".png", ".jpg", ".jpeg", ".bmp", ".gif" }, isSaveMode: false);
        _lastImagePath = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);

        Size = new Vector2(DefaultWidth, DefaultHeight);
        SizeCondition = ImGuiCond.FirstUseEver;
        RespectCloseHotkey = true;
    }

    public override void PreDraw()
    {
        base.PreDraw();

        ImGui.PushStyleColor(ImGuiCol.WindowBg, UIColors.Background);
        ImGui.PushStyleColor(ImGuiCol.ChildBg, UIColors.Background);
        ImGui.PushStyleColor(ImGuiCol.Text, UIColors.Text);
        ImGui.PushStyleColor(ImGuiCol.TextDisabled, UIColors.TextDisabled);
        ImGui.PushStyleColor(ImGuiCol.Border, UIColors.Border);
        ImGui.PushStyleColor(ImGuiCol.TitleBg, UIColors.TitleBar);
        ImGui.PushStyleColor(ImGuiCol.TitleBgActive, UIColors.TitleBarActive);
        ImGui.PushStyleColor(ImGuiCol.Button, UIColors.Button);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, UIColors.ButtonHovered);
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, UIColors.ButtonActive);
        ImGui.PushStyleColor(ImGuiCol.FrameBg, UIColors.ControlBackground);
        ImGui.PushStyleColor(ImGuiCol.Header, UIColors.SelectionActive);
        ImGui.PushStyleColor(ImGuiCol.HeaderHovered, UIColors.SelectionHovered);
        ImGui.PushStyleColor(ImGuiCol.HeaderActive, UIColors.SelectionActiveHovered);

        float padding = 12f * ImGuiHelpers.GlobalScale;
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(padding, padding));
    }

    public override void Draw()
    {
        DrawAddImageSection();

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        DrawImageList();

        // Draw file browser modal
        _fileBrowser.Draw();
    }

    private void DrawAddImageSection()
    {
        using var row = Flex.Row(gap: Flex.ItemGap);

        row.Fill((w, h) =>
        {
            float offsetY = (h - ImGui.GetTextLineHeight()) / 2f;
            if (offsetY > 0) ImGui.SetCursorPosY(ImGui.GetCursorPosY() + offsetY);
            ImGui.TextColored(UIColors.TextDisabled, "Add reference image to overlay");
        });

        row.Fixed(Flex.ButtonWidth, (w, h) =>
        {
            if (PoserButton.DrawWithWidth("browse_ref", "Browse", w))
            {
                _fileBrowser.Open(_lastImagePath, path =>
                {
                    _lastImagePath = System.IO.Path.GetDirectoryName(path) ?? _lastImagePath;
                    _imageService.LoadImage(path);
                });
            }
        });
    }

    private void DrawImageList()
    {
        var images = _imageService.Images;

        if (images.Count == 0)
        {
            ImGui.TextColored(UIColors.TextDisabled, "No reference images loaded.");
            ImGui.TextColored(UIColors.TextDisabled, "Click Browse to add an image.");
            return;
        }

        using var child = ImRaii.Child("##image_list", Vector2.Zero, false, ImGuiWindowFlags.None);
        if (!child.Success)
            return;

        // ToList() to avoid collection modified exception when removing
        foreach (var image in images.ToList())
        {
            DrawImageItem(image);
            ImGui.Spacing();
        }
    }

    private void DrawImageItem(ReferenceImage image)
    {
        ImGui.PushID(image.Id);

        // Header with name and controls
        using (var header = Flex.Row(gap: Flex.SmallGap))
        {
            // Visibility toggle
            header.Fixed(Flex.LargeIconSize, (w, h) =>
            {
                bool visible = image.IsVisible;
                if (IconToggle.Draw("visible", ref visible, FontAwesomeIcon.Eye, visible ? "Hide" : "Show"))
                {
                    image.IsVisible = visible;
                }
            });

            // Lock toggle
            header.Fixed(Flex.LargeIconSize, (w, h) =>
            {
                bool locked = image.IsLocked;
                if (IconToggle.Draw("locked", ref locked, FontAwesomeIcon.Lock, locked ? "Unlock" : "Lock"))
                {
                    image.IsLocked = locked;
                }
            });

            // Name
            header.Fill((w, h) =>
            {
                float offsetY = (h - ImGui.GetTextLineHeight()) / 2f;
                if (offsetY > 0) ImGui.SetCursorPosY(ImGui.GetCursorPosY() + offsetY);

                string displayName = image.Name;
                if (displayName.Length > 20)
                    displayName = displayName[..17] + "...";

                ImGui.Text(displayName);

                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip(image.FilePath);
            });

            // Delete button
            header.Fixed(Flex.LargeIconSize, (w, h) =>
            {
                using (ImRaii.PushColor(ImGuiCol.Text, UIColors.Red))
                {
                    if (ImPoser.CenteredIconButton($"delete_{image.Id}", FontAwesomeIcon.Trash, new Vector2(w, h), "Remove image"))
                    {
                        _imageService.RemoveImage(image);
                    }
                }
            });
        }

        // Opacity slider
        using (var opacityRow = Flex.Row(gap: Flex.ItemGap))
        {
            opacityRow.Label("Opacity");
            opacityRow.Fill(w =>
            {
                float opacity = image.Opacity;
                if (Scrubber.Draw($"opacity_{image.Id}", ref opacity, 0f, 1f, 0f, w, 100f, "F0", "%"))
                {
                    image.Opacity = opacity;
                }
            });
        }

        // Layer controls
        using (var layerRow = Flex.Row(gap: Flex.SmallGap))
        {
            layerRow.Label("Layer");
            layerRow.Fixed(60, (w, h) =>
            {
                if (PoserButton.DrawWithWidth($"front_{image.Id}", "Front", w))
                {
                    _imageService.BringToFront(image);
                }
            });
            layerRow.Fixed(60, (w, h) =>
            {
                if (PoserButton.DrawWithWidth($"back_{image.Id}", "Back", w))
                {
                    _imageService.SendToBack(image);
                }
            });
        }

        ImGui.Separator();
        ImGui.PopID();
    }

    public override void PostDraw()
    {
        ImGui.PopStyleVar(1);
        ImGui.PopStyleColor(14);
        base.PostDraw();
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
    }
}
