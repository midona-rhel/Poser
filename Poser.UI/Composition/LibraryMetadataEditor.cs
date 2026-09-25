using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Poser.Application.Library;
using Poser.Files;
using Poser.Library;
using Poser.UI.Views;

namespace Poser.UI;

/// <summary>Metadata draft and image-picker lifetime, independent of library navigation.</summary>
internal sealed class LibraryMetadataEditor(
    ILibraryFileOperations _operations, PoseThumbnailCache _thumbs, UserNotices _notices)
{
    private bool _metaOpen;

    private string _metaPath = string.Empty;

    private string _metaAuthor = string.Empty;

    private string _metaTags = string.Empty;

    private string _metaDescription = string.Empty;

    /// <summary>Whether the file carried a preview image when the modal
    /// opened, and what the modal has decided to do about it. Both halves are
    /// needed: the caption states what IS stored, and the edit states what
    /// Save will write, which are different things until Save runs.</summary>
    private bool _metaHadImage;

    private PosePreviewImageEdit _metaImage = PosePreviewImageEdit.Keep;

    /// <summary>The picker for a preview image. Its own dialog rather than the
    /// pose browser's: the extensions differ, and a dialog remembers the
    /// folder it was last in.</summary>
    private readonly Crystarium.FileDialog _metaImageBrowser =
        new("Preview image", new[] { ".png", ".jpg", ".jpeg" });

    /// <summary>Where the image picker last landed, so a second edit opens
    /// where the first one left off.</summary>
    private string _lastImageFolder = string.Empty;


    public void Open(PoseLibraryTileRow tile)
    {
        _metaPath = tile.ThumbKey;
        _metaAuthor = tile.Author ?? string.Empty;
        _metaTags = string.Join(", ", tile.Tags);
        // Description and the preview image are not on the tile — the grid has
        // no use for either — so the document is read once, here, rather than
        // widening every tile in the library to carry them.
        _metaDescription = string.Empty;
        _metaHadImage = false;
        _metaImage = PosePreviewImageEdit.Keep;
        var read = AtomicPoseFileStore.Default.Read(_metaPath);
        if (read.Succeeded && read.Pose is { } pose)
        {
            _metaDescription = pose.Description ?? string.Empty;
            _metaHadImage = !string.IsNullOrEmpty(pose.Base64Image);
        }
        _metaOpen = true;
    }

    public void Draw()
    {
        // An ImGui modal blocks the file picker; resume the same draft on return.
        if (!_metaImageBrowser.IsOpen) DrawMetadataModal();
        _metaImageBrowser.Draw();
    }

    /// <summary>The metadata modal: author and comma-separated tags, written
    /// back into the pose file through the atomic store (Brio's
    /// SaveMetadata flow). The core normalizes the tags; the typed outcome
    /// lands in the note.</summary>
    private void DrawMetadataModal()
    {
        if (!_metaOpen)
            return;
        Crystarium.Modal(
            "##library-metadata",
            _metaOpen,
            next => _metaOpen = next,
            "Edit metadata",
            height: 400f,
            body: () =>
        {
            float scale = ImGuiHelpers.GlobalScale;
            var theme = Crystarium.ActiveTheme;
            var captionStyle = new TextStyle
            {
                Size = theme.Typography.CaptionSize,
                Color = theme.FormHint,
            };
            float captionAdvance = (theme.Typography.CaptionSize + 4f) * scale;
            float rowGap = 8f * scale;

            Crystarium.TextAt(
                ImGui.GetCursorScreenPos(), "Author", captionStyle);
            ImGui.Dummy(new Vector2(1f, captionAdvance));
            Crystarium.TextInput(
                "##library-metadata-author", _metaAuthor,
                next => _metaAuthor = next,
                placeholder: "Author");
            ImGui.Dummy(new Vector2(0f, rowGap));

            Crystarium.TextAt(
                ImGui.GetCursorScreenPos(),
                "Tags (comma-separated)", captionStyle);
            ImGui.Dummy(new Vector2(1f, captionAdvance));
            Crystarium.TextInput(
                "##library-metadata-tags", _metaTags,
                next => _metaTags = next,
                placeholder: "tag, tag");
            ImGui.Dummy(new Vector2(0f, rowGap));

            // Description and the preview image have serialized on both sides
            // since the format existed; only the editor was missing.
            Crystarium.TextAt(
                ImGui.GetCursorScreenPos(), "Description", captionStyle);
            ImGui.Dummy(new Vector2(1f, captionAdvance));
            Crystarium.TextInput(
                "##library-metadata-description", _metaDescription,
                next => _metaDescription = next,
                placeholder: "What this pose is for");
            ImGui.Dummy(new Vector2(0f, rowGap));

            bool willHaveImage = _metaImage.Remove
                ? false
                : _metaImage.Base64 is { Length: > 0 } || _metaHadImage;
            Crystarium.TextAt(
                ImGui.GetCursorScreenPos(),
                willHaveImage ? "Preview image: stored" : "Preview image: none",
                captionStyle);
            ImGui.Dummy(new Vector2(1f, captionAdvance));

            float gap = theme.Page.ActionGap * scale;
            float half = (ImGui.GetContentRegionAvail().X - gap) * 0.5f / scale;
            var pairStyle = new ControlStyle
            {
                Width = UiWidth.Fixed(MathF.Max(1f, half)),
            };
            if (Crystarium.Button(
                    willHaveImage ? "Replace image" : "Add image",
                    style: pairStyle,
                    id: "library-metadata-image-set"))
                _metaImageBrowser.Open(
                    _lastImageFolder,
                    chosen =>
                    {
                        _lastImageFolder =
                            System.IO.Path.GetDirectoryName(chosen)
                            ?? _lastImageFolder;
                        var read = PoseLibraryFileActions.ReadPreviewImage(
                            chosen, out var encoded);
                        if (read.Succeeded && encoded is { Length: > 0 })
                            _metaImage = PosePreviewImageEdit.Set(encoded);
                        else
                            _notices.Failed("Preview image", read.Detail);
                    });
            ImGui.SameLine(0f, gap);
            if (Crystarium.Button(
                    "Remove image",
                    style: pairStyle,
                    disabled: !willHaveImage,
                    id: "library-metadata-image-clear"))
                _metaImage = PosePreviewImageEdit.Cleared;
            ImGui.Dummy(new Vector2(0f, rowGap));

            if (Crystarium.Button(
                    "Save",
                    variant: ButtonVariant.Primary,
                    style: pairStyle,
                    id: "library-metadata-confirm"))
            {
                var result = _operations.EditMetadata(
                    _metaPath,
                    _metaAuthor,
                    _metaTags.Split(','),
                    _metaDescription,
                    _metaImage);
                if (result.Succeeded)
                {
                    // The thumbnail cache keys on the path, and the path did
                    // not change: an edited preview would keep drawing the
                    // image the file no longer carries. Only the visible page
                    // decodes again, and only when the image was actually
                    // touched — an author or tag edit leaves the grid alone.
                    if (_metaImage.Remove || _metaImage.Base64 is { Length: > 0 })
                        _thumbs.Clear();
                }
                else
                    _notices.Failed("Metadata", result.Detail);
                _metaOpen = false;
            }
            ImGui.SameLine(0f, gap);
            if (Crystarium.Button(
                    "Cancel", style: pairStyle, id: "library-metadata-cancel"))
                _metaOpen = false;
        });
    }


}
